using System.Diagnostics;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using Renderite.Shared;

namespace WorldTelemetry;

internal sealed class ObjectEntry
{
    internal required Slot Slot { get; init; }
    internal required RefID Id { get; init; }
    internal required string Name { get; init; }
    internal required string Path { get; init; }
    internal required bool Persistent { get; init; }
    internal required bool IsUser { get; init; }
    internal required bool IsContainer { get; init; }

    internal int Slots;
    internal int Components;
    internal readonly AssetTotals Assets = new();
    internal readonly GpuTotals Gpu = new();
    internal readonly LightTotals Light = new();
    internal readonly PhysicsTotals Physics = new();
    internal readonly ExtraTotals Extra = new();
    internal readonly List<string> Warnings = new();
    internal readonly List<WarningKind> WarningKinds = new();
    internal int FluxNodes;
    internal readonly long[] CpuTicks = new long[CpuResult.Categories];
    internal readonly double[] CpuMs = new double[CpuResult.Categories];
    internal readonly HashSet<IAssetProvider> Providers = new(ReferenceEqualityComparer.Instance);

    internal long SizeBytes => Assets.TotalBytes;
    internal double CpuTotalMs => CpuMs.Sum();
    internal double BonesMs => CpuMs[(int)CpuCategory.DynamicBones];
    internal double ParticlesMs => CpuMs[(int)CpuCategory.Particles];
    internal double AudioMs => CpuMs[(int)CpuCategory.Audio];
    internal double ComponentMs => CpuMs[(int)CpuCategory.Updates] + CpuMs[(int)CpuCategory.Changes] + CpuMs[(int)CpuCategory.Startups];
    internal double FluxMs => CpuMs[(int)CpuCategory.ProtoFlux];
    internal double GpuScore => Gpu.Score;
}

internal readonly record struct LightCost(Light Light, double ValueMs, bool Measured);

internal sealed class SlotNode
{
    internal required Slot Slot { get; init; }
    internal required RefID Id { get; init; }
    internal required string Name { get; init; }
    internal required SlotNode? Parent { get; init; }
    internal required int Depth { get; init; }
    internal required bool Persistent { get; init; }
    internal required bool Active { get; init; }
    internal required bool IsUser { get; init; }

    internal List<SlotNode>? Children;
    internal int OwnComponents;
    internal int OwnFluxNodes;
    internal int FluxNodes;
    internal LightTotals? OwnLight;
    internal LightTotals? Light;
    internal PhysicsTotals? OwnPhysics;
    internal PhysicsTotals? Physics;
    internal ExtraTotals? OwnExtra;
    internal ExtraTotals? Extra;
    internal List<(string Type, long Ticks)>? OwnCpuTypes;
    internal IAsset[] OwnAssets = [];
    internal AssetTotals? OwnSize;
    internal GpuTotals? OwnGpu;
    internal long[]? OwnCpu;
    internal int Slots;
    internal int Components;
    internal AssetTotals Size = null!;
    internal GpuTotals? Gpu;
    internal long[]? Cpu;
    internal HashSet<IAsset>? Set;

    internal bool HasChildren => Children is { Count: > 0 };

    private string? _searchKey;

    internal string SearchKey => _searchKey ??= (Parent is null ? "" : Parent.SearchKey + "/") + Name.ToLowerInvariant();

    internal bool Matches(string query)
    {
        if (query.Length == 0)
            return false;

        string key = SearchKey;
        int at = key.LastIndexOf(query, StringComparison.Ordinal);
        return at >= 0 && at + query.Length > key.Length - Name.Length;
    }

    internal string Path()
    {
        var names = new List<string>();
        for (SlotNode? parent = Parent; parent is not null; parent = parent.Parent)
            names.Add(parent.Name);

        if (names.Count == 0)
            return "World root";

        names.Reverse();
        if (names.Count > 5)
            names = ["...", .. names.Skip(names.Count - 4)];

        return string.Join(" / ", names);
    }
}

