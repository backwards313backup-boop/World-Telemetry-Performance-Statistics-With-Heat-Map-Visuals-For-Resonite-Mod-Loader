using System.Diagnostics;
using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.PhotonDust;
using HarmonyLib;

namespace WorldTelemetry;

internal sealed class ExtraResult
{
    internal Dictionary<RefID, long[]> Network { get; init; } = new();
    internal Dictionary<Component, double> ParticleCounts { get; init; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<Component, (int Rebuilds, int Vertices)> MeshRebuilds { get; init; } = new(ReferenceEqualityComparer.Instance);
    internal double BonesManagerMs { get; init; }
    internal double Seconds { get; init; }
    internal bool NetworkHooked { get; init; }
    internal bool BonesHooked { get; init; }
}

internal sealed class BonesRelay
{
    private static readonly AccessTools.FieldRef<DynamicBoneChainManager, Action<int>>? SimulateField = TryField<Action<int>>("_simulate");
    private static readonly AccessTools.FieldRef<DynamicBoneChainManager, DynamicBoneChain[]>? BonesField = TryField<DynamicBoneChain[]>("bones");
    internal static readonly Action<DynamicBoneChain>? RunSimulation = TryMethod("RunSimulation");
    internal static readonly Action<DynamicBoneChain>? Prepare = TryMethod("Prepare");
    internal static readonly Action<DynamicBoneChain>? FinishSimulation = TryMethod("FinishSimulation");

    private readonly DynamicBoneChainManager _manager;
    private readonly Action<int> _original;
    private readonly Action<int> _relay;

    private BonesRelay(DynamicBoneChainManager manager, Action<int> original)
    {
        _manager = manager;
        _original = original;
        _relay = Run;
    }

    internal static BonesRelay? Install(DynamicBoneChainManager? manager)
    {
        if (manager is null || SimulateField is null || BonesField is null || RunSimulation is null)
            return null;

        ref Action<int> field = ref SimulateField(manager);
        if (field is null)
            return null;

        var relay = new BonesRelay(manager, field);
        field = relay._relay;
        return relay;
    }

    internal void Remove()
    {
        ref Action<int> field = ref SimulateField!(_manager);
        if (ReferenceEquals(field, _relay))
            field = _original;
    }

    private void Run(int index)
    {
        DynamicBoneChain chain = BonesField!(_manager)[index];
        if (chain is null || chain.IsRemoved)
            return;

        try
        {
            if (!CpuProfiler.Tracks(chain))
            {
                RunSimulation!(chain);
            }
            else
            {
                long start = CpuProfiler.Enter(out long saved);
                try
                {
                    RunSimulation!(chain);
                }
                finally
                {
                    CpuProfiler.Exit(chain, CpuHook.Bones, start, saved);
                }
            }
        }
        catch (Exception ex)
        {
            UniLog.Log($"Exception running chain.RunSimulation()\n{chain}\n{ex}", stackTrace: true);
            _manager.World.RunSynchronously(() => chain.Enabled = false);
        }
    }

