using System.Diagnostics;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace WorldTelemetry;

internal enum HeatMetric
{
    Off,
    Size,
    Cpu,
    Components,
    ProtoFlux,
    Bones,
    Gpu,
    Lighting,
    Physics,
    Network,
    Warnings,
    Slots
}

internal enum HeatStyle
{
    Solid,
    Outline,
    Meshes,
    MeshesOrOutline
}

internal static class Heatmap
{
    private const int Buckets = 16;
    private const int RenderersPerObject = 256;
    private const int MaximumOverlays = 4000;
    private const int BatchSize = 100;
    private const double SliceMs = 3;
    private const double RefreshBudgetMs = 1.5;
    private const long BuildTimeoutMs = 60000;
    private const float BoxPadding = 1.03f;
    private const int OverlayRenderQueue = 3990;
    private const float Alpha = 0.45f;
    private const string RootName = "WorldTelemetry Heatmap";
    private const string OverlayName = "WorldTelemetry Heat";
    private const string VolumeName = "WorldTelemetry Light Range";
    private const float MarkerSize = 0.3f;
    private const float DirectionalLength = 0.6f;

    private sealed record Target(ObjectEntry Entry, int Bucket);

    private sealed record LightTarget(Light Light, int Bucket);

    private sealed class Volume
    {
        internal required Slot Slot { get; init; }
        internal required MeshRenderer Renderer { get; init; }
        internal required LightType Type { get; init; }
        internal SphereMesh? Sphere;
        internal ConeMesh? Cone;
    }

    private sealed class BoxOverlay
    {
        internal required Slot Slot { get; init; }
        internal required MeshRenderer Renderer { get; init; }
        internal required Slot Target { get; init; }
        internal required bool Outline { get; init; }
        internal bool UserOwned;
        internal BoxMesh? Box;
        internal TubeBoxMesh? Tube;
    }

    private sealed class State
    {
        internal required World World { get; init; }
        internal Slot? Root;
        internal readonly UnlitMaterial[] Materials = new UnlitMaterial[Buckets];
        internal readonly UnlitMaterial[] VolumeMaterials = new UnlitMaterial[Buckets];
        internal readonly UnlitMaterial[] OutlineMaterials = new UnlitMaterial[Buckets];
        internal float VolumeAlpha = -1f;
        internal readonly Dictionary<Light, Volume> Volumes = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<MeshRenderer, (Slot Slot, MeshRenderer Overlay)> Meshes = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<RefID, BoxOverlay> Boxes = new();
        internal int Generation;
    }

    private static State? _state;
    private static int _appliedVersion = -1;
    private static bool _appliedHideUsers;
    private static bool _appliedShowAll;
    private static HeatStyle _appliedStyle;
    private const float OutlineAlpha = 0.95f;
    private const float OutlineThickness = 0.006f;
    private const float OutlineMinimum = 0.005f;
    private const float OutlineMaximum = 0.04f;
    private const int UnlimitedOverlays = 100000;
    private static HeatMetric _appliedMetric = HeatMetric.Off;
    private static int _colored;
    private static int _skippedLarge;
    private static int _volumes;
    private static long _nextBoxRefresh;
    private const long BoxRefreshMs = 250;
    private const float BoxEpsilon = 0.002f;

    internal static bool Enabled { get; private set; }
    internal static bool HideUsers { get; private set; }
    internal static bool ShowAll { get; private set; }
    internal static HeatStyle Style { get; private set; } = HeatStyle.MeshesOrOutline;
    internal static HeatMetric Metric => Enabled ? (HeatMetric)((int)DashScreen.CurrentSort + 1) : HeatMetric.Off;
    internal static int Colored => _colored;
    internal static int SkippedLarge => _skippedLarge;
    internal static int Volumes => _volumes;
    internal static bool Building => _building && Environment.TickCount64 - _buildStarted < BuildTimeoutMs;
    private static bool _building;
    private static long _buildStarted;
    private static int _refreshCursor;

    internal static void Toggle()
    {
        Enabled = !Enabled;
        EnsureProfile();
    }

