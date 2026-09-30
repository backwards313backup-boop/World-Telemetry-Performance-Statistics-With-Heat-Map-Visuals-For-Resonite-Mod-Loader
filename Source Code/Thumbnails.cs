using Elements.Core;
using FrooxEngine;

namespace WorldTelemetry;

internal static class Thumbnails
{
    private const int Size = 128;
    private const float FieldOfView = 40f;
    private const long RenderTimeoutMs = 15000;

    private sealed record Request(World World, Slot Slot, RefID Id, Action<Uri?> Apply);

    private static readonly Dictionary<(World World, RefID Id), Uri?> Cache = new();
    private static readonly Queue<Request> Pending = new();
    private static int _generation;
    private static bool _busy;
    private static long _busySince;
    private static bool _loggedFailure;

    internal static bool TryGet(World world, RefID id, out Uri? uri) => Cache.TryGetValue((world, id), out uri);

    internal static void Enqueue(World world, Slot slot, RefID id, Action<Uri?> apply)
    {
        if (Cache.TryGetValue((world, id), out Uri? known))
        {
            apply(known);
            return;
        }
        Pending.Enqueue(new Request(world, slot, id, apply));
    }

    internal static void ClearQueue()
    {
        Pending.Clear();
    }

    internal static void Forget(World keep)
    {
        foreach ((World World, RefID Id) key in Cache.Keys.Where(key => key.World != keep).ToList())
            Cache.Remove(key);
    }

    internal static void ForgetClosedWorlds()
    {
        foreach ((World World, RefID Id) key in Cache.Keys.Where(key => key.World.IsDestroyed).ToList())
            Cache.Remove(key);
    }

    internal static void Tick()
    {
        if (_busy && Environment.TickCount64 - _busySince > RenderTimeoutMs)
        {
            _busy = false;
            _generation++;
        }
        if (Heatmap.Building)
            return;

        while (!_busy && Pending.Count > 0)
        {
            Request request = Pending.Dequeue();
            if (request.World.IsDestroyed)
                continue;

            if (Cache.TryGetValue((request.World, request.Id), out Uri? known))
            {
                request.Apply(known);
                continue;
            }
            Render(request);
        }
    }

    private static void Render(Request request)
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null)
            return;

        _busy = true;
        _busySince = Environment.TickCount64;
        int generation = _generation;
        World world = request.World;
        Slot slot = request.Slot;
        world.RunSynchronously(() =>
        {
            if (slot.IsDestroyed)
            {
                Finish(userspace, generation, request, null);
                return;
            }
            BoundingBox box;
            try
            {
                box = Bounds.Of(slot);
            }
            catch (Exception)
            {
                Finish(userspace, generation, request, null);
                return;
            }
            if (!box.IsValid || box.IsEmpty || box.IsInfinite)
            {
                Finish(userspace, generation, request, null);
                return;
            }
            float reach = MathX.Max(box.Size.Magnitude, 0.05f);
            float3 source = box.Center + new float3(0.55f, 0.45f, -0.7f).Normalized * reach * 2f;
            List<Slot> exclude = Heatmap.OverlaySlots(world);
            exclude.AddRange(Selection.Slots(world));
            world.Coroutines.StartTask(async () =>
            {
                Uri? uri = null;
                try
                {
                    uri = await slot.RenderToAsset(new int2(Size, Size), source, FieldOfView, "webp", 85, exclude);
                }
                catch (Exception ex)
                {
                    if (!_loggedFailure)
                    {
                        _loggedFailure = true;
                        WorldTelemetryMod.LogWarning("Could not render an object thumbnail: " + ex.Message);
                    }
                }
                Finish(userspace, generation, request, uri);
            });
        });
    }

    private static void Finish(World userspace, int generation, Request request, Uri? uri)
    {
        userspace.RunSynchronously(() =>
        {
            Cache[(request.World, request.Id)] = uri;
            if (generation != _generation)
                return;

            _busy = false;
            request.Apply(uri);
        });
    }
}
