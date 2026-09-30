using Elements.Core;
using FrooxEngine;

namespace WorldTelemetry;

internal static class Teleporter
{
    private const float StandOffDistance = 0.8f;
    private const float MaximumExtent = 6f;

    internal static void TeleportTo(World world, Slot slot, string name, Action<string> report)
    {
        World? userspace = Userspace.UserspaceWorld;
        if (world.IsDestroyed)
        {
            report("That world is no longer open.");
            return;
        }
        world.RunSynchronously(() =>
        {
            string message;
            try
            {
                message = Teleport(world, slot, name);
            }
            catch (Exception ex)
            {
                message = "Could not teleport: " + ex.Message;
                WorldTelemetryMod.LogWarning("Teleport failed: " + ex);
            }
            userspace?.RunSynchronously(() => report(message));
        });
    }

    private static string Teleport(World world, Slot slot, string name)
    {
        if (slot.IsDestroyed)
            return $"{name} no longer exists.";

        UserRoot? root = world.LocalUser?.Root;
        if (root is null || root.IsDestroyed)
            return "Could not find your user in this world.";

        User? target = slot.GetComponent<UserRoot>()?.ActiveUser;
        if (Access.TeleportBlocked(world, target) is string blocked)
            return blocked;

        BoundingBox box = Bounds.Of(slot);
        bool bounded = box.IsValid && !box.IsEmpty && !box.IsInfinite;
        float3 center = bounded ? box.Center : slot.GlobalPosition;
        float extent = bounded ? MathX.Clamp(MathX.Max(box.Size.x, box.Size.z) * 0.5f, 0.2f, MaximumExtent) : 0.3f;
        float3 head = root.HeadPosition;
        float3 toward = new float3(center.x - head.x, 0f, center.z - head.z);
        toward = toward.Magnitude > 0.001f ? toward.Normalized : float3.Forward;
        float3 feet = center - toward * (extent + StandOffDistance);
        float floor = bounded ? box.min.y : center.y;
        root.HeadFacingDirection = toward;
        root.FeetPosition = new float3(feet.x, floor, feet.z);
        return $"Teleported next to {name}.";
    }
}