internal sealed class Snapshot
{
    internal required World World { get; init; }
    internal required string WorldName { get; init; }
    internal required string SessionName { get; init; }
    internal required DateTime TakenLocal { get; init; }
    internal required long TakenTick { get; init; }
    internal required List<ObjectEntry> Entries { get; init; }
    internal required List<SlotNode> Roots { get; init; }
    internal required List<SlotNode> Nodes { get; init; }
    internal required long UniqueAssetBytes { get; init; }
    internal required int UniqueAssets { get; init; }
    internal required double ScanMs { get; init; }
    internal required int TotalSlots { get; init; }
    internal required int TotalComponents { get; init; }
    internal required double MsPerTick { get; init; }
    internal CpuSummary? Cpu { get; init; }
    internal int MeasuredLights { get; init; }
    internal List<LightCost> Lights { get; init; } = new();
    internal int PhysicsMeasured { get; init; }
    internal ExtraResult? Extra { get; init; }
    internal double NetUnattributedBytesPerSecond { get; init; }
    internal double Fps { get; init; }
    internal double PhysicsScale { get; init; }

    private Dictionary<RefID, SlotNode>? _byId;

    private Dictionary<RefID, ObjectEntry>? _entriesById;

    internal ObjectEntry? EntryFor(RefID id)
    {
        if (_entriesById is null)
        {
            _entriesById = new Dictionary<RefID, ObjectEntry>(Entries.Count);
            foreach (ObjectEntry entry in Entries)
                _entriesById[entry.Id] = entry;
        }
        return _entriesById.TryGetValue(id, out ObjectEntry? found) ? found : null;
    }

    internal SlotNode? Find(RefID id)
    {
        if (_byId is null)
        {
            _byId = new Dictionary<RefID, SlotNode>(Nodes.Count);
            foreach (SlotNode node in Nodes)
                _byId[node.Id] = node;
        }
        return _byId.TryGetValue(id, out SlotNode? found) ? found : null;
    }

    internal double Ms(long[]? ticks, int category) => ticks is null ? 0 : ticks[category] * MsPerTick;

    internal double ComponentMs(long[]? ticks) => Ms(ticks, (int)CpuCategory.Updates) + Ms(ticks, (int)CpuCategory.Changes) + Ms(ticks, (int)CpuCategory.Startups);

    internal double FluxMs(long[]? ticks) => Ms(ticks, (int)CpuCategory.ProtoFlux);

    internal double Ms(long[]? ticks)
    {
        if (ticks is null)
            return 0;

        long sum = 0;
        for (int i = 0; i < ticks.Length; i++)
            sum += ticks[i];

        return sum * MsPerTick;
    }
}

internal sealed class CpuSummary
{
    internal required int Frames { get; init; }
    internal required double Seconds { get; init; }
    internal required double TotalMs { get; init; }
    internal required double AttributedMs { get; init; }
    internal required double[] CategoryMs { get; init; }
    internal required double Coverage { get; init; }
    internal required string CoverageLine { get; init; }
    internal required int MissingHooks { get; init; }
}

internal static class Scanner
{
    private static readonly Regex RichTextTag = new("<[^>]*>", RegexOptions.Compiled);

    internal const string OwnTag = "WorldTelemetry";

    internal sealed class ScanJob
    {
        private const int CheckEvery = 64;

        private readonly World _world;
        private readonly CpuResult? _cpu;
        private readonly IReadOnlyDictionary<Light, LightResult>? _lightResults;
        private readonly IReadOnlyDictionary<RefID, PhysicsResult>? _physicsResults;
        private readonly Dictionary<object, long[]>? _cpuTicks;
        private readonly AssetMeasure _measure = new();
        private readonly List<ObjectEntry> _entries = new();
        private readonly List<SlotNode> _roots = new();
        private readonly List<SlotNode> _nodes = new();
        private readonly List<IAssetRef> _refs = new();
        private readonly HashSet<IAssetProvider> _ownProviders = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<IAsset> _ownAssets = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<(Slot Slot, ObjectEntry? Owner, SlotNode? Parent)> _stack = new();
        private readonly List<(Light Light, SlotNode Node, ObjectEntry Entry)> _lights = new();
        private readonly ExtraResult? _extra;
        private readonly double _extraSeconds;
        private readonly Dictionary<Slot, (double Updates, double In, double Out)> _network;
        private readonly double _netOther;
        private long _attributedTicks;
        private int _totalComponents;
        private long _workTicks;

        internal Snapshot? Result { get; private set; }

