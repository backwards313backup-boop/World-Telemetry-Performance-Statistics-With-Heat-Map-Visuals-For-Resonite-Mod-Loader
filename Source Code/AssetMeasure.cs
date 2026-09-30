using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.PhotonDust;
using Renderite.Shared;

namespace WorldTelemetry;

internal enum AssetKind
{
    Texture,
    Mesh,
    Audio,
    Other
}

[Flags]
internal enum AssetFlags
{
    None = 0,
    Uncompressed = 1,
    Oversized = 2,
    HighPoly = 4,
    LargeAudio = 8
}

internal readonly record struct AssetSize(AssetKind Kind, long Bytes, AssetFlags Flags = AssetFlags.None);

internal sealed class ExtraTotals
{
    internal int BoneChains;
    internal int Bones;
    internal int AudioOutputs;
    internal double Particles;
    internal double MeshRebuildsPerSecond;
    internal double RebuiltVerticesPerSecond;
    internal double NetUpdatesPerSecond;
    internal double NetInBytesPerSecond;
    internal double NetOutBytesPerSecond;

    internal double NetBytesPerSecond => NetInBytesPerSecond + NetOutBytesPerSecond;

    internal bool IsEmpty => BoneChains == 0 && AudioOutputs == 0 && Particles == 0 && MeshRebuildsPerSecond == 0 && NetUpdatesPerSecond == 0 && NetInBytesPerSecond == 0 && NetOutBytesPerSecond == 0;

    internal void Add(ExtraTotals other)
    {
        BoneChains += other.BoneChains;
        Bones += other.Bones;
        AudioOutputs += other.AudioOutputs;
        Particles += other.Particles;
        MeshRebuildsPerSecond += other.MeshRebuildsPerSecond;
        RebuiltVerticesPerSecond += other.RebuiltVerticesPerSecond;
        NetUpdatesPerSecond += other.NetUpdatesPerSecond;
        NetInBytesPerSecond += other.NetInBytesPerSecond;
        NetOutBytesPerSecond += other.NetOutBytesPerSecond;
    }
}

internal sealed class AssetTotals
{
    internal long TextureBytes;
    internal long MeshBytes;
    internal long AudioBytes;
    internal long OtherBytes;
    internal int Textures;
    internal int Meshes;
    internal int AudioClips;
    internal int Others;
    internal int UncompressedTextures;
    internal long UncompressedBytes;
    internal int LargeTextures;
    internal int HighPolyMeshes;
    internal int LargeAudio;

    internal long TotalBytes => TextureBytes + MeshBytes + AudioBytes + OtherBytes;

    internal AssetTotals Copy() => (AssetTotals)MemberwiseClone();

    internal void Add(AssetSize size)
    {
        if ((size.Flags & AssetFlags.Uncompressed) != 0)
        {
            UncompressedTextures++;
            UncompressedBytes += size.Bytes;
        }
        if ((size.Flags & AssetFlags.Oversized) != 0)
            LargeTextures++;

        if ((size.Flags & AssetFlags.HighPoly) != 0)
            HighPolyMeshes++;

        if ((size.Flags & AssetFlags.LargeAudio) != 0)
            LargeAudio++;

        switch (size.Kind)
        {
            case AssetKind.Texture:
                TextureBytes += size.Bytes;
                Textures++;
                break;
            case AssetKind.Mesh:
                MeshBytes += size.Bytes;
                Meshes++;
                break;
            case AssetKind.Audio:
                AudioBytes += size.Bytes;
                AudioClips++;
                break;
            default:
                OtherBytes += size.Bytes;
                Others++;
                break;
        }
    }
}

internal enum MaterialKind
{
    Opaque,
    Cutout,
    Transparent
}

internal readonly record struct RendererSphere(float3 Center, float Radius, int Draws, bool CastsShadows);

