using Elements.Core;
using FrooxEngine;

namespace WorldTelemetry;

internal static class Bounds
{
    private static readonly Predicate<Slot> SkipOwn = slot => !Selection.IsBox(slot) && !IsTransient(slot);

    private static bool IsTransient(Slot slot)
    {
        foreach (Component component in slot.Components)
            if (component is InteractionLaser or ContextMenu)
                return true;

        return false;
    }

    internal static BoundingBox Of(Slot slot, Slot? space = null) => slot.ComputeBoundingBox(false, space!, null!, SkipOwn);

    internal static bool IsUsable(in BoundingBox box) => box.IsValid && !box.IsEmpty && !box.IsInfinite;
}
