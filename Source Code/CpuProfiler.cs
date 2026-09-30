using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using HarmonyLib;

namespace WorldTelemetry;

internal enum CpuCategory
{
    Updates,
    Changes,
    Startups,
    ProtoFlux,
    DynamicBones,
    Particles,
    Audio
}

internal enum CpuHook
{
    Update,
    Change,
    Startup,
    NodeUpdates,
    NodeEvents,
    NodeChanges,
    Bones,
    Audio
}

internal sealed class CpuResult
{
    internal const int Categories = 7;
    internal const int Hooks = 8;

    internal required World World { get; init; }
    internal required int Frames { get; init; }
    internal required double Seconds { get; init; }
    internal required Dictionary<object, long[]> Ticks { get; init; }
    internal required long[] Calls { get; init; }
    internal required long ExpectedUpdates { get; init; }
    internal required long ExpectedChanges { get; init; }
    internal required long ExpectedNodeUpdates { get; init; }
    internal required long ExpectedNodeEvents { get; init; }
    internal required long ExpectedContinuousGroups { get; init; }
    internal required int MissingHooks { get; init; }
    internal required ExtraResult Extra { get; init; }

    internal double MsPerFrame(long ticks) => Frames <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / Frames;

    internal long TotalTicks()
    {
        long total = 0;
        foreach (long[] ticks in Ticks.Values)
            for (int i = 0; i < ticks.Length; i++)
                total += ticks[i];

        return total;
    }

    internal double Coverage()
    {
        long expected = ExpectedUpdates + ExpectedChanges;
        if (expected <= 0)
            return 1;

        long timed = Calls[(int)CpuHook.Update] + Calls[(int)CpuHook.Change];
        return Math.Min(1, timed / (double)expected);
    }

    internal string CoverageLine() =>
        $"updates {Calls[(int)CpuHook.Update]:N0} of {ExpectedUpdates:N0} calls, changes {Calls[(int)CpuHook.Change]:N0} of {ExpectedChanges:N0}, startups {Calls[(int)CpuHook.Startup]:N0}, ProtoFlux updates {Calls[(int)CpuHook.NodeUpdates]:N0} of {ExpectedNodeUpdates:N0} groups, events {Calls[(int)CpuHook.NodeEvents]:N0} of {ExpectedNodeEvents:N0}, changes {Calls[(int)CpuHook.NodeChanges]:N0} (continuous groups {ExpectedContinuousGroups:N0}), dynamic bone stages {Calls[(int)CpuHook.Bones]:N0}, audio {Calls[(int)CpuHook.Audio]:N0}";
}

public static class ProfilerHooks
{
    public static void RunUpdate(IUpdatable updatable)
    {
        if (!CpuProfiler.Tracks(updatable))
        {
            updatable.InternalRunUpdate();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            updatable.InternalRunUpdate();
        }
        finally
        {
            CpuProfiler.Exit(updatable, CpuHook.Update, start, saved);
        }
    }

    public static void RunStartup(IUpdatable updatable)
    {
        if (!CpuProfiler.Tracks(updatable))
        {
            updatable.InternalRunStartup();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            updatable.InternalRunStartup();
        }
        finally
        {
            CpuProfiler.Exit(updatable, CpuHook.Startup, start, saved);
        }
    }

    public static void RunAudioUpdate(IAudioUpdatable updatable)
    {
        if (updatable is not IWorldElement element || !CpuProfiler.Tracks(element))
        {
            updatable.InternalRunAudioUpdate();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            updatable.InternalRunAudioUpdate();
        }
        finally
        {
            CpuProfiler.Exit(updatable, CpuHook.Audio, start, saved);
        }
    }

    public static void RunBonePrepare(DynamicBoneChain chain)
    {
        RunBoneStage(chain, BonesRelay.Prepare);
    }

    public static void RunBoneFinish(DynamicBoneChain chain)
    {
        RunBoneStage(chain, BonesRelay.FinishSimulation);
    }

    private static void RunBoneStage(DynamicBoneChain chain, Action<DynamicBoneChain>? stage)
    {
        if (stage is null)
            return;

        if (!CpuProfiler.Tracks(chain))
        {
            stage(chain);
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            stage(chain);
        }
        finally
        {
            CpuProfiler.Exit(chain, CpuHook.Bones, start, saved);
        }
    }

    public static void RunNodeUpdates(ProtoFluxNodeGroup group)
    {
        if (!CpuProfiler.Tracks(group))
        {
            group.RunNodeUpdates();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            group.RunNodeUpdates();
        }
        finally
        {
            CpuProfiler.Exit(group, CpuHook.NodeUpdates, start, saved);
        }
    }

