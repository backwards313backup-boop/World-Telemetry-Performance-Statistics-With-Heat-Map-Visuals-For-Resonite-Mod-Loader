using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace WorldTelemetry;

internal sealed class ScrollBar
{
    internal const float Width = 18f;
    private const float MinimumThumb = 0.06f;
    private const float Epsilon = 0.001f;

    private readonly ScrollRect _scroll;
    private readonly bool _horizontal;
    private readonly Slot _thumbSlot;
    private readonly RectTransform _thumb;
    private float _size = -1f;
    private float _top = -1f;

    private ScrollBar(ScrollRect scroll, Slot thumbSlot, bool horizontal)
    {
        _scroll = scroll;
        _horizontal = horizontal;
        _thumbSlot = thumbSlot;
        _thumb = thumbSlot.GetComponent<RectTransform>();
    }

    internal bool IsValid => !_scroll.IsDestroyed && !_thumbSlot.IsDestroyed;

    internal static ScrollBar Create(UIBuilder ui, ScrollRect scroll, bool horizontal = false)
    {
        ui.PushStyle();
        ui.Style.MinWidth = horizontal ? -1f : Width;
        ui.Style.PreferredWidth = horizontal ? -1f : Width;
        ui.Style.FlexibleWidth = horizontal ? 1f : -1f;
        ui.Style.MinHeight = horizontal ? Width : -1f;
        ui.Style.PreferredHeight = horizontal ? Width : -1f;
        ui.Style.FlexibleHeight = horizontal ? -1f : 1f;
        Slot track = ui.Next(horizontal ? "Horizontal Scroll Bar" : "Scroll Bar");
        if (track.GetComponent<LayoutElement>() is LayoutElement element)
        {
            element.UseZeroMetrics.Value = true;
            if (horizontal)
                element.FlexibleHeight.Value = 0f;
            else
                element.FlexibleWidth.Value = 0f;
        }
        Image background = track.AttachComponent<Image>();
        background.Tint.Value = new colorX(1f, 1f, 1f, 0.07f);
        Button button = track.AttachComponent<Button>();
        button.PassThroughVerticalMovement.Value = false;
        button.PassThroughHorizontalMovement.Value = false;
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = new colorX(1f, 1f, 1f, 0.07f);
        hover.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.14f);
        hover.PressColor.Value = new colorX(1f, 1f, 1f, 0.2f);
        hover.DisabledColor.Value = colorX.Clear;

        Slot thumb = track.AddSlot("Thumb");
        RectTransform rect = thumb.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = horizontal ? new float2(0f, 0.2f) : new float2(0.2f, 0f);
        rect.AnchorMax.Value = horizontal ? new float2(1f, 0.8f) : new float2(0.8f, 1f);
        thumb.AttachComponent<Image>().Tint.Value = RadiantUI_Constants.Hero.CYAN.SetA(0.55f);
        ui.PopStyle();

        var bar = new ScrollBar(scroll, thumb, horizontal);
        button.LocalPressed += (_, data) => bar.Jump(horizontal ? data.normalizedPressPoint.x : data.normalizedPressPoint.y);
        button.LocalPressing += (_, data) => bar.Jump(horizontal ? data.normalizedPressPoint.x : data.normalizedPressPoint.y);
        return bar;
    }

    private float VisibleFraction()
    {
        RectTransform content = _scroll.RectTransform;
        RectTransform? viewport = content?.RectParent;
        if (content is null || viewport is null)
            return 1f;

        float2 contentSize = content.LocalComputeRect.size;
        float2 viewportSize = viewport.LocalComputeRect.size;
        float contentLength = _horizontal ? contentSize.x : contentSize.y;
        float viewportLength = _horizontal ? viewportSize.x : viewportSize.y;
        if (contentLength <= 0f || viewportLength <= 0f)
            return 1f;

        return MathX.Clamp(viewportLength / contentLength, 0f, 1f);
    }

    internal void Update()
    {
        if (!IsValid)
            return;

        float visible = VisibleFraction();
        if (visible >= 0.999f)
        {
            if (_thumbSlot.ActiveSelf)
                _thumbSlot.ActiveSelf = false;

            _size = 1f;
            return;
        }
        if (!_thumbSlot.ActiveSelf)
            _thumbSlot.ActiveSelf = true;

        float size = MathX.Max(MinimumThumb, visible);
        float2 normalized = _scroll.NormalizedPosition.Value;
        float position = MathX.Clamp01(_horizontal ? normalized.x : normalized.y);
        float top = position * (1f - size);
        if (MathX.Abs(size - _size) < Epsilon && MathX.Abs(top - _top) < Epsilon)
            return;

        _size = size;
        _top = top;
        if (_horizontal)
        {
            _thumb.AnchorMin.Value = new float2(top, 0.2f);
            _thumb.AnchorMax.Value = new float2(top + size, 0.8f);
            return;
        }
        _thumb.AnchorMin.Value = new float2(0.2f, 1f - top - size);
        _thumb.AnchorMax.Value = new float2(0.8f, 1f - top);
    }

    private void Jump(float press)
    {
        if (!IsValid)
            return;

        float visible = VisibleFraction();
        if (visible >= 0.999f)
            return;

        float size = MathX.Max(MinimumThumb, visible);
        float along = _horizontal ? MathX.Clamp01(press) : 1f - MathX.Clamp01(press);
        float position = MathX.Clamp01((along - size * 0.5f) / (1f - size));
        float2 current = _scroll.NormalizedPosition.Value;
        _scroll.NormalizedPosition.Value = _horizontal ? new float2(position, current.y) : new float2(current.x, position);
    }
}
