using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Renderite.Shared;

namespace WorldTelemetry;

internal enum WarningKind
{
    UncompressedTextures,
    LargeTextures,
    HighPolyMeshes,
    ManyMaterials,
    ManyRenderers,
    LargeAudio,
    ShadowedLights,
    Cameras,
    Probes,
    MovingColliders,
    BoneChains,
    Blendshapes,
    MeshRebuilds,
    NetworkUpdates
}

internal static class WarningActions
{
    private const int MaximumInspectors = 10;
    private const int BatchSize = 3;
    private const string InspectorName = "Inspector (WorldTelemetry)";
    private const float Spacing = 1.08f;
    private const float PanelWidth = 660f;

    private static readonly List<Slot> Spawned = new();
    private static int _totalTargets;

    internal static void ForgetClosedWorlds() => Spawned.RemoveAll(slot => slot.IsDestroyed || slot.World.IsDestroyed);

    internal static bool HasTargets(WarningKind kind) => kind is not (WarningKind.ManyRenderers or WarningKind.NetworkUpdates);

    internal static void Run(Snapshot snapshot, ObjectEntry entry, WarningKind kind, Action<string> report)
    {
        World world = snapshot.World;
        if (world.IsDestroyed || entry.Slot.IsDestroyed)
        {
            report($"{entry.Name} no longer exists.");
            return;
        }
        if (!HasTargets(kind))
        {
            report($"Selected {entry.Name}. This warning is about the object as a whole, so there is nothing specific to inspect.");
            return;
        }
        if (!Access.CanInspect(world))
        {
            report("You do not have permission to open inspectors in this world, so none were opened.");
            return;
        }
        world.RunSynchronously(() => world.Coroutines.StartCoroutine(Spawn(snapshot, entry, kind, report)));
    }