        internal ScanJob(World world, CpuResult? cpu, IReadOnlyDictionary<Light, LightResult>? lightResults, IReadOnlyDictionary<RefID, PhysicsResult>? physicsResults)
        {
            long start = Stopwatch.GetTimestamp();
            _world = world;
            _cpu = cpu;
            _lightResults = lightResults;
            _physicsResults = physicsResults;
            _cpuTicks = cpu is null ? null : AttributeProtoFlux(cpu);
            _extra = cpu?.Extra;
            _extraSeconds = Math.Max(0.001, _extra?.Seconds ?? 1);
            _network = ResolveNetwork(world, _extra, out _netOther);
            Slot root = world.RootSlot;
            for (int i = root.ChildrenCount - 1; i >= 0; i--)
                _stack.Push((root[i], null, null));

            _workTicks = Stopwatch.GetTimestamp() - start;
        }

        internal bool Step(double sliceMs)
        {
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(sliceMs * Stopwatch.Frequency / 1000.0);
            int processed = 0;
            while (_stack.Count > 0)
            {
                (Slot slot, ObjectEntry? owner, SlotNode? parent) = _stack.Pop();
                Visit(slot, owner, parent);
                if (++processed % CheckEvery == 0 && Stopwatch.GetTimestamp() - start >= budget)
                {
                    _workTicks += Stopwatch.GetTimestamp() - start;
                    return false;
                }
            }
            Result = Finish(start);
            return true;
        }

        private void Visit(Slot slot, ObjectEntry? owner, SlotNode? parent)
        {
            if (slot is null || slot.IsDestroyed || slot.IsLocalElement || Selection.IsBox(slot))
                return;

            bool objectRoot = IsObjectRoot(slot, out UserRoot? userRoot);
            if (owner is null || (owner.IsContainer && objectRoot))
            {
                owner = CreateEntry(slot, !objectRoot, userRoot);
                _entries.Add(owner);
            }
            var node = new SlotNode
            {
                Slot = slot,
                Id = slot.ReferenceID,
                Name = DisplayName(slot, userRoot),
                Parent = parent,
                Depth = parent is null ? 0 : parent.Depth + 1,
                Persistent = slot.IsPersistent,
                Active = slot.IsActive,
                IsUser = userRoot is not null
            };
            if (parent is null)
                _roots.Add(node);
            else
                (parent.Children ??= new List<SlotNode>()).Add(node);

            _nodes.Add(node);
            owner.Slots++;

            _ownProviders.Clear();
            var slotGpu = new GpuTotals();
            var slotPhysics = new PhysicsTotals();
            var slotExtra = new ExtraTotals();
            if (_network.TryGetValue(slot, out (double Updates, double In, double Out) net))
            {
                slotExtra.NetUpdatesPerSecond += net.Updates;
                slotExtra.NetInBytesPerSecond += net.In;
                slotExtra.NetOutBytesPerSecond += net.Out;
            }
            long[]? slotCpu = null;
            if (_cpuTicks is not null)
                AddCpu(_cpuTicks, slot, ref slotCpu);

            foreach (Component component in slot.Components)
            {
                node.OwnComponents++;
                if (component is IAssetProvider provider)
                    _ownProviders.Add(provider);

                _refs.Clear();
                try
                {
                    component.GetSyncMembers(_refs);
                }
                catch (Exception)
                {
                }
                foreach (IAssetRef reference in _refs)
                    if (reference.Target is IAssetProvider referenced)
                        _ownProviders.Add(referenced);

                if (node.Active)
                {
                    _measure.AddGpu(component, slotGpu);
                    AssetMeasure.AddPhysics(component, slotPhysics);
                    if (component is Light light && light.Enabled)
                        _lights.Add((light, node, owner));
                }
                if (component is ProtoFluxNode)
                    node.OwnFluxNodes++;

                if (node.Active && component.Enabled)
                {
                    if (component is DynamicBoneChain chain)
                    {
                        slotExtra.BoneChains++;
                        slotExtra.Bones += chain.Bones.Count;
                    }
                    else if (component is AudioOutput audio && audio.Source.Target is not null)
                        slotExtra.AudioOutputs++;
                }
                if (_extra is not null)
                {
                    if (_extra.ParticleCounts.TryGetValue(component, out double particles))
                        slotExtra.Particles += particles;

                    if (_extra.MeshRebuilds.TryGetValue(component, out (int Rebuilds, int Vertices) rebuilt))
                    {
                        slotExtra.MeshRebuildsPerSecond += rebuilt.Rebuilds / _extraSeconds;
                        slotExtra.RebuiltVerticesPerSecond += (double)rebuilt.Rebuilds * rebuilt.Vertices / _extraSeconds;
                    }
                }

                if (_cpuTicks is not null)
                {
                    long found = AddCpu(_cpuTicks, component, ref slotCpu);
                    if (found > 0)
                        (node.OwnCpuTypes ??= new List<(string, long)>()).Add((TypeName(component), found));
                }
            }
            owner.FluxNodes += node.OwnFluxNodes;
            if (!slotExtra.IsEmpty)
            {
                node.OwnExtra = slotExtra;
                owner.Extra.Add(slotExtra);
            }
            if (slotPhysics.Colliders > 0)
            {
                node.OwnPhysics = slotPhysics;
                owner.Physics.Add(slotPhysics);
            }
            owner.Components += node.OwnComponents;
            _totalComponents += node.OwnComponents;

            if (_ownProviders.Count > 0)
            {
                _ownAssets.Clear();
                foreach (IAssetProvider provider in _ownProviders)
                {
                    owner.Providers.Add(provider);
                    foreach (IAsset asset in _measure.Closure(provider))
                        _ownAssets.Add(asset);
                }
                if (_ownAssets.Count > 0)
                {
                    node.OwnAssets = _ownAssets.ToArray();
                    node.OwnSize = new AssetTotals();
                    foreach (IAsset asset in node.OwnAssets)
                        node.OwnSize.Add(_measure.Bytes(asset));
                }
            }
            if (!slotGpu.IsEmpty)
            {
                node.OwnGpu = slotGpu;
                owner.Gpu.Add(slotGpu);
            }
            if (slotCpu is not null)
            {
                node.OwnCpu = slotCpu;
                for (int i = 0; i < CpuResult.Categories; i++)
                {
                    owner.CpuTicks[i] += slotCpu[i];
                    _attributedTicks += slotCpu[i];
                }
            }
            for (int i = slot.ChildrenCount - 1; i >= 0; i--)
                _stack.Push((slot[i], owner, node));
        }

