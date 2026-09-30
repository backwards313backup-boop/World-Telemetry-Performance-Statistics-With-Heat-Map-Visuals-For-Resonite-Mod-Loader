using Elements.Core;
using FrooxEngine;

namespace WorldTelemetry;

internal static class Selection
{
    private const long RefreshMs = 250;
    internal const string BoxName = "Selection (WorldTelemetry)";
    private const float MoveThreshold = 0.002f;
    private const float LabelGap = 0.04f;

    private static World? _world;
    private static Slot? _target;
    private static Slot? _box;
    private static TubeBoxMesh? _mesh;
    private static Slot? _labelSlot;
    private static TextRenderer? _label;
    private static string _labelText = "";
    private static bool _shared;
    private static long _next;
    private static bool _remoteCleared;

    internal static void Show(World world, Slot target, string label)
    {
        _labelText = label;
        if (_world == world && _target == target && _shared == (WorldTelemetryMod.ShareSelection && Access.CanSpawn(world)))
        {
            _next = 0;
            return;
        }
        Clear();
        _labelText = label;
        _world = world;
        _target = target;
        _next = 0;
    }

    internal static bool IsBox(Slot slot)
    {
        if (slot.Tag != Scanner.OwnTag || slot.IsPersistent)
            return false;

        if (slot.Name == BoxName)
            return slot.Parent is Slot parent && parent.IsRootSlot;

        return slot.Parent is Slot box && box.Name == BoxName && box.Tag == Scanner.OwnTag && !box.IsPersistent && box.Parent is Slot root && root.IsRootSlot;
    }

    internal static void SetLabel(string label)
    {
        if (_labelText == label)
            return;

        _labelText = label;
        _next = 0;
    }

    internal static bool TakeRemoteClear()
    {
        bool cleared = _remoteCleared;
        _remoteCleared = false;
        return cleared;
    }

    internal static void ClearAll(World world)
    {
        Clear();
        if (world.IsDestroyed)
            return;

        world.RunSynchronously(() =>
        {
            foreach (Slot child in world.RootSlot.Children.ToList())
            {
                if (child.IsDestroyed || child.Name != BoxName || !IsBox(child))
                    continue;

                try
                {
                    child.Destroy();
                }
                catch (Exception)
                {
                }
            }
        });
    }

    internal static void Clear()
    {
        World? world = _world;
        Slot? box = _box;
        _world = null;
        _target = null;
        _box = null;
        _mesh = null;
        _labelSlot = null;
        _label = null;
        if (world is null || box is null || world.IsDestroyed)
            return;

        world.RunSynchronously(() =>
        {
            if (!box.IsDestroyed)
                box.Destroy();
        });
    }

    internal static List<Slot> Slots(World world)
    {
        var slots = new List<Slot>();
        if (_world == world && _box is not null && !_box.IsDestroyed)
            slots.Add(_box);

        return slots;
    }

    internal static void Tick()
    {
        World? world = _world;
        if (world is null)
            return;

        if (world.IsDestroyed || world != Telemetry.FocusedWorld)
        {
            Clear();
            return;
        }
        if (WorldTelemetryMod.SelectionBoxHidden || !Access.CanSelect(world) || (_target is Slot selected && Heatmap.IsBlocked(selected)))
        {
            if (_box is not null)
            {
                Slot box = _box;
                _box = null;
                _mesh = null;
                _labelSlot = null;
                _label = null;
                world.RunSynchronously(() =>
                {
                    if (!box.IsDestroyed)
                        box.Destroy();
                });
            }
            return;
        }
        if (_box is not null && _box.IsDestroyed)
        {
            Clear();
            _remoteCleared = true;
            return;
        }
        if (_box is not null && _shared != (WorldTelemetryMod.ShareSelection && Access.CanSpawn(world)))
        {
            Slot? target = _target;
            string label = _labelText;
            Clear();
            if (target is not null)
                Show(world, target, label);

            return;
        }
        long now = Environment.TickCount64;
        if (now < _next)
            return;

        _next = now + RefreshMs;
        Slot? current = _target;
        string text = _labelText;
        world.RunSynchronously(() => Update(world, current, text));
    }