    private static AccessTools.FieldRef<DynamicBoneChainManager, T>? TryField<T>(string name)
    {
        try
        {
            return AccessTools.Field(typeof(DynamicBoneChainManager), name) is null ? null : AccessTools.FieldRefAccess<DynamicBoneChainManager, T>(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Action<DynamicBoneChain>? TryMethod(string name)
    {
        try
        {
            MethodInfo? method = AccessTools.Method(typeof(DynamicBoneChain), name, Type.EmptyTypes);
            return method is null ? null : AccessTools.MethodDelegate<Action<DynamicBoneChain>>(method);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class ExtraProbes
{
    private const string HarmonyId = "local.world-telemetry.network";

    private static readonly Harmony Harmony = new(HarmonyId);
    private static readonly object Gate = new();
    private static readonly PropertyInfo? ManagerProperty = AccessTools.Property(typeof(ParticleSystem), "Manager");
    private static readonly PropertyInfo? NativeProperty = ManagerProperty?.PropertyType.GetProperty("NativeSystem");
    private static readonly PropertyInfo? SimulationTimeProperty = NativeProperty?.PropertyType.GetProperty("LastSimulationTime");
    private static readonly PropertyInfo? CountProperty = NativeProperty?.PropertyType.GetProperty("ParticleCount");

    private static World? _target;
    private static Dictionary<RefID, long[]> _network = new();
    private static BonesRelay? _bones;
    private static bool _networkHooked;
    private static List<(ParticleSystem System, object Native)> _particles = new();
    private static Dictionary<Component, double> _particleSums = new(ReferenceEqualityComparer.Instance);
    private static Dictionary<ParticleSystem, (double Seconds, double Rate, int Samples)> _particleTimes = new(ReferenceEqualityComparer.Instance);
    private static bool _networkPatched;
    private static List<(ProceduralMesh Mesh, int Version)> _meshes = new();
    private static double _bonesMs;
    private static int _frames;

    internal static int Start(World world)
    {
        int missing = 0;
        _target = world;
        lock (Gate)
            _network = new Dictionary<RefID, long[]>();

        _particles = new List<(ParticleSystem, object)>();
        _particleSums = new Dictionary<Component, double>(ReferenceEqualityComparer.Instance);
        _particleTimes = new Dictionary<ParticleSystem, (double, double, int)>(ReferenceEqualityComparer.Instance);
        _meshes = new List<(ProceduralMesh, int)>();
        _bonesMs = 0;
        _frames = 0;
        _networkHooked = PatchNetwork();
        if (!_networkHooked)
            missing++;

        _bones = BonesRelay.Install(world.DynamicBones);
        if (_bones is null)
        {
            missing++;
            WorldTelemetryMod.LogWarning("Profiler hook missing: the dynamic bone simulation could not be replaced, so bone simulation is not timed.");
        }
        world.RunSynchronously(() =>
        {
            if (_target != world)
                return;

            try
            {
                var particles = new List<(ParticleSystem, object)>();
                if (ManagerProperty is not null && NativeProperty is not null)
                    foreach (ParticleSystem system in world.RootSlot.GetComponentsInChildren<ParticleSystem>(system => system.Enabled && system.Slot.IsActive))
                        if (ManagerProperty.GetValue(system) is object manager && NativeProperty.GetValue(manager) is object native)
                            particles.Add((system, native));

                _particles = particles;
                _meshes = world.RootSlot.GetComponentsInChildren<ProceduralMesh>()
                    .Where(mesh => mesh.Asset is not null)
                    .Select(mesh => (mesh, mesh.Asset!.Version))
                    .ToList();
            }
            catch (Exception ex)
            {
                WorldTelemetryMod.LogWarning("Could not list particle systems and procedural meshes: " + ex.Message);
            }
        });
        return missing;
    }

    private static bool PatchNetwork()
    {
        if (_networkPatched)
            return true;

        try
        {
            MethodInfo? collect = AccessTools.Method(typeof(SyncController), nameof(SyncController.CollectDeltaMessages));
            MethodInfo? decode = AccessTools.Method(typeof(SyncController), nameof(SyncController.DecodeDeltaMessage));
            if (collect is null || decode is null)
            {
                WorldTelemetryMod.LogWarning("Network hook missing: SyncController delta methods were not found.");
                return false;
            }
            Harmony.Patch(collect, postfix: new HarmonyMethod(typeof(ExtraProbes), nameof(AfterCollect)));
            Harmony.Patch(decode, postfix: new HarmonyMethod(typeof(ExtraProbes), nameof(AfterDecode)));
            _networkPatched = true;
            return true;
        }
        catch (Exception ex)
        {
            WorldTelemetryMod.LogWarning("Could not hook network sync: " + ex.Message);
            try
            {
                Harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    internal static void Sample(World world)
    {
        _frames++;
        _bonesMs += world.DynamicBones?.TotalUpdateTime ?? 0;
        if (SimulationTimeProperty is null || CountProperty is null)
            return;

        foreach ((ParticleSystem system, object native) in _particles)
        {
            if (system.IsDestroyed)
                continue;

            try
            {
                double seconds = SimulationTimeProperty.GetValue(native) is double time ? time : 0;
                int count = CountProperty.GetValue(native) is int live ? live : 0;
                (double total, double rate, int samples) = _particleTimes.GetValueOrDefault(system);
                _particleTimes[system] = (total + Math.Max(0, seconds), rate + Math.Max(0, system.ParticleSystemFPS), samples + 1);
                _particleSums[system] = _particleSums.GetValueOrDefault(system) + count;
            }
            catch (Exception)
            {
            }
        }
    }

    internal static void Stop()
    {
        try
        {
            _bones?.Remove();
        }
        catch (Exception ex)
        {
            WorldTelemetryMod.LogWarning("Could not restore the dynamic bone simulation: " + ex.Message);
        }
        _bones = null;
        _target = null;
        _particles = new List<(ParticleSystem, object)>();
        _meshes = new List<(ProceduralMesh, int)>();
        _particleTimes = new Dictionary<ParticleSystem, (double, double, int)>(ReferenceEqualityComparer.Instance);
        _particleSums = new Dictionary<Component, double>(ReferenceEqualityComparer.Instance);
    }

    internal static ExtraResult Finish(double seconds)
    {
        var rebuilds = new Dictionary<Component, (int, int)>(ReferenceEqualityComparer.Instance);
        foreach ((ProceduralMesh mesh, int version) in _meshes)
        {
            if (mesh.IsDestroyed || mesh.Asset is null)
                continue;

            int delta = mesh.Asset.Version - version;
            if (delta > 0)
                rebuilds[mesh] = (delta, mesh.Asset.Data?.VertexCount ?? 0);
        }
        var particles = new Dictionary<Component, double>(ReferenceEqualityComparer.Instance);
        foreach ((Component system, double sum) in _particleSums)
            particles[system] = _frames > 0 ? sum / _frames : 0;

        double frameRate = seconds > 0 ? _frames / seconds : 0;
        foreach ((ParticleSystem system, (double total, double rate, int samples)) in _particleTimes)
        {
            if (samples <= 0 || total <= 0)
                continue;

            double averageTime = total / samples;
            double averageRate = rate / samples;
            double stepsPerFrame = averageRate > 0 && frameRate > 0 ? Math.Min(1, averageRate / frameRate) : 1;
            CpuProfiler.AddTicks(system, CpuCategory.Particles, (long)(averageTime * stepsPerFrame * _frames * Stopwatch.Frequency));
        }

        Dictionary<RefID, long[]> network;
        lock (Gate)
        {
            network = _network;
            _network = new Dictionary<RefID, long[]>();
        }
        return new ExtraResult
        {
            Network = network,
            ParticleCounts = particles,
            MeshRebuilds = rebuilds,
            BonesManagerMs = _frames > 0 ? _bonesMs / _frames : 0,
            Seconds = seconds,
            NetworkHooked = _networkHooked,
            BonesHooked = BonesRelay.RunSimulation is not null
        };
    }

    private static void Record(RefID id, int direction, int bytes)
    {
        lock (Gate)
        {
            if (!_network.TryGetValue(id, out long[]? totals))
            {
                totals = new long[4];
                _network[id] = totals;
            }
            totals[direction * 2]++;
            totals[direction * 2 + 1] += bytes;
        }
    }

    public static void AfterCollect(SyncController __instance, DeltaBatch __result)
    {
        if (_target is null || __instance.World != _target || __result is null)
            return;

        try
        {
            int count = __result.DataRecordCount;
            for (int i = 0; i < count; i++)
            {
                DataRecord record = __result.GetDataRecord(i);
                Record(record.targetID, 0, record.length);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void AfterDecode(SyncController __instance, int recordIndex, DeltaBatch batch)
    {
        if (_target is null || __instance.World != _target || batch is null)
            return;

        try
        {
            DataRecord record = batch.GetDataRecord(recordIndex);
            Record(record.targetID, 1, record.length);
        }
        catch (Exception)
        {
        }
    }
}