        private Snapshot Finish(long stepStart)
        {
            List<LightCost> lightCosts = AddLighting(_lights, _measure.Spheres, _lightResults);
            Summarize(_nodes, _measure);

            var unique = new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
            foreach (ObjectEntry entry in _entries)
            {
                _measure.AddAssets(entry.Providers, entry.Assets, unique);
                entry.Providers.Clear();
                if (_cpu is not null)
                    for (int i = 0; i < CpuResult.Categories; i++)
                        entry.CpuMs[i] = _cpu.MsPerFrame(entry.CpuTicks[i]);
            }
            _entries.RemoveAll(entry => entry.Components == 0 && entry.SizeBytes == 0);
            (int physicsMeasured, double physicsScale) = ApplyPhysics(_entries, _physicsResults);
            foreach (ObjectEntry entry in _entries)
                AddWarnings(entry);

            long uniqueBytes = 0;
            foreach (IAsset asset in unique)
                uniqueBytes += _measure.Bytes(asset).Bytes;

            long work = _workTicks + Stopwatch.GetTimestamp() - stepStart;
            return new Snapshot
            {
                World = _world,
                WorldName = PlainText(_world.Name, "Unnamed world"),
                SessionName = _world.SessionId ?? "",
                TakenLocal = DateTime.Now,
                TakenTick = Environment.TickCount64,
                Entries = _entries,
                Roots = _roots,
                Nodes = _nodes,
                UniqueAssetBytes = uniqueBytes,
                UniqueAssets = unique.Count,
                ScanMs = work * 1000.0 / Stopwatch.Frequency,
                TotalSlots = _nodes.Count,
                TotalComponents = _totalComponents,
                MsPerTick = _cpu is null || _cpu.Frames <= 0 ? 0 : 1000.0 / Stopwatch.Frequency / _cpu.Frames,
                Cpu = _cpu is null ? null : Summarize(_cpu, _attributedTicks),
                MeasuredLights = _lights.Count(light => _lightResults?.ContainsKey(light.Light) == true),
                Lights = lightCosts,
                PhysicsMeasured = physicsMeasured,
                Extra = _extra,
                NetUnattributedBytesPerSecond = _netOther,
                Fps = Engine.Current?.PerfStats?.FPS ?? 0,
                PhysicsScale = physicsScale
            };
        }
    }