    private static void Update(World world, Slot? target, string text)
    {
        if (world != _world || target != _target || target is null || WorldTelemetryMod.SelectionBoxHidden || !Access.CanSelect(world) || Heatmap.IsBlocked(target))
            return;

        if (target.IsDestroyed)
        {
            Clear();
            return;
        }
        BoundingBox box;
        try
        {
            box = Bounds.Of(target);
        }
        catch (Exception)
        {
            return;
        }
        bool bounded = Bounds.IsUsable(box);
        float3 center = bounded ? box.Center : target.GlobalPosition;
        float3 size = bounded ? box.Size : new float3(0.1f, 0.1f, 0.1f);
        EnsureBox(world);
        if (_box is null || _mesh is null)
            return;

        float largest = MathX.Max(size.x, MathX.Max(size.y, size.z));
        float radius = MathX.Clamp(largest * 0.005f, 0.002f, 0.04f);
        if (MathX.Distance(_box.GlobalPosition, center) > MoveThreshold)
            _box.GlobalPosition = center;

        if (_box.GlobalRotation != floatQ.Identity)
            _box.GlobalRotation = floatQ.Identity;

        if (MathX.Distance(_mesh.Size.Value, size) > MoveThreshold)
            _mesh.Size.Value = size;

        if (MathX.Abs(_mesh.TubeRadius.Value - radius) > 0.0005f)
            _mesh.TubeRadius.Value = radius;

        if (_labelSlot is null || _label is null)
            return;

        float scale = MathX.Clamp(largest * 0.072f, 0.03f, 0.3f);
        float3 position = float3.Up * (size.y * 0.5f + LabelGap + radius);
        if (MathX.Distance(_labelSlot.LocalPosition, position) > MoveThreshold)
            _labelSlot.LocalPosition = position;

        if (MathX.Abs(_labelSlot.LocalScale.x - scale) > 0.001f)
            _labelSlot.LocalScale = float3.One * scale;

        if (_label.Text.Value != text)
            _label.Text.Value = text;
    }

    private static void EnsureBox(World world)
    {
        if (_box is not null && !_box.IsDestroyed && _mesh is not null && !_mesh.IsDestroyed)
            return;

        _shared = WorldTelemetryMod.ShareSelection && Access.CanSpawn(world);
        Slot box = _shared ? world.AddSlot(BoxName, false) : world.AddLocalSlot(BoxName, false);
        box.Tag = Scanner.OwnTag;
        if (_shared && world.LocalUser is User user)
            box.DestroyWhenUserLeaves(user);

        box.GlobalScale = float3.One;
        AttachedModel<TubeBoxMesh, OverlayFresnelMaterial> model = box.AttachMesh<TubeBoxMesh, OverlayFresnelMaterial>();
        model.mesh.UVScale.Value *= 0.5f;
        StaticTexture2D stripe = box.AttachComponent<StaticTexture2D>();
        stripe.URL.Value = OfficialAssets.Common.Misc.TransparentStripe;
        stripe.FilterMode.Value = Renderite.Shared.TextureFilterMode.Anisotropic;
        model.material.SetTexture(stripe);
        Panner2D panner = box.AttachComponent<Panner2D>();
        panner.Speed = float2.One;
        panner.Repeat = float2.One;
        panner.Target = model.material.FrontNearTextureOffset;
        model.material.FrontFarTextureOffset.DriveFrom(model.material.FrontNearTextureOffset);
        model.material.BehindNearTextureOffset.DriveFrom(model.material.FrontNearTextureOffset);
        model.material.BehindFarTextureOffset.DriveFrom(model.material.FrontNearTextureOffset);
        GizmoHelper.SetupMaterial(model.material, colorX.Red);
        model.renderer.ShadowCastMode.Value = Renderite.Shared.ShadowCastMode.Off;

        Slot labelSlot = box.AddSlot("Stats");
        labelSlot.Tag = Scanner.OwnTag;
        LookAtUser look = labelSlot.AttachComponent<LookAtUser>();
        look.TargetAtLocalUser.Value = true;
        look.RotationOffset.Value = floatQ.AxisAngle(float3.Up, 180f);
        TextRenderer label = labelSlot.AttachComponent<TextRenderer>();
        TextUnlitMaterial material = labelSlot.AttachComponent<TextUnlitMaterial>();
        material.FaceDilate.Value = 0.2f;
        material.OutlineThickness.Value = 0.25f;
        material.OutlineColor.Value = new colorX(0.2f, 0f, 0f);
        label.Material.Target = material;
        label.Size.Value = 1f;
        label.HorizontalAlign.Value = Elements.Assets.TextHorizontalAlignment.Center;
        label.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Bottom;
        label.Color.Value = colorX.White;
        _box = box;
        _mesh = model.mesh;
        _labelSlot = labelSlot;
        _label = label;
    }
}