    internal static void ToggleUsers() => HideUsers = !HideUsers;

    internal static void ToggleShowAll() => ShowAll = !ShowAll;

    internal static void CycleStyle() => Style = (HeatStyle)(((int)Style + 1) % 4);

    private static bool IsUserOwned(Slot slot) => slot.GetComponentInParents<UserRoot>() is not null;

    internal static bool IsBlocked(Slot slot)
    {
        try
        {
            return !slot.IsDestroyed && slot.ActiveUser is User user && user.IsRenderingLocallyBlocked;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BlockedKey(World world)
    {
        try
        {
            return string.Join(",", world.AllUsers.Where(user => user.IsRenderingLocallyBlocked).Select(user => user.ReferenceID.ToString()).OrderBy(id => id, StringComparer.Ordinal));
        }
        catch (Exception)
        {
            return _appliedBlocked;
        }
    }

    private static string _appliedBlocked = "";

    internal static void EnsureProfile()
    {
        bool timed = Metric is HeatMetric.Cpu or HeatMetric.Components or HeatMetric.ProtoFlux or HeatMetric.Bones or HeatMetric.Network;
        if (timed && Telemetry.FocusedWorld is World world && Telemetry.Latest?.World == world && Telemetry.Latest.Cpu is null && !CpuProfiler.Running)
            Telemetry.RequestProfile(world, WorldTelemetryMod.ProfileSeconds, ReportMode.None);
    }

    internal static string Label => Enabled ? DashScreen.SortName(DashScreen.CurrentSort) : "Off";

    internal static void Tick()
    {
        World? focused = Telemetry.FocusedWorld;
        if (_state is not null && (_state.World.IsDestroyed || _state.World != focused || Metric == HeatMetric.Off))
        {
            Clear();
            _appliedVersion = -1;
        }
        if (Metric == HeatMetric.Off || focused is null)
            return;

        long now = Environment.TickCount64;
        if (_state is State current && current.Boxes.Count > 0 && now >= _nextBoxRefresh)
        {
            _nextBoxRefresh = now + BoxRefreshMs;
            current.World.RunSynchronously(() => RefreshBoxes(current));
        }
        Snapshot? snapshot = Telemetry.Latest;
        if (snapshot is null || snapshot.World != focused)
            return;

        string blocked = BlockedKey(focused);
        if (_appliedVersion == Telemetry.Version && _appliedMetric == Metric && _appliedHideUsers == HideUsers && _appliedShowAll == ShowAll && _appliedStyle == Style && _appliedBlocked == blocked)
            return;

        _appliedBlocked = blocked;
        _appliedHideUsers = HideUsers;
        _appliedShowAll = ShowAll;
        _appliedStyle = Style;
        _appliedVersion = Telemetry.Version;
        _appliedMetric = Metric;
        Apply(snapshot, Metric);
    }

    internal static List<Slot> OverlaySlots(World world)
    {
        var slots = new List<Slot>();
        State? state = _state;
        if (state is null || state.World != world)
            return slots;

        if (state.Root is not null && !state.Root.IsDestroyed)
            slots.Add(state.Root);

        foreach ((Slot slot, MeshRenderer _) in state.Meshes.Values)
            if (!slot.IsDestroyed)
                slots.Add(slot);

        foreach (Volume volume in state.Volumes.Values)
            if (!volume.Slot.IsDestroyed)
                slots.Add(volume.Slot);

        foreach (BoxOverlay box in state.Boxes.Values)
            if (!box.Slot.IsDestroyed)
                slots.Add(box.Slot);

        return slots;
    }

    private static void Apply(Snapshot snapshot, HeatMetric metric)
    {
        World world = snapshot.World;
        List<Target> targets = Rank(snapshot, metric);
        List<LightTarget> lights = metric == HeatMetric.Lighting ? RankLights(snapshot) : new List<LightTarget>();
        State state = _state ??= new State { World = world };
        int generation = ++state.Generation;
        _building = true;
        _buildStarted = Environment.TickCount64;
        world.RunSynchronously(() => world.Coroutines.StartCoroutine(Build(state, targets, lights, generation)));
    }

    private static List<Target> Rank(Snapshot snapshot, HeatMetric metric)
    {
        User? local = snapshot.World.LocalUser;
        List<(ObjectEntry Entry, double Value)> values = snapshot.Entries
            .Where(entry => !(entry.IsUser && local?.Root is UserRoot root && entry.Slot == root.Slot))
            .Where(entry => !HideUsers || !(entry.IsUser || IsUserOwned(entry.Slot)))
            .Where(entry => !IsBlocked(entry.Slot))
            .Select(entry => (entry, Value(entry, metric, snapshot.PhysicsMeasured > 0)))
            .OrderBy(pair => pair.Item2)
            .ToList();
        var targets = new List<Target>(values.Count);
        int count = values.Count;
        int i = 0;
        while (i < count)
        {
            int j = i;
            while (j + 1 < count && values[j + 1].Value == values[i].Value)
                j++;

            double rank = count <= 1 || values[i].Value <= 0 ? 0 : (i + j) * 0.5 / (count - 1);
            int bucket = Math.Clamp((int)Math.Round(rank * (Buckets - 1)), 0, Buckets - 1);
            for (int k = i; k <= j; k++)
                targets.Add(new Target(values[k].Entry, bucket));

            i = j + 1;
        }
        targets.Reverse();
        return targets;
    }

    private static List<LightTarget> RankLights(Snapshot snapshot)
    {
        List<LightCost> costs = snapshot.Lights.Where(cost => cost.Light.IsDestroyed || ((!HideUsers || !IsUserOwned(cost.Light.Slot)) && !IsBlocked(cost.Light.Slot))).OrderBy(cost => cost.ValueMs).ToList();
        var targets = new List<LightTarget>(costs.Count);
        int count = costs.Count;
        int i = 0;
        while (i < count)
        {
            int j = i;
            while (j + 1 < count && costs[j + 1].ValueMs == costs[i].ValueMs)
                j++;

            double rank = count <= 1 ? (costs[i].ValueMs > 0 ? 1 : 0) : costs[i].ValueMs <= 0 ? 0 : (i + j) * 0.5 / (count - 1);
            int bucket = Math.Clamp((int)Math.Round(rank * (Buckets - 1)), 0, Buckets - 1);
            for (int k = i; k <= j; k++)
                targets.Add(new LightTarget(costs[k].Light, bucket));

            i = j + 1;
        }
        return targets;
    }

    private static double Value(ObjectEntry entry, HeatMetric metric, bool physicsMeasured) => metric switch
    {
        HeatMetric.Cpu => entry.CpuTotalMs,
        HeatMetric.Components => entry.ComponentMs,
        HeatMetric.ProtoFlux => entry.FluxMs,
        HeatMetric.Bones => entry.BonesMs > 0 ? entry.BonesMs : entry.Extra.Bones * 0.00001,
        HeatMetric.Network => entry.Extra.NetBytesPerSecond,
        HeatMetric.Warnings => entry.Warnings.Count,
        HeatMetric.Gpu => entry.GpuScore,
        HeatMetric.Lighting => entry.Light.ValueMs,
        HeatMetric.Physics => physicsMeasured ? entry.Physics.ValueMs : entry.Physics.Score,
        HeatMetric.Slots => entry.Slots,
        _ => entry.SizeBytes
    };

    internal static colorX Color(double t)
    {
        t = Math.Clamp(t, 0, 1);
        colorX green = new(0.35f, 1f, 0.45f, Alpha);
        colorX yellow = new(1f, 0.95f, 0.2f, Alpha);
        colorX orange = new(1f, 0.55f, 0.1f, Alpha);
        colorX red = new(1f, 0.08f, 0.08f, Alpha);
        if (t < 0.5)
            return Lerp(green, yellow, t / 0.5);

        if (t < 0.8)
            return Lerp(yellow, orange, (t - 0.5) / 0.3);

        return Lerp(orange, red, (t - 0.8) / 0.2);
    }

    private static colorX Lerp(colorX a, colorX b, double t)
    {
        float f = (float)t;
        return new colorX(a.r + (b.r - a.r) * f, a.g + (b.g - a.g) * f, a.b + (b.b - a.b) * f, Alpha);
    }

    private static UnlitMaterial CreateMaterial(Slot root, colorX color)
    {
        UnlitMaterial material = root.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = color;
        material.BlendMode.Value = BlendMode.Alpha;
        material.ZWrite.Value = ZWrite.Off;
        material.Sidedness.Value = Sidedness.Double;
        material.RenderQueue.Value = OverlayRenderQueue;
        material.OffsetFactor.Value = -2f;
        material.OffsetUnits.Value = -8f;
        return material;
    }

    private static void EnsureRoot(State state)
    {
        if (state.Root is not null && !state.Root.IsDestroyed)
            return;

        state.Root = state.World.AddLocalSlot(RootName, false);
        state.Root.Tag = Scanner.OwnTag;
        for (int i = 0; i < Buckets; i++)
        {
            state.Materials[i] = CreateMaterial(state.Root, Color(i / (double)(Buckets - 1)));
            state.VolumeMaterials[i] = CreateMaterial(state.Root, Color(i / (double)(Buckets - 1)));
            state.OutlineMaterials[i] = CreateMaterial(state.Root, Color(i / (double)(Buckets - 1)).SetA(OutlineAlpha));
        }
        state.VolumeAlpha = -1f;
    }

    private static void UpdateVolumeAlpha(State state)
    {
        float alpha = WorldTelemetryMod.LightVolumeAlpha;
        if (state.VolumeAlpha == alpha)
            return;

        state.VolumeAlpha = alpha;
        for (int i = 0; i < Buckets; i++)
            state.VolumeMaterials[i].TintColor.Value = Color(i / (double)(Buckets - 1)).SetA(alpha);
    }

    private sealed class Pacer
    {
        private readonly Stopwatch _slice = Stopwatch.StartNew();
        private int _work;

        internal bool Due()
        {
            if (++_work % BatchSize != 0 && _slice.Elapsed.TotalMilliseconds < SliceMs)
                return false;

            _slice.Restart();
            return true;
        }
    }

    private static IEnumerator<Context> Build(State state, List<Target> targets, List<LightTarget> lights, int generation)
    {
        try
        {
            try
            {
                EnsureRoot(state);
                UpdateVolumeAlpha(state);
            }
            catch (Exception ex)
            {
                WorldTelemetryMod.LogWarning("Could not prepare the heatmap: " + ex.Message);
                yield break;
            }
            bool showAll = ShowAll;
            HeatStyle style = Style;
            bool outlineBox = style is HeatStyle.Outline or HeatStyle.MeshesOrOutline;
            bool useMeshes = style is HeatStyle.Meshes or HeatStyle.MeshesOrOutline;
            float maximumSize = showAll ? float.MaxValue : WorldTelemetryMod.HeatmapMaximumSize;
            int perObject = showAll ? UnlimitedOverlays : RenderersPerObject;
            int overlayLimit = showAll ? UnlimitedOverlays : MaximumOverlays;
            var seenMeshes = new HashSet<MeshRenderer>(ReferenceEqualityComparer.Instance);
            var seenBoxes = new HashSet<RefID>();
            var pacer = new Pacer();
            int colored = 0;
            int skipped = 0;
            foreach (Target target in targets)
            {
                if (generation != state.Generation || state.World.IsDestroyed)
                    yield break;

                Slot slot = target.Entry.Slot;
                if (slot.IsDestroyed)
                    continue;

                bool bounded;
                bool tooLarge;
                List<MeshRenderer>? renderers = null;
                try
                {
                    BoundingBox box = Bounds.Of(slot);
                    bounded = Bounds.IsUsable(box);
                    tooLarge = bounded && MathX.Max(box.Size.x, MathX.Max(box.Size.y, box.Size.z)) > maximumSize;
                    if (!tooLarge && useMeshes)
                        renderers = slot.GetComponentsInChildren<MeshRenderer>(candidate => candidate.Enabled && candidate.Slot.IsActive);
                }
                catch (Exception)
                {
                    continue;
                }
                if (tooLarge)
                {
                    skipped++;
                    continue;
                }
                UnlitMaterial boxMaterial = outlineBox ? state.OutlineMaterials[target.Bucket] : state.Materials[target.Bucket];
                UnlitMaterial meshMaterial = state.Materials[target.Bucket];
                int placed = 0;
                if (renderers is not null)
                {
                    foreach (MeshRenderer renderer in renderers)
                    {
                        if (placed >= perObject || seenMeshes.Count >= overlayLimit)
                            break;

                        bool added = false;
                        try
                        {
                            if (!renderer.IsRemoved && !renderer.Slot.IsDestroyed && HasValidMesh(renderer))
                            {
                                SetMeshOverlay(state, renderer, meshMaterial);
                                added = true;
                            }
                        }
                        catch (Exception)
                        {
                        }
                        if (added)
                        {
                            placed++;
                            seenMeshes.Add(renderer);
                        }
                        if (pacer.Due())
                        {
                            yield return Context.WaitForNextUpdate();
                            if (generation != state.Generation || state.World.IsDestroyed)
                                yield break;
                        }
                    }
                }
                bool needsBox = style is HeatStyle.Solid or HeatStyle.Outline || (style == HeatStyle.MeshesOrOutline && placed == 0);
                if (needsBox && bounded && !slot.IsDestroyed)
                {
                    try
                    {
                        SetBoxOverlay(state, target.Entry.Id, slot, boxMaterial, outlineBox, target.Entry.IsUser || IsUserOwned(slot));
                        seenBoxes.Add(target.Entry.Id);
                        placed++;
                    }
                    catch (Exception)
                    {
                    }
                }
                if (placed > 0)
                    colored++;

                if (pacer.Due())
                    yield return Context.WaitForNextUpdate();
            }
            foreach (MeshRenderer stale in state.Meshes.Keys.Where(renderer => !seenMeshes.Contains(renderer)).ToList())
            {
                (Slot slot, MeshRenderer _) = state.Meshes[stale];
                state.Meshes.Remove(stale);
                DestroyQuietly(slot);
            }
            foreach (RefID stale in state.Boxes.Keys.Where(id => !seenBoxes.Contains(id)).ToList())
            {
                Slot slot = state.Boxes[stale].Slot;
                state.Boxes.Remove(stale);
                DestroyQuietly(slot);
            }
            var seenVolumes = new HashSet<Light>(ReferenceEqualityComparer.Instance);
            foreach (LightTarget light in lights)
            {
                if (generation != state.Generation || state.World.IsDestroyed)
                    yield break;

                if (light.Light.IsDestroyed || light.Light.Slot.IsDestroyed)
                    continue;

                try
                {
                    SetVolume(state, light.Light, state.VolumeMaterials[light.Bucket]);
                    seenVolumes.Add(light.Light);
                }
                catch (Exception)
                {
                }
                if (pacer.Due())
                    yield return Context.WaitForNextUpdate();
            }
            foreach (Light stale in state.Volumes.Keys.Where(light => !seenVolumes.Contains(light)).ToList())
            {
                Volume volume = state.Volumes[stale];
                state.Volumes.Remove(stale);
                DestroyQuietly(volume.Slot);
            }
            _colored = colored;
            _skippedLarge = skipped;
            _volumes = seenVolumes.Count;
        }
        finally
        {
            if (generation == state.Generation)
                _building = false;
        }
    }

    private static void DestroyQuietly(Slot slot)
    {
        try
        {
            if (!slot.IsDestroyed)
                slot.Destroy();
        }
        catch (Exception)
        {
        }
    }

    private static void SetVolume(State state, Light light, UnlitMaterial material)
    {
        LightType type = light.LightType.Value;
        if (!state.Volumes.TryGetValue(light, out Volume? volume) || volume.Slot.IsDestroyed || volume.Type != type)
        {
            if (volume is not null && !volume.Slot.IsDestroyed)
                volume.Slot.Destroy();

            Slot slot = light.Slot.AddLocalSlot(VolumeName, false);
            slot.Tag = Scanner.OwnTag;
            MeshRenderer renderer = slot.AttachComponent<MeshRenderer>();
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            volume = new Volume { Slot = slot, Renderer = renderer, Type = type };
            if (type == LightType.Point)
            {
                volume.Sphere = slot.AttachComponent<SphereMesh>();
                volume.Sphere.Segments.Value = 32;
                volume.Sphere.Rings.Value = 16;
                renderer.Mesh.Target = volume.Sphere;
            }
            else
            {
                volume.Cone = slot.AttachComponent<ConeMesh>();
                volume.Cone.Sides.Value = 32;
                volume.Cone.RadiusTop.Value = 0f;
                renderer.Mesh.Target = volume.Cone;
            }
            renderer.Materials.Add(material);
            state.Volumes[light] = volume;
        }
        float3 scale = light.Slot.GlobalScale;
        float largest = MathX.Max(1e-6f, MathX.Max(MathX.Abs(scale.x), MathX.Max(MathX.Abs(scale.y), MathX.Abs(scale.z))));
        float range = MathX.Max(0f, light.Range.Value);
        float maximum = WorldTelemetryMod.LightVolumeMaximum / largest;
        switch (type)
        {
            case LightType.Point:
            {
                float radius = range * largest > WorldTelemetryMod.LightVolumeMaximum ? MarkerSize / largest : range;
                volume.Slot.LocalPosition = float3.Zero;
                volume.Slot.LocalRotation = floatQ.Identity;
                if (volume.Sphere!.Radius.Value != radius)
                    volume.Sphere.Radius.Value = radius;

                break;
            }
            case LightType.Spot:
            {
                float length = MathX.Min(range, maximum);
                float angle = MathX.Clamp(light.SpotAngle.Value, 1f, 179f) * 0.5f * (MathF.PI / 180f);
                SetCone(volume, length, length * MathF.Tan(angle));
                break;
            }
            default:
            {
                float length = DirectionalLength / largest;
                SetCone(volume, length, length * 0.3f);
                break;
            }
        }
        if (volume.Renderer.Materials.Count == 0)
            volume.Renderer.Materials.Add(material);
        else if (volume.Renderer.Materials[0] != material)
            volume.Renderer.Materials[0] = material;
    }

    private static void SetCone(Volume volume, float length, float radius)
    {
        ConeMesh cone = volume.Cone!;
        volume.Slot.LocalRotation = floatQ.FromToRotation(float3.Up, float3.Backward);
        volume.Slot.LocalPosition = float3.Forward * (length * 0.5f);
        if (cone.Height.Value != length)
            cone.Height.Value = length;

        if (cone.RadiusBase.Value != radius)
            cone.RadiusBase.Value = radius;
    }

    private static bool HasValidMesh(MeshRenderer renderer)
    {
        if (renderer.Mesh.Target is null || renderer.Materials.Count == 0)
            return false;

        bool material = false;
        for (int i = 0; i < renderer.Materials.Count && !material; i++)
            material = renderer.Materials[i] is not null;

        if (!material)
            return false;

        MeshX? data = renderer.Mesh.Asset?.Data;
        return data is not null && data.VertexCount > 0;
    }

    private static void SetMeshOverlay(State state, MeshRenderer source, UnlitMaterial material)
    {
        bool skinned = source is SkinnedMeshRenderer;
        if (!state.Meshes.TryGetValue(source, out (Slot Slot, MeshRenderer Overlay) overlay) || overlay.Slot.IsDestroyed || (overlay.Overlay is SkinnedMeshRenderer) != skinned)
        {
            if (overlay.Slot is not null && !overlay.Slot.IsDestroyed)
                overlay.Slot.Destroy();

            Slot slot = source.Slot.AddLocalSlot(OverlayName, false);
            slot.Tag = Scanner.OwnTag;
            MeshRenderer renderer = skinned ? slot.AttachComponent<SkinnedMeshRenderer>() : slot.AttachComponent<MeshRenderer>();
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            overlay = (slot, renderer);
            state.Meshes[source] = overlay;
        }
        int sorting = source.SortingOrder.Value + 1;
        if (overlay.Overlay.SortingOrder.Value != sorting)
            overlay.Overlay.SortingOrder.Value = sorting;

        if (!overlay.Overlay.Enabled)
            overlay.Overlay.Enabled = true;

        if (overlay.Overlay.Mesh.Target != source.Mesh.Target)
            overlay.Overlay.Mesh.Target = source.Mesh.Target;

        if (source is SkinnedMeshRenderer skin && overlay.Overlay is SkinnedMeshRenderer copy)
            SyncSkinned(skin, copy);

        int count = source.Materials.Count;
        while (overlay.Overlay.Materials.Count > count)
            overlay.Overlay.Materials.RemoveAt(overlay.Overlay.Materials.Count - 1);

        while (overlay.Overlay.Materials.Count < count)
            overlay.Overlay.Materials.Add(material);

        for (int i = 0; i < count; i++)
            if (overlay.Overlay.Materials[i] != material)
                overlay.Overlay.Materials[i] = material;
    }

    private static void SyncSkinned(SkinnedMeshRenderer source, SkinnedMeshRenderer copy)
    {
        if (copy.BoundsComputeMethod.Value != SkinnedBounds.Proxy)
            copy.BoundsComputeMethod.Value = SkinnedBounds.Proxy;

        if (copy.ProxyBoundsSource.Target != source)
            copy.ProxyBoundsSource.Target = source;

        while (copy.Bones.Count > source.Bones.Count)
            copy.Bones.RemoveAt(copy.Bones.Count - 1);

        while (copy.Bones.Count < source.Bones.Count)
            copy.Bones.Add(source.Bones[copy.Bones.Count]);

        for (int i = 0; i < source.Bones.Count; i++)
            if (copy.Bones[i] != source.Bones[i])
                copy.Bones[i] = source.Bones[i];

        while (copy.BlendShapeWeights.Count > source.BlendShapeWeights.Count)
            copy.BlendShapeWeights.RemoveAt(copy.BlendShapeWeights.Count - 1);

        while (copy.BlendShapeWeights.Count < source.BlendShapeWeights.Count)
            copy.BlendShapeWeights.Add(0f);

        for (int i = 0; i < source.BlendShapeWeights.Count; i++)
        {
            float weight = source.BlendShapeWeights[i];
            Sync<float> element = copy.BlendShapeWeights.GetElement(i);
            if (element.Value != weight)
                element.Value = weight;
        }
    }

    private static void SetBoxOverlay(State state, RefID id, Slot target, UnlitMaterial material, bool outline, bool userOwned)
    {
        if (!state.Boxes.TryGetValue(id, out BoxOverlay? overlay) || overlay.Slot.IsDestroyed || overlay.Target != target || overlay.Outline != outline)
        {
            if (overlay is not null && !overlay.Slot.IsDestroyed)
                overlay.Slot.Destroy();

            Slot slot = target.AddLocalSlot(OverlayName, false);
            slot.Tag = Scanner.OwnTag;
            MeshRenderer renderer = slot.AttachComponent<MeshRenderer>();
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            renderer.Materials.Add(material);
            overlay = new BoxOverlay { Slot = slot, Renderer = renderer, Target = target, Outline = outline };
            if (outline)
            {
                overlay.Tube = slot.AttachComponent<TubeBoxMesh>();
                renderer.Mesh.Target = overlay.Tube;
            }
            else
            {
                overlay.Box = slot.AttachComponent<BoxMesh>();
                renderer.Mesh.Target = overlay.Box;
            }
            state.Boxes[id] = overlay;
        }
        overlay.UserOwned = userOwned;
        FitBox(overlay);
        if (overlay.Renderer.Materials.Count == 0)
            overlay.Renderer.Materials.Add(material);
        else if (overlay.Renderer.Materials[0] != material)
            overlay.Renderer.Materials[0] = material;
    }

    private static void RefreshBoxes(State state)
    {
        if (state != _state || state.World.IsDestroyed)
            return;

        List<BoxOverlay> boxes = state.Boxes.Values.ToList();
        if (boxes.Count == 0)
            return;

        foreach (BoxOverlay overlay in boxes)
            if (overlay.UserOwned)
                Refit(overlay);

        long start = Stopwatch.GetTimestamp();
        long budget = (long)(RefreshBudgetMs * Stopwatch.Frequency / 1000.0);
        int visited = 0;
        while (visited < boxes.Count && Stopwatch.GetTimestamp() - start < budget)
        {
            _refreshCursor %= boxes.Count;
            BoxOverlay overlay = boxes[_refreshCursor++];
            visited++;
            if (!overlay.UserOwned)
                Refit(overlay);
        }
    }

    private static void Refit(BoxOverlay overlay)
    {
        if (overlay.Slot.IsDestroyed || overlay.Target.IsDestroyed)
            return;

        try
        {
            FitBox(overlay);
        }
        catch (Exception)
        {
        }
    }

    internal static void Shutdown()
    {
        Enabled = false;
        Clear();
        _appliedVersion = -1;
    }

    private static void FitBox(BoxOverlay overlay)
    {
        Slot slot = overlay.Slot;
        Slot target = overlay.Target;
        BoundingBox bounds = Bounds.Of(target, target);
        if (!Bounds.IsUsable(bounds))
        {
            if (slot.ActiveSelf)
                slot.ActiveSelf = false;

            return;
        }
        if (!slot.ActiveSelf)
            slot.ActiveSelf = true;

        float3 size = overlay.Tube is null ? bounds.Size * BoxPadding : bounds.Size;
        if (MathX.Distance(slot.LocalPosition, bounds.Center) > BoxEpsilon)
            slot.LocalPosition = bounds.Center;

        if (slot.LocalRotation != floatQ.Identity)
            slot.LocalRotation = floatQ.Identity;

        if (slot.LocalScale != float3.One)
            slot.LocalScale = float3.One;

        if (overlay.Tube is TubeBoxMesh tube)
        {
            float3 scale = target.GlobalScale;
            float largestScale = MathX.Max(1e-6f, MathX.Max(MathX.Abs(scale.x), MathX.Max(MathX.Abs(scale.y), MathX.Abs(scale.z))));
            float largestSide = MathX.Max(bounds.Size.x, MathX.Max(bounds.Size.y, bounds.Size.z));
            float radius = MathX.Clamp(largestSide * largestScale * OutlineThickness, OutlineMinimum, OutlineMaximum) / largestScale;
            if (MathX.Distance(tube.Size.Value, size) > BoxEpsilon)
                tube.Size.Value = size;

            if (MathX.Abs(tube.TubeRadius.Value - radius) > radius * 0.02f)
                tube.TubeRadius.Value = radius;

            return;
        }
        if (overlay.Box is BoxMesh box && MathX.Distance(box.Size.Value, size) > BoxEpsilon)
            box.Size.Value = size;
    }

    private static void Clear()
    {
        State? state = _state;
        _state = null;
        _colored = 0;
        _skippedLarge = 0;
        _volumes = 0;
        _building = false;
        if (state is null)
            return;

        state.Generation++;
        if (state.World.IsDestroyed)
            return;

        List<Slot> slots = state.Meshes.Values.Select(pair => pair.Slot)
            .Concat(state.Boxes.Values.Select(box => box.Slot))
            .Concat(state.Volumes.Values.Select(volume => volume.Slot))
            .ToList();
        Slot? root = state.Root;
        state.World.RunSynchronously(() =>
        {
            foreach (Slot slot in slots)
                if (!slot.IsDestroyed)
                    slot.Destroy();

            if (root is not null && !root.IsDestroyed)
                root.Destroy();
        });
    }
}