    private static IEnumerator<Context> Spawn(Snapshot snapshot, ObjectEntry entry, WarningKind kind, Action<string> report)
    {
        World world = snapshot.World;
        List<Slot> targets = new();
        string? failure = null;
        try
        {
            failure = Targets(snapshot, entry, kind, targets);
        }
        catch (Exception ex)
        {
            failure = "Could not open inspectors: " + ex.Message;
            WorldTelemetryMod.LogWarning("Finding inspector targets failed: " + ex);
        }
        if (failure is not null)
        {
            Finish(report, kind, failure);
            yield break;
        }
        ClearOwn(world);
        int failed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            try
            {
                Slot panel = world.LocalUserSpace.AddSlot(InspectorName);
                Spawned.Add(panel);
                SceneInspector inspector = panel.AttachComponent<SceneInspector>();
                inspector.Root.Target = entry.Slot;
                inspector.ComponentView.Target = targets[i];
            }
            catch (Exception ex)
            {
                failed++;
                WorldTelemetryMod.LogWarning("Opening an inspector failed: " + ex);
            }
            if ((i + 1) % BatchSize == 0)
                yield return Context.WaitForNextUpdate();
        }
        yield return Context.WaitForNextUpdate();
        string message;
        try
        {
            Arrange(world, Spawned);
            string limited = _totalTargets > Spawned.Count + failed ? $" There are {_totalTargets} slots with this problem, the {MaximumInspectors} biggest are shown" : "";
            message = $"Opened {Spawned.Count} {(Spawned.Count == 1 ? "inspector" : "inspectors")} showing the slots with the problem{(failed > 0 ? $", {failed} failed" : "")}.{(limited.Length > 0 ? limited + "." : "")}";
        }
        catch (Exception ex)
        {
            message = "Could not place the inspectors: " + ex.Message;
            WorldTelemetryMod.LogWarning("Placing inspectors failed: " + ex);
        }
        Finish(report, kind, message);
    }

    private static void Finish(Action<string> report, WarningKind kind, string message)
    {
        WorldTelemetryMod.Log("Inspect warning " + kind + ": " + message);
        World? userspace = Userspace.UserspaceWorld;
        userspace?.RunSynchronously(() => report(message));
    }

    private static string? Targets(Snapshot snapshot, ObjectEntry entry, WarningKind kind, List<Slot> targets)
    {
        World world = snapshot.World;
        if (world.IsDestroyed || entry.Slot.IsDestroyed)
            return $"{entry.Name} no longer exists.";

        if (!Access.CanInspect(world))
            return "You do not have permission to open inspectors in this world, so none were opened.";

        List<Component> found = Find(snapshot, entry, kind);
        var seen = new HashSet<Slot>(ReferenceEqualityComparer.Instance);
        foreach (Component component in found)
            if (!component.Slot.IsDestroyed && seen.Add(component.Slot))
                targets.Add(component.Slot);

        if (targets.Count == 0)
            return "Nothing specific to open: the items behind this warning were not found, or it is about the object as a whole.";

        _totalTargets = targets.Count;
        if (targets.Count > MaximumInspectors)
            targets.RemoveRange(MaximumInspectors, targets.Count - MaximumInspectors);

        return null;
    }

    private static void ClearOwn(World world)
    {
        foreach (Slot old in Spawned.ToList())
            if (!old.IsDestroyed && old.World == world && old.Parent == world.LocalUserSpace && old.Name == InspectorName)
                old.Destroy();

        Spawned.Clear();
    }

    private static int PositionIndex(int i) => i % 2 == 1 ? (i + 1) / 2 : -(i / 2);

    private static void Arrange(World world, List<Slot> slots)
    {
        slots.RemoveAll(slot => slot.IsDestroyed);
        if (slots.Count == 0)
            return;

        Slot first = slots[0];
        first.PositionInFrontOfUser(float3.Backward);
        UserRoot? root = world.LocalUser?.Root;
        float3 origin = first.GlobalPosition;
        floatQ rotation = first.GlobalRotation;
        float3 scale = first.GlobalScale;
        float3 head = root?.HeadPosition ?? origin;
        if (root is not null && (!origin.IsValid() || MathX.Distance(origin, head) > 3f))
        {
            floatQ facing = root.HeadFacingRotation;
            origin = head + facing * float3.Forward * 0.7f;
            rotation = facing * floatQ.AxisAngle(float3.Up, 180f);
            WorldTelemetryMod.LogWarning("The engine placed the inspector far from you, so it was placed in front of your head instead.");
        }
        float2 canvasSize = first.GetComponentInChildren<Canvas>()?.Size.Value ?? new float2(PanelWidth, 1000f);
        float step = MathX.Max(canvasSize.x, 100f) * scale.x * Spacing;
        int count = slots.Count;
        float3 flat = new float3(origin.x - head.x, 0f, origin.z - head.z);
        float distance = flat.Magnitude > 0.1f ? flat.Magnitude : 0.7f;
        float3 direction = flat.Magnitude > 0.1f ? flat.Normalized : float3.Forward;
        if (count >= 3)
            distance = MathX.Max(distance, step / (2f * MathX.Sin(MathX.PI / count)));

        float angle = 2f * MathX.Asin(MathX.Min(0.999f, step / (2f * distance))) * (180f / MathX.PI);
        WorldTelemetryMod.Log($"Inspectors: {count}, ring distance {distance:F2}, step {step:F2}, scale {scale}, head {head}.");
        for (int i = 0; i < count; i++)
        {
            floatQ turn = floatQ.AxisAngle(float3.Up, angle * PositionIndex(i));
            float3 offset = turn * (direction * distance);
            slots[i].GlobalRotation = turn * rotation;
            slots[i].GlobalScale = scale;
            slots[i].GlobalPosition = new float3(head.x + offset.x, origin.y, head.z + offset.z);
        }
    }

    private static bool Usable(Slot slot) => !slot.IsLocalElement && !Selection.IsBox(slot);

    private static List<T> Under<T>(ObjectEntry entry, Predicate<T>? filter = null) where T : class => entry.Slot.GetComponentsInChildren(filter!, false, false, Usable);

    private static List<Component> Ranked(IEnumerable<(Component Component, double Weight)> items) => items.OrderByDescending(item => item.Weight).Select(item => item.Component).ToList();

    private static List<Component> UniqueAssets(IEnumerable<(IAssetProvider Provider, double Weight)> items)
    {
        var seen = new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
        var result = new List<(Component, double)>();
        foreach ((IAssetProvider provider, double weight) in items.OrderByDescending(item => item.Weight))
            if (provider is Component component && seen.Add(provider.GenericAsset))
                result.Add((component, weight));

        return Ranked(result);
    }

    private static List<Component> Find(Snapshot snapshot, ObjectEntry entry, WarningKind kind)
    {
        switch (kind)
        {
            case WarningKind.UncompressedTextures:
            case WarningKind.LargeTextures:
            {
                AssetFlags wanted = kind == WarningKind.LargeTextures ? AssetFlags.Oversized : AssetFlags.Uncompressed;
                return UniqueAssets(Under<IAssetProvider>(entry, provider => provider.GenericAsset is Texture2D texture && (AssetMeasure.TextureFlags(texture) & wanted) != 0)
                    .Select(provider => (provider, (double)((Texture2D)provider.GenericAsset).Size.x * ((Texture2D)provider.GenericAsset).Size.y)));
            }
            case WarningKind.HighPolyMeshes:
                return UniqueAssets(Under<IAssetProvider>(entry, provider => provider.GenericAsset is FrooxEngine.Mesh mesh && (mesh.Data?.TotalTriangleCount ?? 0) > AssetMeasure.HighPolyTriangles)
                    .Select(provider => (provider, (double)(((FrooxEngine.Mesh)provider.GenericAsset).Data?.TotalTriangleCount ?? 0))));
            case WarningKind.LargeAudio:
                return UniqueAssets(Under<IAssetProvider>(entry, provider => provider.GenericAsset is AudioClip clip && AssetMeasure.AudioBytes(clip.Data) > AssetMeasure.LargeAudioBytes)
                    .Select(provider => (provider, (double)AssetMeasure.AudioBytes(((AudioClip)provider.GenericAsset).Data))));
            case WarningKind.ManyMaterials:
                return Ranked(Under<MeshRenderer>(entry, renderer => renderer.Enabled && renderer.Materials.Count > 8).Select(renderer => ((Component)renderer, (double)renderer.Materials.Count)));
            case WarningKind.ShadowedLights:
                return Ranked(Under<Light>(entry, light => light.Enabled && light.LightType.Value == LightType.Point && light.ShadowType.Value != ShadowType.None).Select(light => ((Component)light, (double)light.Range.Value)));
            case WarningKind.Cameras:
                return Ranked(Under<Camera>(entry, camera => camera.Enabled && camera.RenderTexture.Target is not null).Select(camera => ((Component)camera, 1.0)));
            case WarningKind.Probes:
                return Ranked(Under<ReflectionProbe>(entry, probe => probe.Enabled && probe.ProbeType.Value == ReflectionProbeType.Realtime).Select(probe => ((Component)probe, 1.0)));
            case WarningKind.MovingColliders:
                return Ranked(Under<MeshCollider>(entry, collider => collider.Enabled && collider.Type.Value != ColliderType.NoCollision && (collider.Type.Value == ColliderType.Active || collider.Type.Value == ColliderType.CharacterController || collider.CharacterCollider.Value))
                    .Select(collider => ((Component)collider, (double)(collider.Mesh.Asset?.Data?.TotalTriangleCount ?? 0))));
            case WarningKind.BoneChains:
                return Ranked(Under<DynamicBoneChain>(entry, chain => chain.Enabled).Select(chain => ((Component)chain, (double)chain.Bones.Count)));
            case WarningKind.Blendshapes:
                return Ranked(Under<SkinnedMeshRenderer>(entry, renderer => renderer.Enabled).Select(renderer => ((Component)renderer, ActiveBlendshapeVertices(renderer))).Where(pair => pair.Item2 > 0));
            case WarningKind.MeshRebuilds:
            {
                if (snapshot.Extra is null)
                    return new List<Component>();

                return Ranked(snapshot.Extra.MeshRebuilds
                    .Where(pair => !pair.Key.IsRemoved && !pair.Key.Slot.IsDestroyed && Usable(pair.Key.Slot) && IsUnder(pair.Key.Slot, entry.Slot))
                    .Select(pair => (pair.Key, (double)pair.Value.Vertices * pair.Value.Rebuilds)));
            }
            default:
                return new List<Component>();
        }
    }

    private static bool IsUnder(Slot slot, Slot root)
    {
        for (Slot? current = slot; current is not null; current = current.Parent)
            if (current == root)
                return true;

        return false;
    }

    private static double ActiveBlendshapeVertices(SkinnedMeshRenderer renderer)
    {
        MeshX? data = renderer.Mesh.Asset?.Data;
        if (data is null)
            return 0;

        int active = 0;
        IReadOnlyList<float> weights = renderer.BlendShapeWeights;
        for (int i = 0; i < weights.Count; i++)
            if (Math.Abs(weights[i]) > 0.0001f)
                active++;

        return (double)active * data.VertexCount;
    }
}
