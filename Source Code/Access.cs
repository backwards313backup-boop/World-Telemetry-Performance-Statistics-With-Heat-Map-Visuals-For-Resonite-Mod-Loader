using FrooxEngine;

namespace WorldTelemetry;

internal static class Access
{
    internal static bool CanSpawn(World world)
    {
        try
        {
            return !world.IsDestroyed && world.LocalUser is not null && world.CanSpawnObjects();
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool CanSelect(World world)
    {
        try
        {
            if (world.IsDestroyed || world.LocalUser is null)
                return false;

            return world.Permissions.Check<InteractionHandlerPermissions>(typeof(InteractionHandler), permissions => Allows(permissions, typeof(DevTool)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool CanInspect(World world) => CanSpawn(world) && CanSelect(world);

    internal static string? TeleportBlocked(World world, User? target)
    {
        try
        {
            User? local = world.LocalUser;
            Slot? root = local?.Root?.Slot;
            if (world.IsDestroyed || local is null || root is null)
                return "Could not find your user in this world.";

            if (target is not null && target != local && !target.CanJumpToUser())
                return "This world does not allow jumping to other users, so you were not teleported.";

            LocomotionController? controller = root.GetComponentInChildren<LocomotionController>();
            if (controller is null)
                return "This world gives you no way to move, so you were not teleported.";

            foreach (ILocomotionModule module in controller.LocomotionModules)
                if (module is TeleportLocomotion or NoclipLocomotion && controller.CanUseModule(module))
                    return null;

            return "This world does not allow teleporting or noclip, so you were not teleported.";
        }
        catch (Exception)
        {
            return "Could not check this world's movement permissions, so you were not teleported.";
        }
    }

    private static bool Allows(InteractionHandlerPermissions permissions, Type tool)
    {
        foreach (InteractionHandlerPermissions.ToolRule rule in permissions.ToolRules)
        {
            Type? ruleType = rule.Type.Value;
            if (ruleType is null)
                continue;

            bool matches = rule.IncludeDerived.Value ? ruleType.IsAssignableFrom(tool) : tool == ruleType;
            if (matches)
                return rule.Allow.Value;
        }
        return !permissions.AllowOnlyWhitelistedTools.Value;
    }
}