    public static void RunNodeEvents(ProtoFluxNodeGroup group)
    {
        if (!CpuProfiler.Tracks(group))
        {
            group.RunNodeEvents();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            group.RunNodeEvents();
        }
        finally
        {
            CpuProfiler.Exit(group, CpuHook.NodeEvents, start, saved);
        }
    }

    public static void RunNodeChanges(ProtoFluxNodeGroup group)
    {
        if (!CpuProfiler.Tracks(group))
        {
            group.RunNodeChanges();
            return;
        }
        long start = CpuProfiler.Enter(out long saved);
        try
        {
            group.RunNodeChanges();
        }
        finally
        {
            CpuProfiler.Exit(group, CpuHook.NodeChanges, start, saved);
        }
    }
}

internal sealed class ChangeRelay
{
    private static readonly AccessTools.FieldRef<UpdateManager, Action<IUpdatable>>? ProcessChangeField = TryFieldRef<Action<IUpdatable>>("_processChange");
    private static readonly AccessTools.FieldRef<UpdateManager, int>? ChangeUpdateIndex = TryFieldRef<int>("changeUpdateIndex");
    private static readonly Action<UpdateManager, IUpdatable?>? SetCurrentlyUpdating = TrySetter<Action<UpdateManager, IUpdatable?>>(typeof(UpdateManager), nameof(UpdateManager.CurrentlyUpdating));
    private static readonly Action<World, int>? SetLastChanges = TrySetter<Action<World, int>>(typeof(World), nameof(World.LastChanges));

    private readonly UpdateManager _manager;
    private readonly Action<IUpdatable> _original;
    private readonly Action<IUpdatable> _relay;

    private ChangeRelay(UpdateManager manager, Action<IUpdatable> original)
    {
        _manager = manager;
        _original = original;
        _relay = Run;
    }

    internal static bool Available => ProcessChangeField is not null && ChangeUpdateIndex is not null && SetCurrentlyUpdating is not null && SetLastChanges is not null;

    internal static ChangeRelay? Install(UpdateManager manager)
    {
        if (!Available)
            return null;

        ref Action<IUpdatable> field = ref ProcessChangeField!(manager);
        if (field is null)
            return null;

        var relay = new ChangeRelay(manager, field);
        field = relay._relay;
        return relay;
    }

    internal void Remove()
    {
        ref Action<IUpdatable> field = ref ProcessChangeField!(_manager);
        if (ReferenceEquals(field, _relay))
            field = _original;
    }

    private void Run(IUpdatable updatable)
    {
        World world = _manager.World;
        SetLastChanges!(world, world.LastChanges + 1);
        if (updatable.IsRemoved)
            return;

        SetCurrentlyUpdating!(_manager, updatable);
        int index = ChangeUpdateIndex!(_manager);
        if (!CpuProfiler.Tracks(updatable))
        {
            updatable.InternalRunApplyChanges(index);
        }
        else
        {
            long start = CpuProfiler.Enter(out long saved);
            try
            {
                updatable.InternalRunApplyChanges(index);
            }
            finally
            {
                CpuProfiler.Exit(updatable, CpuHook.Change, start, saved);
            }
        }
        SetCurrentlyUpdating!(_manager, null);
    }