    private static void Summarize(List<SlotNode> nodes, AssetMeasure measure)
    {
        for (int n = nodes.Count - 1; n >= 0; n--)
        {
            SlotNode node = nodes[n];
            int slots = 1;
            int components = node.OwnComponents;
            int fluxNodes = node.OwnFluxNodes;
            ExtraTotals? extraTotals = null;
            if (node.OwnExtra is not null)
                (extraTotals = new ExtraTotals()).Add(node.OwnExtra);
            LightTotals? light = null;
            PhysicsTotals? physics = null;
            if (node.OwnLight is not null)
                (light = new LightTotals()).Add(node.OwnLight);

            if (node.OwnPhysics is not null)
                (physics = new PhysicsTotals()).Add(node.OwnPhysics);
            GpuTotals? gpu = null;
            if (node.OwnGpu is not null)
            {
                gpu = new GpuTotals();
                gpu.Add(node.OwnGpu);
            }
            long[]? cpu = node.OwnCpu is null ? null : (long[])node.OwnCpu.Clone();
            SlotNode? largest = null;
            if (node.Children is not null)
            {
                foreach (SlotNode child in node.Children)
                {
                    slots += child.Slots;
                    components += child.Components;
                    fluxNodes += child.FluxNodes;
                    if (child.Extra is not null)
                        (extraTotals ??= new ExtraTotals()).Add(child.Extra);
                    if (child.Light is not null)
                        (light ??= new LightTotals()).Add(child.Light);

                    if (child.Physics is not null)
                        (physics ??= new PhysicsTotals()).Add(child.Physics);
                    if (child.Gpu is not null)
                        (gpu ??= new GpuTotals()).Add(child.Gpu);

                    if (child.Cpu is not null)
                    {
                        cpu ??= new long[CpuResult.Categories];
                        for (int i = 0; i < CpuResult.Categories; i++)
                            cpu[i] += child.Cpu[i];
                    }
                    if (child.Set is not null && (largest?.Set is null || child.Set.Count > largest.Set.Count))
                        largest = child;
                }
            }
            HashSet<IAsset>? set = largest?.Set;
            AssetTotals totals = largest is null ? new AssetTotals() : largest.Size.Copy();
            if (largest is not null)
                largest.Set = null;

            if (node.Children is not null)
            {
                foreach (SlotNode child in node.Children)
                {
                    if (child.Set is null)
                        continue;

                    set ??= new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
                    foreach (IAsset asset in child.Set)
                        if (set.Add(asset))
                            totals.Add(measure.Bytes(asset));

                    child.Set = null;
                }
            }
            if (node.OwnAssets.Length > 0)
            {
                set ??= new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
                foreach (IAsset asset in node.OwnAssets)
                    if (set.Add(asset))
                        totals.Add(measure.Bytes(asset));
            }
            node.Slots = slots;
            node.Components = components;
            node.FluxNodes = fluxNodes;
            node.Extra = extraTotals;
            node.Light = light;
            node.Physics = physics;
            node.Gpu = gpu;
            node.Cpu = cpu;
            node.Size = totals;
            node.Set = set;
            if (node.Parent is null)
                node.Set = null;
        }
    }

    private static CpuSummary Summarize(CpuResult cpu, long attributedTicks)
    {
        var categories = new double[CpuResult.Categories];
        foreach (long[] ticks in cpu.Ticks.Values)
            for (int i = 0; i < CpuResult.Categories; i++)
                categories[i] += ticks[i];

        for (int i = 0; i < CpuResult.Categories; i++)
            categories[i] = cpu.MsPerFrame((long)categories[i]);

        return new CpuSummary
        {
            Frames = cpu.Frames,
            Seconds = cpu.Seconds,
            TotalMs = cpu.MsPerFrame(cpu.TotalTicks()),
            AttributedMs = cpu.MsPerFrame(attributedTicks),
            CategoryMs = categories,
            Coverage = cpu.Coverage(),
            CoverageLine = cpu.CoverageLine(),
            MissingHooks = cpu.MissingHooks
        };
    }