internal sealed class GpuTotals
{
    internal long Triangles;
    internal int DrawCalls;
    internal int Renderers;
    internal int TransparentDraws;
    internal long TransparentTriangles;
    internal int CutoutDraws;
    internal long SkinnedVertices;
    internal long BlendshapeVertices;
    internal int Cameras;
    internal int RealtimeProbes;
    internal long Particles;
    internal int ManyMaterialRenderers;

    internal bool IsEmpty => Renderers == 0 && Cameras == 0 && RealtimeProbes == 0 && Particles == 0;

    internal void Add(GpuTotals other)
    {
        Triangles += other.Triangles;
        DrawCalls += other.DrawCalls;
        Renderers += other.Renderers;
        TransparentDraws += other.TransparentDraws;
        TransparentTriangles += other.TransparentTriangles;
        CutoutDraws += other.CutoutDraws;
        SkinnedVertices += other.SkinnedVertices;
        BlendshapeVertices += other.BlendshapeVertices;
        Cameras += other.Cameras;
        RealtimeProbes += other.RealtimeProbes;
        Particles += other.Particles;
        ManyMaterialRenderers += other.ManyMaterialRenderers;
    }

    internal double Score =>
        Triangles * 0.00005
        + DrawCalls * 1.0
        + TransparentDraws * 1.5
        + TransparentTriangles * 0.0001
        + CutoutDraws * 0.3
        + SkinnedVertices * 0.0001
        + BlendshapeVertices * 0.00005
        + Cameras * 200.0
        + RealtimeProbes * 300.0
        + Particles * 0.002;
}

internal sealed class LightTotals
{
    internal int Lights;
    internal int Shadowed;
    internal int Directional;
    internal int ShadowedPoint;
    internal long LitDraws;
    internal long ShadowDraws;
    internal double Score;
    internal int Measured;
    internal double MeasuredMs;
    internal double ValueMs;

    internal void Add(LightTotals other)
    {
        Measured += other.Measured;
        MeasuredMs += other.MeasuredMs;
        ValueMs += other.ValueMs;
        Lights += other.Lights;
        Shadowed += other.Shadowed;
        Directional += other.Directional;
        ShadowedPoint += other.ShadowedPoint;
        LitDraws += other.LitDraws;
        ShadowDraws += other.ShadowDraws;
        Score += other.Score;
    }
}

internal sealed class PhysicsTotals
{
    internal int Colliders;
    internal int Primitives;
    internal int MeshColliders;
    internal int Hulls;
    internal long CollisionTriangles;
    internal long HullSourceVertices;
    internal int Moving;
    internal int MovingMeshColliders;
    internal int Triggers;
    internal double Score;
    internal int Measured;
    internal double MeasuredMs;
    internal double ValueMs;

    internal void Add(PhysicsTotals other)
    {
        Measured += other.Measured;
        MeasuredMs += other.MeasuredMs;
        ValueMs += other.ValueMs;
        Colliders += other.Colliders;
        Primitives += other.Primitives;
        MeshColliders += other.MeshColliders;
        Hulls += other.Hulls;
        CollisionTriangles += other.CollisionTriangles;
        HullSourceVertices += other.HullSourceVertices;
        Moving += other.Moving;
        MovingMeshColliders += other.MovingMeshColliders;
        Triggers += other.Triggers;
        Score += other.Score;
    }
}

internal sealed class AssetMeasure
{
    private readonly Dictionary<IAsset, AssetSize> _sizes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAssetProvider, IAssetProvider[]> _dependencies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAssetProvider, IAsset[]> _closures = new(ReferenceEqualityComparer.Instance);
    private readonly List<IAssetRef> _refs = new();

    internal void AddAssets(HashSet<IAssetProvider> providers, AssetTotals totals, HashSet<IAsset> worldAssets)
    {
        var assets = new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
        foreach (IAssetProvider provider in providers)
            foreach (IAsset asset in Closure(provider))
                if (assets.Add(asset))
                {
                    totals.Add(Bytes(asset));
                    worldAssets.Add(asset);
                }
    }