    private static AccessTools.FieldRef<UpdateManager, T>? TryFieldRef<T>(string name)
    {
        try
        {
            return AccessTools.Field(typeof(UpdateManager), name) is null ? null : AccessTools.FieldRefAccess<UpdateManager, T>(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static D? TrySetter<D>(Type type, string property) where D : Delegate
    {
        try
        {
            MethodInfo? setter = AccessTools.PropertySetter(type, property);
            return setter is null ? null : AccessTools.MethodDelegate<D>(setter);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class CpuProfiler
{
    private const string HarmonyId = "local.world-telemetry.profiler";

    private static readonly Harmony Harmony = new(HarmonyId);
    private static readonly object Gate = new();
    private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = BuildReplacements();
    private static readonly (Type Type, string Name)[] Targets =
    [
        (typeof(UpdateManager), nameof(UpdateManager.RunUpdates)),
        (typeof(UpdateManager), nameof(UpdateManager.RunStartups)),
        (typeof(ProtoFluxController), nameof(ProtoFluxController.RunNodeUpdates)),
        (typeof(ProtoFluxController), nameof(ProtoFluxController.RunNodeEvents)),
        (typeof(ProtoFluxController), nameof(ProtoFluxController.RunContinuousChanges)),
        (typeof(ProtoFluxController), nameof(ProtoFluxController.RunDiscreteChanges)),
        (typeof(UpdateManager), nameof(UpdateManager.RunAudioUpdates)),
        (typeof(DynamicBoneChainManager), nameof(DynamicBoneChainManager.Update))
    ];

    [ThreadStatic]
    private static long _child;

    private static volatile bool _active;
    private static World? _target;
    private static ChangeRelay? _changeRelay;
    private static Dictionary<object, long[]> _ticks = new(ReferenceEqualityComparer.Instance);
    private static long[] _calls = new long[CpuResult.Hooks];
    private static long _startTimestamp;
    private static long _deadline;
    private static int _frames;
    private static long _expectedUpdates;
    private static long _expectedChanges;
    private static long _expectedNodeUpdates;
    private static long _expectedNodeEvents;
    private static long _expectedContinuous;
    private static int _missingHooks;
    private static int _replacedInCurrentMethod;
    private static Action<CpuResult>? _done;
    private static Action? _cancelled;
    private static bool _patched;
    private static int _patchMissing;

    internal static bool Running => _active;
    internal static World? Target => _active ? _target : null;

    internal static double SecondsLeft => _active ? Math.Max(0, (_deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency) : 0;

    internal static bool Tracks(IWorldElement element) => _active && element.World == _target;

    internal static bool Tracks(ProtoFluxNodeGroup group) => _active && group.World == _target;

    internal static long Enter(out long saved)
    {
        saved = _child;
        _child = 0;
        return Stopwatch.GetTimestamp();
    }

    internal static void Exit(object key, CpuHook hook, long start, long saved)
    {
        long elapsed = Stopwatch.GetTimestamp() - start;
        long self = Math.Max(0, elapsed - _child);
        _child = saved + elapsed;
        int category = hook switch
        {
            CpuHook.Update => (int)CpuCategory.Updates,
            CpuHook.Change => (int)CpuCategory.Changes,
            CpuHook.Startup => (int)CpuCategory.Startups,
            CpuHook.Bones => (int)CpuCategory.DynamicBones,
            CpuHook.Audio => (int)CpuCategory.Audio,
            _ => (int)CpuCategory.ProtoFlux
        };
        lock (Gate)
        {
            if (!_ticks.TryGetValue(key, out long[]? ticks))
            {
                ticks = new long[CpuResult.Categories];
                _ticks[key] = ticks;
            }
            ticks[category] += self;
            _calls[(int)hook]++;
        }
    }

    internal static void AddTicks(object key, CpuCategory category, long ticks)
    {
        if (!_active || ticks <= 0)
            return;

        lock (Gate)
        {
            if (!_ticks.TryGetValue(key, out long[]? totals))
            {
                totals = new long[CpuResult.Categories];
                _ticks[key] = totals;
            }
            totals[(int)category] += ticks;
        }
    }

    internal static bool Start(World world, int seconds, Action<CpuResult> done, Action cancelled, out string error)
    {
        error = "";
        if (_active)
        {
            error = "A CPU profile is already running.";
            return false;
        }
        lock (Gate)
        {
            _ticks = new Dictionary<object, long[]>(ReferenceEqualityComparer.Instance);
            _calls = new long[CpuResult.Hooks];
        }
        _target = world;
        _frames = 0;
        _expectedUpdates = 0;
        _expectedChanges = 0;
        _expectedNodeUpdates = 0;
        _expectedNodeEvents = 0;
        _expectedContinuous = 0;
        _done = done;
        _cancelled = cancelled;
        try
        {
            _missingHooks = EnsurePatched();
            _missingHooks += ExtraProbes.Start(world);
            _changeRelay = ChangeRelay.Install(world.UpdateManager);
            if (_changeRelay is null)
            {
                _missingHooks++;
                WorldTelemetryMod.LogWarning("Profiler hook missing: the change handler of this world could not be replaced, so change handling is not timed.");
            }
        }
        catch (Exception ex)
        {
            Stop();
            error = "Could not hook the engine for profiling: " + ex.Message;
            WorldTelemetryMod.LogWarning(error + "\n" + ex);
            _target = null;
            _done = null;
            _cancelled = null;
            return false;
        }
        _startTimestamp = Stopwatch.GetTimestamp();
        _deadline = _startTimestamp + (long)(Math.Max(1, seconds) * (double)Stopwatch.Frequency);
        _active = true;
        return true;
    }

    internal static void Tick()
    {
        if (!_active)
            return;

        World? target = _target;
        if (target is null || target.IsDestroyed)
        {
            Cancel();
            return;
        }
        _frames++;
        _expectedUpdates += target.LastCommonUpdates;
        _expectedChanges += target.LastChanges;
        _expectedNodeUpdates += target.LastUpdatedNodeGroups;
        _expectedNodeEvents += target.LastNodeEvents;
        _expectedContinuous += target.LastContinuousChangeNodeGroups;
        ExtraProbes.Sample(target);
        long now = Stopwatch.GetTimestamp();
        if (now < _deadline)
            return;

        double elapsed = (now - _startTimestamp) / (double)Stopwatch.Frequency;
        ExtraResult extra = ExtraProbes.Finish(elapsed);
        _active = false;
        Stop();
        CpuResult result;
        lock (Gate)
        {
            result = new CpuResult
            {
                World = target,
                Frames = _frames,
                Seconds = (now - _startTimestamp) / (double)Stopwatch.Frequency,
                Ticks = _ticks,
                Calls = _calls,
                ExpectedUpdates = _expectedUpdates,
                ExpectedChanges = _expectedChanges,
                ExpectedNodeUpdates = _expectedNodeUpdates,
                ExpectedNodeEvents = _expectedNodeEvents,
                ExpectedContinuousGroups = _expectedContinuous,
                MissingHooks = _missingHooks,
                Extra = extra
            };
            _ticks = new Dictionary<object, long[]>(ReferenceEqualityComparer.Instance);
            _calls = new long[CpuResult.Hooks];
        }
        Action<CpuResult>? done = _done;
        _done = null;
        _cancelled = null;
        _target = null;
        done?.Invoke(result);
    }

    internal static void Cancel()
    {
        bool wasActive = _active;
        Action? cancelled = _cancelled;
        _active = false;
        Stop();
        _target = null;
        _done = null;
        _cancelled = null;
        lock (Gate)
        {
            _ticks = new Dictionary<object, long[]>(ReferenceEqualityComparer.Instance);
            _calls = new long[CpuResult.Hooks];
        }
        if (wasActive)
            cancelled?.Invoke();
    }

    private static void Stop()
    {
        ExtraProbes.Stop();
        try
        {
            _changeRelay?.Remove();
        }
        catch (Exception ex)
        {
            WorldTelemetryMod.LogWarning("Could not restore the change handler: " + ex.Message);
        }
        _changeRelay = null;
    }

    private static int EnsurePatched()
    {
        if (_patched)
            return _patchMissing;

        try
        {
            _patchMissing = Patch();
            _patched = true;
            return _patchMissing;
        }
        catch (Exception)
        {
            Unpatch();
            throw;
        }
    }

    internal static int Patch()
    {
        int missing = 0;
        var transpiler = new HarmonyMethod(typeof(CpuProfiler), nameof(Transpile));
        foreach ((Type type, string name) in Targets)
        {
            MethodInfo? method = AccessTools.Method(type, name);
            if (method is null)
            {
                WorldTelemetryMod.LogWarning($"Profiler hook missing: {type.Name}.{name} was not found in this Resonite build.");
                missing++;
                continue;
            }
            _replacedInCurrentMethod = 0;
            Harmony.Patch(method, transpiler: transpiler);
            if (_replacedInCurrentMethod == 0)
            {
                WorldTelemetryMod.LogWarning($"Profiler hook missing: no dispatch call was found inside {type.Name}.{name}.");
                missing++;
            }
        }
        return missing;
    }

    internal static void Unpatch()
    {
        try
        {
            Harmony.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            WorldTelemetryMod.LogWarning("Could not remove the profiler hooks: " + ex.Message);
        }
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                && instruction.operand is MethodInfo called
                && Replacements.TryGetValue(called, out MethodInfo? replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                _replacedInCurrentMethod++;
            }
            yield return instruction;
        }
    }

    private static Dictionary<MethodInfo, MethodInfo> BuildReplacements()
    {
        var map = new Dictionary<MethodInfo, MethodInfo>();
        Add(typeof(IUpdatable), nameof(IUpdatable.InternalRunUpdate), nameof(ProfilerHooks.RunUpdate));
        Add(typeof(IUpdatable), nameof(IUpdatable.InternalRunStartup), nameof(ProfilerHooks.RunStartup));
        Add(typeof(ProtoFluxNodeGroup), nameof(ProtoFluxNodeGroup.RunNodeUpdates), nameof(ProfilerHooks.RunNodeUpdates));
        Add(typeof(ProtoFluxNodeGroup), nameof(ProtoFluxNodeGroup.RunNodeEvents), nameof(ProfilerHooks.RunNodeEvents));
        Add(typeof(ProtoFluxNodeGroup), nameof(ProtoFluxNodeGroup.RunNodeChanges), nameof(ProfilerHooks.RunNodeChanges));
        Add(typeof(IAudioUpdatable), nameof(IAudioUpdatable.InternalRunAudioUpdate), nameof(ProfilerHooks.RunAudioUpdate));
        Add(typeof(DynamicBoneChain), "Prepare", nameof(ProfilerHooks.RunBonePrepare));
        Add(typeof(DynamicBoneChain), "FinishSimulation", nameof(ProfilerHooks.RunBoneFinish));
        return map;

        void Add(Type type, string original, string hook)
        {
            MethodInfo? source = AccessTools.Method(type, original);
            MethodInfo? target = AccessTools.Method(typeof(ProfilerHooks), hook);
            if (source is not null && target is not null)
                map[source] = target;
        }
    }
}