    private static long AddCpu(Dictionary<object, long[]> cpuTicks, object key, ref long[]? target)
    {
        if (!cpuTicks.TryGetValue(key, out long[]? ticks))
            return 0;

        target ??= new long[CpuResult.Categories];
        long sum = 0;
        for (int i = 0; i < CpuResult.Categories; i++)
        {
            target[i] += ticks[i];
            sum += ticks[i];
        }
        return sum;
    }

    private static Dictionary<Slot, (double Updates, double In, double Out)> ResolveNetwork(World world, ExtraResult? extra, out double other)
    {
        other = 0;
        var result = new Dictionary<Slot, (double, double, double)>(ReferenceEqualityComparer.Instance);
        if (extra is null || extra.Network.Count == 0)
            return result;

        double seconds = Math.Max(0.001, extra.Seconds);
        foreach ((RefID id, long[] totals) in extra.Network)
        {
            RefID key = id;
            Slot? slot = null;
            try
            {
                slot = world.ReferenceController.GetObjectOrNull(in key) switch
                {
                    SyncElement element => element.Worker switch
                    {
                        Slot owner => owner,
                        Component component => component.Slot,
                        _ => null
                    },
                    Slot direct => direct,
                    Component component => component.Slot,
                    _ => null
                };
            }
            catch (Exception)
            {
            }
            double updates = (totals[0] + totals[2]) / seconds;
            double outBytes = totals[1] / seconds;
            double inBytes = totals[3] / seconds;
            if (slot is null || slot.IsDestroyed)
            {
                other += inBytes + outBytes;
                continue;
            }
            (double u, double i, double o) = result.GetValueOrDefault(slot);
            result[slot] = (u + updates, i + inBytes, o + outBytes);
        }
        return result;
    }

    internal const int BoneWarningChains = 30;
    internal const int BoneWarningBones = 256;

    private static void AddWarnings(ObjectEntry entry)
    {
        entry.Warnings.Clear();
        entry.WarningKinds.Clear();
        AssetTotals assets = entry.Assets;
        GpuTotals gpu = entry.Gpu;
        if (assets.UncompressedTextures > 0)
            Warn(entry, WarningKind.UncompressedTextures, $"{assets.UncompressedTextures} uncompressed {Plural(assets.UncompressedTextures, "texture", "textures")} {AssetMeasure.UncompressedMinimumSize} px or larger ({Format.Bytes(assets.UncompressedBytes)})");

        if (assets.LargeTextures > 0)
            Warn(entry, WarningKind.LargeTextures, $"{assets.LargeTextures} {Plural(assets.LargeTextures, "texture", "textures")} 4K or larger");

        if (assets.HighPolyMeshes > 0)
            Warn(entry, WarningKind.HighPolyMeshes, $"{assets.HighPolyMeshes} {Plural(assets.HighPolyMeshes, "mesh", "meshes")} over {AssetMeasure.HighPolyTriangles / 1000}k triangles");

        if (gpu.ManyMaterialRenderers > 0)
            Warn(entry, WarningKind.ManyMaterials, $"{gpu.ManyMaterialRenderers} {Plural(gpu.ManyMaterialRenderers, "mesh", "meshes")} with more than 8 materials");

        if (gpu.Renderers > 100)
            Warn(entry, WarningKind.ManyRenderers, $"{gpu.Renderers:N0} separate meshes ({gpu.DrawCalls:N0} draw calls), merging them would help");

        if (assets.LargeAudio > 0)
            Warn(entry, WarningKind.LargeAudio, $"{assets.LargeAudio} audio {Plural(assets.LargeAudio, "clip", "clips")} over {AssetMeasure.LargeAudioBytes >> 20} MB when decoded");

        if (entry.Light.ShadowedPoint > 0)
            Warn(entry, WarningKind.ShadowedLights, $"{entry.Light.ShadowedPoint} point {Plural(entry.Light.ShadowedPoint, "light", "lights")} with shadows (6 shadow maps each)");

        if (gpu.Cameras > 0)
            Warn(entry, WarningKind.Cameras, $"{gpu.Cameras} {Plural(gpu.Cameras, "camera", "cameras")} rendering every frame");

        if (gpu.RealtimeProbes > 0)
            Warn(entry, WarningKind.Probes, $"{gpu.RealtimeProbes} realtime reflection {Plural(gpu.RealtimeProbes, "probe", "probes")}");

        if (entry.Physics.MovingMeshColliders > 0)
            Warn(entry, WarningKind.MovingColliders, $"{entry.Physics.MovingMeshColliders} moving mesh {Plural(entry.Physics.MovingMeshColliders, "collider", "colliders")}");

        if (entry.Extra.BoneChains > BoneWarningChains || entry.Extra.Bones > BoneWarningBones)
            Warn(entry, WarningKind.BoneChains, $"{entry.Extra.BoneChains} dynamic bone chains with {entry.Extra.Bones:N0} bones");

        if (gpu.BlendshapeVertices > 2_000_000)
            Warn(entry, WarningKind.Blendshapes, $"{Format.Count(gpu.BlendshapeVertices)} active blendshape vertices");

        if (entry.Extra.MeshRebuildsPerSecond > 5)
            Warn(entry, WarningKind.MeshRebuilds, $"procedural meshes rebuilding {entry.Extra.MeshRebuildsPerSecond:F0} times a second");

        if (entry.Extra.NetUpdatesPerSecond > 60)
            Warn(entry, WarningKind.NetworkUpdates, $"{entry.Extra.NetUpdatesPerSecond:F0} network updates a second");
    }