    internal IAsset[] Closure(IAssetProvider root)
    {
        if (_closures.TryGetValue(root, out IAsset[]? known))
            return known;

        var visited = new HashSet<IAssetProvider>(ReferenceEqualityComparer.Instance);
        var assets = new HashSet<IAsset>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IAssetProvider>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            IAssetProvider provider = pending.Pop();
            if (!visited.Add(provider) || provider.IsDestroyed)
                continue;

            IAsset? asset = null;
            try
            {
                asset = provider.GenericAsset;
            }
            catch (Exception)
            {
            }
            if (asset is not null)
                assets.Add(asset);

            foreach (IAssetProvider dependency in Dependencies(provider))
                pending.Push(dependency);
        }
        IAsset[] closure = assets.ToArray();
        _closures[root] = closure;
        return closure;
    }

    private IAssetProvider[] Dependencies(IAssetProvider provider)
    {
        if (_dependencies.TryGetValue(provider, out IAssetProvider[]? known))
            return known;

        IAssetProvider[] found = [];
        if (provider is Worker worker)
        {
            _refs.Clear();
            try
            {
                worker.GetSyncMembers(_refs);
            }
            catch (Exception)
            {
            }
            found = _refs.Select(reference => reference.Target as IAssetProvider)
                .Where(target => target is not null && !ReferenceEquals(target, provider))
                .Cast<IAssetProvider>()
                .ToArray();
        }
        _dependencies[provider] = found;
        return found;
    }

    internal AssetSize Bytes(IAsset asset)
    {
        if (_sizes.TryGetValue(asset, out AssetSize known))
            return known;

        AssetSize size;
        try
        {
            size = asset switch
            {
                Texture2D texture => new AssetSize(AssetKind.Texture, TextureBytes(texture.Size.x, texture.Size.y, 1, texture.Format, texture.MipMapCount), TextureFlags(texture)),
                Cubemap cubemap => new AssetSize(AssetKind.Texture, 6 * TextureBytes(cubemap.Size.x, cubemap.Size.y, 1, cubemap.Format, cubemap.MipMapCount)),
                Texture3D volume => new AssetSize(AssetKind.Texture, TextureBytes(volume.Size.x, volume.Size.y, volume.Size.z, volume.Format, 1)),
                RenderTexture render => new AssetSize(AssetKind.Texture, (long)Math.Max(0, render.Size.x) * Math.Max(0, render.Size.y) * 8),
                FrooxEngine.Mesh mesh => new AssetSize(AssetKind.Mesh, MeshBytes(mesh.Data), (mesh.Data?.TotalTriangleCount ?? 0) > HighPolyTriangles ? AssetFlags.HighPoly : AssetFlags.None),
                AudioClip clip => new AssetSize(AssetKind.Audio, AudioBytes(clip.Data), AudioBytes(clip.Data) > LargeAudioBytes ? AssetFlags.LargeAudio : AssetFlags.None),
                _ => new AssetSize(AssetKind.Other, 0)
            };
        }
        catch (Exception)
        {
            size = new AssetSize(AssetKind.Other, 0);
        }
        _sizes[asset] = size;
        return size;
    }

    internal const int HighPolyTriangles = 150_000;
    internal const long LargeAudioBytes = 20L << 20;
    internal const int UncompressedMinimumSize = 512;
    internal const int OversizedTextureSize = 4096;

    internal static AssetFlags TextureFlags(Texture2D texture)
    {
        int largest = Math.Max(texture.Size.x, texture.Size.y);
        AssetFlags flags = AssetFlags.None;
        if (largest >= UncompressedMinimumSize && !texture.Format.IsBlockCompressed())
            flags |= AssetFlags.Uncompressed;

        if (largest >= OversizedTextureSize)
            flags |= AssetFlags.Oversized;

        return flags;
    }

    private static long TextureBytes(int width, int height, int depth, TextureFormat format, int mips)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            return 0;

        RenderVector2i block = format.BlockSize();
        int blockX = Math.Max(1, block.x);
        int blockY = Math.Max(1, block.y);
        double bitsPerPixel = format.GetBitsPerPixel();
        long bytes = 0;
        for (int mip = 0; mip < Math.Max(1, mips); mip++)
        {
            long paddedWidth = (width + blockX - 1) / blockX * (long)blockX;
            long paddedHeight = (height + blockY - 1) / blockY * (long)blockY;
            bytes += (long)Math.Ceiling(paddedWidth * paddedHeight * depth * bitsPerPixel / 8);
            if (width == 1 && height == 1)
                break;

            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return bytes;
    }

    private static long MeshBytes(MeshX? mesh)
    {
        if (mesh is null)
            return 0;

        long vertices = mesh.VertexCount;
        long stride = 12 + (mesh.HasNormals ? 12 : 0) + (mesh.HasTangents ? 16 : 0) + (mesh.HasColors ? 16 : 0) + mesh.UV_ChannelCount * 8L;
        long indexSize = vertices > ushort.MaxValue ? 4 : 2;
        long indices = ((long)mesh.TotalTriangleCount * 3 + mesh.TotalPointCount) * indexSize;
        long bones = mesh.HasBoneBindings ? vertices + vertices * 4 * 8 + mesh.BoneCount * 64L : 0;
        long shapeLayers = 1 + (mesh.HasBlendshapeNormals ? 1 : 0) + (mesh.HasBlendshapeTangents ? 1 : 0);
        long shapes = mesh.BlendShapeCount * vertices * 12 * shapeLayers;
        return vertices * stride + indices + bones + shapes;
    }

    internal static long AudioBytes(AudioX? audio)
    {
        if (audio is null)
            return 0;

        return (long)audio.SampleCount * Math.Max(1, audio.ChannelCount) * sizeof(float);
    }

    private readonly Dictionary<IAssetProvider, MaterialKind> _materialKinds = new(ReferenceEqualityComparer.Instance);

    internal readonly List<RendererSphere> Spheres = new();

    internal void AddGpu(Component component, GpuTotals gpu)
    {
        if (!component.Enabled)
            return;

        try
        {
            switch (component)
            {
                case MeshRenderer renderer:
                    AddRenderer(renderer, gpu);
                    break;
                case Camera camera:
                    if (camera.RenderTexture.Target is not null)
                        gpu.Cameras++;

                    break;
                case ReflectionProbe probe:
                    if (probe.ProbeType.Value == ReflectionProbeType.Realtime)
                        gpu.RealtimeProbes++;

                    break;
                case ParticleSystem particles:
                    gpu.Particles += Math.Max(0, particles.MaxParticleCount.Value);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }

    private void AddRenderer(MeshRenderer renderer, GpuTotals gpu)
    {
        int materials = renderer.Materials.Count;
        FrooxEngine.Mesh? mesh = renderer.Mesh.Asset;
        MeshX? data = mesh?.Data;
        gpu.Renderers++;
        if (mesh is null || data is null || materials == 0)
            return;

        int submeshes = Math.Max(1, data.SubmeshCount);
        long triangles = data.TotalTriangleCount;
        long perSubmesh = triangles / submeshes;
        long drawn = materials <= submeshes ? perSubmesh * materials : triangles + (materials - submeshes) * perSubmesh;
        gpu.Triangles += drawn;
        gpu.DrawCalls += materials;
        if (materials > 8)
            gpu.ManyMaterialRenderers++;
        long perDraw = drawn / materials;
        for (int i = 0; i < materials; i++)
        {
            switch (Kind(renderer.Materials[i]))
            {
                case MaterialKind.Transparent:
                    gpu.TransparentDraws++;
                    gpu.TransparentTriangles += perDraw;
                    break;
                case MaterialKind.Cutout:
                    gpu.CutoutDraws++;
                    break;
            }
        }
        AddSphere(renderer, mesh, materials);

        if (renderer is not SkinnedMeshRenderer skinned)
            return;

        if (data.BoneCount > 0 || data.BlendShapeCount > 0)
            gpu.SkinnedVertices += data.VertexCount;

        IReadOnlyList<float> weights = skinned.BlendShapeWeights;
        int active = 0;
        for (int i = 0; i < weights.Count; i++)
            if (Math.Abs(weights[i]) > 0.0001f)
                active++;

        gpu.BlendshapeVertices += (long)active * data.VertexCount;
    }

    private void AddSphere(MeshRenderer renderer, FrooxEngine.Mesh mesh, int draws)
    {
        BoundingBox bounds = mesh.Bounds;
        if (!bounds.IsValid || bounds.IsEmpty || bounds.IsInfinite)
            return;

        Slot slot = renderer.Slot;
        float3 scale = slot.GlobalScale;
        float largest = MathX.Max(MathX.Abs(scale.x), MathX.Max(MathX.Abs(scale.y), MathX.Abs(scale.z)));
        float3 center = slot.LocalPointToGlobal(bounds.Center);
        Spheres.Add(new RendererSphere(center, bounds.Size.Magnitude * 0.5f * largest, draws, renderer.ShadowCastMode.Value != ShadowCastMode.Off));
    }

    internal MaterialKind Kind(IAssetProvider? material)
    {
        if (material is null)
            return MaterialKind.Opaque;

        if (_materialKinds.TryGetValue(material, out MaterialKind known))
            return known;

        MaterialKind kind = MaterialKind.Opaque;
        if (material is Worker worker)
        {
            try
            {
                if (worker.TryGetField("BlendMode")?.BoxedValue is BlendMode blend)
                {
                    kind = blend switch
                    {
                        BlendMode.Alpha or BlendMode.Transparent or BlendMode.Additive or BlendMode.Multiply => MaterialKind.Transparent,
                        BlendMode.Cutout => MaterialKind.Cutout,
                        _ => MaterialKind.Opaque
                    };
                }
                if (kind == MaterialKind.Opaque && worker.TryGetField("RenderQueue")?.BoxedValue is int queue && queue > 2500)
                    kind = MaterialKind.Transparent;
            }
            catch (Exception)
            {
            }
        }
        _materialKinds[material] = kind;
        return kind;
    }

    internal static void AddPhysics(Component component, PhysicsTotals physics)
    {
        if (component is not Collider collider || !collider.Enabled)
            return;

        try
        {
            ColliderType type = collider.Type.Value;
            if (type == ColliderType.NoCollision)
                return;

            double cost;
            switch (collider)
            {
                case MeshCollider meshCollider:
                {
                    long triangles = meshCollider.Mesh.Asset?.Data?.TotalTriangleCount ?? 0;
                    physics.MeshColliders++;
                    physics.CollisionTriangles += triangles;
                    cost = 5 + triangles / 200.0;
                    if (meshCollider.Sidedness.Value.ToString().Contains("Dual", StringComparison.OrdinalIgnoreCase))
                        cost *= 1.5;

                    break;
                }
                case ConvexHullCollider hull:
                {
                    long vertices = hull.Mesh.Asset?.Data?.VertexCount ?? 0;
                    physics.Hulls++;
                    physics.HullSourceVertices += vertices;
                    cost = 4 + vertices / 250.0;
                    break;
                }
                default:
                    physics.Primitives++;
                    cost = 1;
                    break;
            }
            bool moving = type == ColliderType.Active || type == ColliderType.CharacterController || collider.CharacterCollider.Value;
            if (moving)
            {
                physics.Moving++;
                if (collider is MeshCollider)
                    physics.MovingMeshColliders++;

                cost *= 2;
            }
            if (type is ColliderType.Trigger or ColliderType.StaticTrigger or ColliderType.StaticTriggerAuto or ColliderType.HapticTrigger or ColliderType.HapticStaticTrigger)
                physics.Triggers++;

            physics.Colliders++;
            physics.Score += cost;
        }
        catch (Exception)
        {
        }
    }
}