    private static void Warn(ObjectEntry entry, WarningKind kind, string text)
    {
        entry.Warnings.Add(text);
        entry.WarningKinds.Add(kind);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static (int Measured, double Scale) ApplyPhysics(List<ObjectEntry> entries, IReadOnlyDictionary<RefID, PhysicsResult>? results)
    {
        if (results is null)
            return (0, 0);

        int measured = 0;
        double measuredMs = 0;
        double measuredScore = 0;
        foreach (ObjectEntry entry in entries)
        {
            if (!results.TryGetValue(entry.Id, out PhysicsResult? result))
                continue;

            entry.Physics.Measured = 1;
            entry.Physics.MeasuredMs = Math.Max(0, result.Ms);
            measured++;
            if (entry.Physics.Score > 0)
            {
                measuredMs += entry.Physics.MeasuredMs;
                measuredScore += entry.Physics.Score;
            }
        }
        double scale = measuredScore > 0 ? measuredMs / measuredScore : 0;
        foreach (ObjectEntry entry in entries)
            entry.Physics.ValueMs = entry.Physics.Measured > 0 ? entry.Physics.MeasuredMs : entry.Physics.Score * scale;

        return (measured, scale);
    }

    private static List<LightCost> AddLighting(List<(Light Light, SlotNode Node, ObjectEntry Entry)> lights, List<RendererSphere> spheres, IReadOnlyDictionary<Light, LightResult>? results)
    {
        var costs = new List<LightCost>(lights.Count);
        foreach ((Light light, SlotNode node, ObjectEntry entry) in lights)
        {
            LightTotals totals;
            try
            {
                totals = MeasureLight(light, spheres);
            }
            catch (Exception)
            {
                continue;
            }
            if (results is not null && results.TryGetValue(light, out LightResult? measured))
            {
                totals.Measured = 1;
                totals.MeasuredMs = Math.Max(0, measured.Ms);
                totals.ValueMs = totals.MeasuredMs;
            }
            else
                totals.ValueMs = totals.Score * 0.01;

            (node.OwnLight ??= new LightTotals()).Add(totals);
            entry.Light.Add(totals);
            costs.Add(new LightCost(light, totals.ValueMs, totals.Measured > 0));
        }
        return costs;
    }

    private static LightTotals MeasureLight(Light light, List<RendererSphere> spheres)
    {
        LightTotals totals = MeasureLightCore(light, spheres);
        if (light.LightType.Value == LightType.Point && light.ShadowType.Value != ShadowType.None)
            totals.ShadowedPoint = 1;

        return totals;
    }

    private static LightTotals MeasureLightCore(Light light, List<RendererSphere> spheres)
    {
        LightType type = light.LightType.Value;
        bool directional = type == LightType.Directional;
        bool shadows = light.ShadowType.Value != ShadowType.None;
        float3 position = light.Slot.GlobalPosition;
        float3 scale = light.Slot.GlobalScale;
        float largest = MathX.Max(MathX.Abs(scale.x), MathX.Max(MathX.Abs(scale.y), MathX.Abs(scale.z)));
        float range = MathX.Max(0f, light.Range.Value) * largest;
        long lit = 0;
        long shadowDraws = 0;
        foreach (RendererSphere sphere in spheres)
        {
            if (!directional && MathX.Distance(sphere.Center, position) > range + sphere.Radius)
                continue;

            lit += sphere.Draws;
            if (shadows && sphere.CastsShadows)
                shadowDraws += sphere.Draws;
        }
        int faces = !shadows ? 0 : type switch
        {
            LightType.Point => 6,
            LightType.Directional => 4,
            _ => 1
        };
        int resolution = light.ShadowMapResolution.Value;
        double resolutionFactor = resolution <= 0 ? 1 : Math.Pow(resolution / 1024.0, 2);
        return new LightTotals
        {
            Lights = 1,
            Shadowed = shadows ? 1 : 0,
            Directional = directional ? 1 : 0,
            LitDraws = lit,
            ShadowDraws = shadowDraws * faces,
            Score = (directional ? 10 : 5) + lit + shadowDraws * faces + faces * 10 * resolutionFactor
        };
    }

    internal static string TypeName(Component component)
    {
        string name = NiceName(component.GetType());
        return component is ProtoFluxNode ? "ProtoFlux " + name : name;
    }

    private static string NiceName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];

        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(NiceName)) + ">";
    }

    private static Dictionary<object, long[]> AttributeProtoFlux(CpuResult cpu)
    {
        var result = new Dictionary<object, long[]>(cpu.Ticks.Count, ReferenceEqualityComparer.Instance);
        var nodes = new List<ProtoFluxNode>();
        foreach ((object key, long[] ticks) in cpu.Ticks)
        {
            if (key is not ProtoFluxNodeGroup group)
            {
                Merge(result, key, ticks, 1);
                continue;
            }
            nodes.Clear();
            try
            {
                foreach (ProtoFluxNode node in group.Nodes)
                    if (node is not null && !node.IsDestroyed)
                        nodes.Add(node);
            }
            catch (InvalidOperationException)
            {
            }
            foreach (ProtoFluxNode node in nodes)
                Merge(result, node, ticks, nodes.Count);
        }
        return result;
    }

    private static void Merge(Dictionary<object, long[]> result, object key, long[] ticks, int share)
    {
        if (!result.TryGetValue(key, out long[]? total))
        {
            total = new long[CpuResult.Categories];
            result[key] = total;
        }
        for (int i = 0; i < CpuResult.Categories; i++)
            total[i] += ticks[i] / share;
    }

    private static bool IsObjectRoot(Slot slot, out UserRoot? userRoot)
    {
        userRoot = null;
        bool found = false;
        foreach (Component component in slot.Components)
        {
            switch (component)
            {
                case UserRoot root:
                    userRoot = root;
                    return true;
                case ObjectRoot:
                case Grabbable:
                    found = true;
                    break;
            }
        }
        return found;
    }

    private static string DisplayName(Slot slot, UserRoot? userRoot)
    {
        string name = PlainText(slot.Name, "Unnamed slot");
        if (userRoot?.ActiveUser is User user)
            name = "User: " + PlainText(user.UserName, name);

        return Truncate(name, 80);
    }

    private static ObjectEntry CreateEntry(Slot slot, bool container, UserRoot? userRoot)
    {
        return new ObjectEntry
        {
            Slot = slot,
            Id = slot.ReferenceID,
            Name = DisplayName(slot, userRoot),
            Path = ParentPath(slot),
            Persistent = slot.IsPersistent,
            IsUser = userRoot is not null,
            IsContainer = container
        };
    }

    private static string ParentPath(Slot slot)
    {
        var names = new List<string>();
        Slot? parent = slot.Parent;
        while (parent is not null && !parent.IsRootSlot)
        {
            names.Add(Truncate(PlainText(parent.Name, "Unnamed slot"), 40));
            parent = parent.Parent;
        }
        if (names.Count == 0)
            return "World root";

        names.Reverse();
        if (names.Count > 5)
            names = ["...", .. names.Skip(names.Count - 4)];

        return string.Join(" / ", names);
    }

    internal static string PlainText(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        string plain = RichTextTag.Replace(text, "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return plain.Length > 0 ? plain : fallback;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";
}
