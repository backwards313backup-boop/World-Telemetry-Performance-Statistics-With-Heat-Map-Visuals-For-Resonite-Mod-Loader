using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace WorldTelemetry;

internal enum SortMode
{
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

internal enum ViewMode
{
    Objects,
    Users,
    Hierarchy,
    EverySlot
}

internal static class DashScreen
{
    private const string SlotName = "WorldTelemetry.Screen";
    private const string ButtonLabel = "Telemetry";
    private const long OrderOffset = int.MaxValue - 2L;
    private const float RowHeight = 74f;
    private const float ThumbSize = 66f;
    private const float InfoHeight = 176f;
    private const float TabHeight = 38f;
    private const float IndentStep = 28f;
    private const float ExpandWidth = 52f;
    private const float PersistentWidth = 64f;
    private const float SizeWidth = 105f;
    private const float CpuWidth = 100f;
    private const float ComponentWidth = 95f;
    private const float FluxWidth = 95f;
    private const float BonesWidth = 95f;
    private const float NetworkWidth = 95f;
    private const float WarningsWidth = 64f;
    private const float GpuWidth = 80f;
    private const float LightWidth = 100f;
    private const float PhysicsWidth = 95f;
    private const float SlotsWidth = 75f;
    private const long StatusInterval = 250;

    private static readonly FieldInfo? ScreenButton = AccessTools.Field(typeof(RadiantDashScreen), "_button");
    private static readonly colorX SelectedColor = RadiantUI_Constants.Hero.CYAN.SetA(0.3f);
    private static readonly colorX ActiveHeaderColor = RadiantUI_Constants.Hero.CYAN;
    private static readonly colorX ActiveColor = RadiantUI_Constants.Hero.CYAN.SetA(0.35f);
    private const string MatchColor = "#ffd84a";

    private sealed class RowItem
    {
        internal required RefID Id { get; init; }
        internal required Slot Slot { get; init; }
        internal required string Name { get; init; }
        internal required Func<string> SubtitleSource { get; init; }
        private string? _subtitle;
        internal string Subtitle => _subtitle ??= SubtitleSource();
        internal required bool Persistent { get; init; }
        internal required long Size { get; init; }
        internal required double Cpu { get; init; }
        internal required double Gpu { get; init; }
        internal required double Component { get; init; }
        internal required double Flux { get; init; }
        internal required double Light { get; init; }
        internal int LightsMeasured { get; init; }
        internal int Lights { get; init; }
        internal required double Physics { get; init; }
        internal double Bones { get; init; }
        internal int BoneChains { get; init; }
        internal double Network { get; init; }
        internal int Warnings { get; init; }
        internal int PhysicsState { get; init; }
        internal required int Slots { get; init; }
        internal int Depth { get; init; }
        internal bool Expandable { get; init; }
        internal bool Open { get; init; }
        internal bool Match { get; init; }
        internal bool SearchBranch { get; init; }
        internal ObjectEntry? Entry { get; init; }
        internal SlotNode? Node { get; init; }
    }

    private sealed class RowView
    {
        internal required RowItem Item { get; set; }
        internal required int Index { get; init; }
        internal required Text Name { get; init; }
        internal required Text[] Cells { get; init; }
        internal required InteractionElement.ColorDriver Hover { get; init; }
        internal required StaticTexture2D Texture { get; init; }
        internal required RawImage Picture { get; init; }
        internal required Text Placeholder { get; init; }
        internal required colorX BaseColor { get; init; }
    }

    private static RadiantDashScreen? _screen;
    private static bool _buttonMarked;
    private enum InfoTab
    {
        Details,
        World,
        Activity,
        History
    }

    private static Text? _statusLine;
    private static Text? _infoText;
    private static Slot? _warningRoot;
    private static RefID _warningShownFor = RefID.Null;
    private static int _warningShownVersion = -1;
    private static Text? _lightLabel;
    private static Text? _physicsLabel;
    private static InfoTab _tab = InfoTab.Details;
    private static readonly Dictionary<InfoTab, InteractionElement.ColorDriver> TabButtons = new();
    private static string _detailsText = "";
    private static int _shownActivity = -1;
    private static Text? _pageLabel;
    private static Text? _profileLabel;
    private static Text? _autoLabel;
    private static Text? _viewLabel;
    private static Text? _heatLabel;
    private static InteractionElement.ColorDriver? _heatHover;
    private static TextField? _pageField;
    private static ScrollBar? _listBar;
    private static ScrollBar? _listHBar;
    private static float _pageIndent;
    private static float _listExtraWidth = -1f;
    private static ScrollBar? _infoBar;
    private static ScrollRect? _infoScroll;
    private static ScrollRect? _listScroll;
    private static int _lastListPage = -1;
    private static TextField? _searchField;
    private static Text? _matchLabel;
    private static string _search = "";
    private static string? _pendingSearch;
    private static long _pendingSearchAt;
    private static int _matchCount;
    private static RefID _revealId = RefID.Null;
    private static readonly HashSet<RefID> SearchCollapsed = new();
    private const long SearchDelayMs = 300;
    private static InteractionElement.ColorDriver? _autoHover;
    private static Text? _nameHeader;
    private static readonly Text?[] ValueHeaders = new Text?[11];
    private static Slot? _listContent;
    private static Slot? _headerRow;
    private static int _headerSort = -1;
    private static Text? _usersLabel;
    private static Text? _showAllLabel;
    private static Text? _outlineLabel;
    private static InteractionElement.ColorDriver? _showAllHover;
    private static InteractionElement.ColorDriver? _usersHover;
    private static Text? _shareLabel;
    private static Text? _rowsLabel;
    private static InteractionElement.ColorDriver? _shareHover;
    private static readonly float[] ColumnWidths = [SizeWidth, CpuWidth, ComponentWidth, FluxWidth, BonesWidth, GpuWidth, LightWidth, PhysicsWidth, NetworkWidth, WarningsWidth, SlotsWidth];
    private static readonly Dictionary<SortMode, InteractionElement.ColorDriver> SortButtons = new();
    private static readonly List<RowView> Rows = new();
    private static readonly HashSet<RefID> Expanded = new();

    private static SortMode _sort = SortMode.Size;
    private static ViewMode _view = ViewMode.Hierarchy;
    private static int _page;
    private static bool _dirty = true;
    private static int _shownVersion = -1;
    private static int _shownRowsPerPage;
    private static Snapshot? _shown;
    private static List<RowItem> _items = new();
    private static int _expandVersion;
    private static (int Version, SortMode Sort, ViewMode View, int Expand, string Search) _itemsKey = (-1, SortMode.Size, ViewMode.Hierarchy, -1, "");
    private static RefID _selected = RefID.Null;
    private static RefID _lastPressId = RefID.Null;
    private static long _lastPressTimestamp;
    private static World? _autoScanned;
    private static long _nextStatus;
    private static long _nextInfoRefresh;
    private static SortMode _rowsSort;
    private static ViewMode _rowsView;
    private const long InfoRefreshMs = 2000;

    internal static void Tick()
    {
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null || userspace.IsDestroyed)
            return;

        if (!WorldTelemetryMod.ShowDashScreen)
        {
            Close();
            return;
        }
        UserspaceRadiantDash? userspaceDash = userspace.GetRadiantDash();
        RadiantDash? dash = userspaceDash?.Dash;
        if (userspaceDash is null || dash is null)
            return;

        if (_screen is null || _screen.IsDestroyed)
            Build(dash);

        MarkButtonNonPersistent();
        ForgetClosedWorld();
        if (_screen is null || !userspaceDash.Open || !_screen.IsShown)
            return;

        World? focused = Telemetry.FocusedWorld;
        if (focused is not null && focused != _autoScanned && Telemetry.Latest?.World != focused && !Telemetry.Scanning)
        {
            _autoScanned = focused;
            Thumbnails.Forget(focused);
            Expanded.Clear();
            _expandVersion++;
            Telemetry.RequestScan(focused, ReportMode.None);
        }
        if (_creatorInfo is not null && !_creatorInfo.IsDestroyed && _creatorInfo.IconURL.Value is Uri icon && _creatorIcon is not null && _creatorIcon.URL.Value != icon)
            _creatorIcon.URL.Value = icon;

        if (Selection.TakeRemoteClear() && _selected != RefID.Null)
            Unselect(false);

        TickSearch();
        UpdateListWidth();
        _listBar?.Update();
        _listHBar?.Update();
        _infoBar?.Update();
        if (_tab == InfoTab.History && Telemetry.HistoryVersion != _chartVersion)
            RefreshInfo();

        if (_tab == InfoTab.Activity && Telemetry.ActivityVersion != _shownActivity)
        {
            _shownActivity = Telemetry.ActivityVersion;
            RefreshInfo();
        }
        if (_dirty || Telemetry.Version != _shownVersion || WorldTelemetryMod.RowsPerPage != _shownRowsPerPage)
            RebuildList();

        long now = Environment.TickCount64;
        if (now >= _nextStatus)
        {
            _nextStatus = now + StatusInterval;
            UpdateStatus();
        }
    }

    private static void ForgetClosedWorld()
    {
        if (_shown is null || !_shown.World.IsDestroyed)
            return;

        _shown = null;
        _items = new List<RowItem>();
        _itemsKey = (-1, _sort, _view, -1, "");
        Rows.Clear();
        _dirty = true;
        if (_listContent is not null && !_listContent.IsDestroyed)
            _listContent.DestroyChildren();

        UpdateDetails();
    }

    private static void Close()
    {
        if (_screen is not null && !_screen.IsDestroyed)
            _screen.CloseContainer();

        _screen = null;
        Rows.Clear();
        SortButtons.Clear();
        TabButtons.Clear();
        _dirty = true;
    }

    private static void MarkButtonNonPersistent()
    {
        if (_buttonMarked || _screen is null)
            return;

        if ((ScreenButton?.GetValue(_screen) as SyncRef<RadiantDashButton>)?.Target is not RadiantDashButton button)
            return;

        button.Slot.PersistentSelf = false;
        _buttonMarked = true;
    }

    private static void Build(RadiantDash dash)
    {
        foreach (Slot stale in dash.ScreensContainer.Children.Where(child => child.Name == SlotName).ToList())
            stale.Destroy();

        RadiantDashScreen screen = dash.AttachScreen<RadiantDashScreen>(ButtonLabel,
            RadiantUI_Constants.Hero.CYAN, OfficialAssets.Graphics.Icons.Dash.MagnifyingGlass);
        screen.Slot.Name = SlotName;
        screen.Slot.PersistentSelf = false;
        screen.Slot.OrderOffset = OrderOffset;

        Rows.Clear();
        SortButtons.Clear();
        TabButtons.Clear();
        var ui = new UIBuilder(screen.ScreenCanvas);
        RadiantUI_Constants.SetupDefaultStyle(ui, false);
        ui.Image(UserspaceRadiantDash.DEFAULT_BACKGROUND, false);
        ui.Nest();
        ui.Panel().AddFixedPadding(PanelTopPadding, 48f, 32f, 48f);
        ui.VerticalLayout(6f, 0f, Alignment.TopLeft, true, false);

        BuildTitleRow(ui);


        BuildHeatRow(ui);
        BuildSearchRow(ui);
        BuildSortRow(ui);
        BuildHeaderRow(ui);

        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot main = ui.Root;
        Slot list = ui.Next("List");
        VerticalLayout listLayout = list.AttachComponent<VerticalLayout>();
        listLayout.Spacing.Value = 2f;
        listLayout.ForceExpandWidth.Value = true;
        listLayout.ForceExpandHeight.Value = false;
        ui.NestInto(list);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot listBody = ScrollRow(ui, "List Body");
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ScrollRect listScroll = ui.ScrollArea(Alignment.TopLeft);
        _listScroll = listScroll;
        ui.VerticalLayout(4f, 0f, Alignment.TopLeft, true, false);
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        _listContent = ui.Root;
        ReturnTo(ui, listBody);
        _listBar = ScrollBar.Create(ui, listScroll);
        ReturnTo(ui, list);
        ui.Style.MinHeight = ScrollBar.Width;
        ui.Style.PreferredHeight = ScrollBar.Width;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Slot barRow = ScrollRow(ui, "List Horizontal Bar");
        FixHeight(barRow);
        _listHBar = ScrollBar.Create(ui, listScroll, true);
        _listExtraWidth = -1f;
        ui.Style.MinWidth = ScrollBar.Width;
        ui.Style.PreferredWidth = ScrollBar.Width;
        ui.Style.FlexibleWidth = -1f;
        ui.Next("Scroll Bar Space");
        ReturnTo(ui, main);

        BuildInfoPanel(ui);
        ReturnTo(ui, main);
        CheckLayout(main);
        BuildCredit(new UIBuilder(screen.ScreenCanvas));

        _screen = screen;
        _buttonMarked = false;
        _dirty = true;
        UpdateToggles();
        WorldTelemetryMod.Log("Added the World Telemetry screen to the dash.");
    }

    private const float PanelTopPadding = 68f;
    private const float VersionWidth = 760f;
    private const float VersionHeight = 28f;
    private const float CreditWidth = 204f;
    private const float CreditHeight = 34f;
    private const float CreditWordWidth = 68f;
    private const float CreditPicture = 30f;
    private const string CreatorUserId = "U-backwards";
    private const string CreatorNameColor = "#FFD700";
    private static readonly Uri CreatorIconFallback = new("resdb:///2bfe4c3df1589cc656ae78880f44bbb5dc260ded4a4c5e2fa75bce64a863b088.webp");
    private static CloudUserInfo? _creatorInfo;
    private static StaticTexture2D? _creatorIcon;

    private static void BuildCredit(UIBuilder ui)
    {
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        Slot credit = ui.Next("Creator");
        RectTransform rect = credit.GetComponent<RectTransform>() ?? credit.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(1f, 1f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.Pivot.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(-CreditWidth - 10f, -CreditHeight - 3f);
        rect.OffsetMax.Value = new float2(-10f, -3f);
        Image background = credit.AttachComponent<Image>();
        background.Tint.Value = colorX.Clear;
        Button button = credit.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = colorX.Clear;
        hover.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.15f);
        hover.PressColor.Value = new colorX(1f, 1f, 1f, 0.3f);
        hover.DisabledColor.Value = colorX.Clear;
        credit.AttachComponent<ContactLink>().UserId.Value = CreatorUserId;
        _creatorInfo = credit.AttachComponent<CloudUserInfo>();
        _creatorInfo.UserId.Value = CreatorUserId;
        _creatorIcon = credit.AttachComponent<StaticTexture2D>();
        _creatorIcon.URL.Value = CreatorIconFallback;

        Row(credit, 6f, Alignment.MiddleRight, 2f);
        ui.NestInto(credit);
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = 20f;
        ui.Style.PreferredHeight = 20f;
        Slot name = ui.Next("Name");
        Row(name, 4f, Alignment.MiddleRight);
        ui.Nest();
        ui.Style.MinWidth = CreditWordWidth;
        ui.Style.PreferredWidth = CreditWordWidth;
        CreditWord("<nobr>Created by</nobr>", Alignment.MiddleRight);
        CreditWord($"<color={CreatorNameColor}>backwards</color>", Alignment.MiddleLeft);
        ui.NestOut();

        ui.Style.MinWidth = CreditPicture;
        ui.Style.PreferredWidth = CreditPicture;
        ui.Style.MinHeight = CreditPicture;
        ui.Style.PreferredHeight = CreditPicture;
        Slot picture = ui.Next("Picture");
        picture.AttachComponent<Image>().Sprite.Target = ui.CircleSprite;
        picture.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        ui.Nest();
        ui.RawImage(_creatorIcon, colorX.White, true);
        ui.NestOut();
        ui.NestOut();

        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        Text info = ui.Text($"<nobr>Settings: {WorldTelemetryMod.ConfigPath}</nobr>\nVersion {WorldTelemetryMod.ModVersion}", 13f, false, Alignment.TopRight, true);
        info.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        info.AutoSizeMin.Value = 8f;
        info.AutoSizeMax.Value = 13f;
        info.HorizontalAutoSize.Value = true;
        info.VerticalAutoSize.Value = true;
        info.Slot.Name = "Version And Settings";
        RectTransform infoRect = info.Slot.GetComponent<RectTransform>();
        infoRect.AnchorMin.Value = new float2(1f, 1f);
        infoRect.AnchorMax.Value = new float2(1f, 1f);
        infoRect.Pivot.Value = new float2(1f, 1f);
        infoRect.OffsetMin.Value = new float2(-VersionWidth - 10f, -CreditHeight - 3f - VersionHeight);
        infoRect.OffsetMax.Value = new float2(-10f, -CreditHeight - 3f);

        void CreditWord(string content, Alignment alignment)
        {
            Text word = ui.Text(content, 15f, true, alignment, true);
            word.AutoSizeMin.Value = 8f;
            word.AutoSizeMax.Value = 15f;
            word.Color.Value = colorX.White;
        }
    }

    private static void BuildTitleRow(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Buttons");
        FixHeight(row);
        Row(row, 12f, Alignment.MiddleLeft);
        ui.NestInto(row);

        (_autoLabel, _autoHover) = TextButton(ui, "AutoScan", 200f, AutoLabel(), () =>
        {
            Telemetry.SetAutoScan(!WorldTelemetryMod.AutoScan);
            UpdateToggles();
        });
        TextButton(ui, "Rescan", 120f, "<nobr>Rescan</nobr>", () =>
        {
            if (Telemetry.FocusedWorld is World world)
                Telemetry.RequestScan(world, ReportMode.None);
        });
        (_profileLabel, _) = TextButton(ui, "Profile", 210f, ProfileLabel(), () =>
        {
            if (Telemetry.FocusedWorld is World world)
                Telemetry.RequestProfile(world, WorldTelemetryMod.ProfileSeconds, ReportMode.None);
        });
        TextButton(ui, "CreateLog", 170f, "<nobr>Create log</nobr>", Telemetry.WriteLog);
        (_lightLabel, _) = TextButton(ui, "Lights", 200f, LightButtonLabel(), () =>
        {
            if (LightProfiler.Running)
                LightProfiler.Cancel("Light measurement stopped.");
            else if (Telemetry.FocusedWorld is World world)
                LightProfiler.Start(world);
        });
        (_physicsLabel, _) = TextButton(ui, "Physics", 210f, PhysicsButtonLabel(), () =>
        {
            if (PhysicsProfiler.Running)
                PhysicsProfiler.Cancel("Physics measurement stopped.");
            else if (Telemetry.FocusedWorld is World world)
                PhysicsProfiler.Start(world);
        });
        (_shareLabel, _shareHover) = TextButton(ui, "Share", 200f, ShareLabel(), () =>
        {
            WorldTelemetryMod.CycleSelectionBox();
            UpdateToggles();
        });
        TextButton(ui, "Unselect", 130f, "<nobr>Unselect</nobr>", () => Unselect(true));
        ui.Style.MinWidth = 10f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Next("Spacer");
        ui.NestOut();
        ui.PopStyle();
    }

    private static void BuildInfoPanel(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = InfoHeight;
        ui.Style.PreferredHeight = InfoHeight;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Slot panel = ui.Next("Info");
        FixHeight(panel);
        panel.AttachComponent<Image>().Tint.Value = new colorX(0f, 0f, 0f, 0.25f);
        VerticalLayout layout = panel.AttachComponent<VerticalLayout>();
        layout.Spacing.Value = 4f;
        layout.PaddingTop.Value = 4f;
        layout.PaddingBottom.Value = 4f;
        layout.PaddingLeft.Value = 6f;
        layout.PaddingRight.Value = 6f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = false;
        ui.NestInto(panel);

        ui.Style.MinHeight = TabHeight;
        ui.Style.PreferredHeight = TabHeight;
        ui.Style.FlexibleHeight = -1f;
        Slot tabs = ui.Next("Tabs");
        FixHeight(tabs);
        Row(tabs, 8f, Alignment.MiddleLeft);
        ui.NestInto(tabs);
        TabButton(ui, InfoTab.Details, "Details");
        TabButton(ui, InfoTab.World, "World");
        TabButton(ui, InfoTab.Activity, "Activity");
        TabButton(ui, InfoTab.History, "History");

        ui.Style.MinWidth = 200f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = TabHeight;
        ui.Style.PreferredHeight = TabHeight;
        Slot line = ui.Next("Status");
        line.AttachComponent<Image>().Tint.Value = colorX.White;
        line.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        ui.NestInto(line);
        _statusLine = ui.Text("", 20f, false, Alignment.MiddleLeft, true);
        _statusLine.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        RectTransform lineRect = _statusLine.Slot.GetComponent<RectTransform>();
        lineRect.OffsetMin.Value = new float2(12f, 0f);
        ui.NestOut();
        ui.NestOut();

        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot body = ScrollRow(ui, "Text");
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _infoScroll = ui.ScrollArea(Alignment.TopLeft);
        ui.VerticalLayout(0f, 6f, Alignment.TopLeft, true, false);
        ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        BuildChart(ui);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _infoText = ui.Text("", 19f, false, Alignment.TopLeft, true);
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        _warningRoot = ui.Next("Warning Actions");
        VerticalLayout warningLayout = _warningRoot.AttachComponent<VerticalLayout>();
        warningLayout.Spacing.Value = 4f;
        warningLayout.ForceExpandWidth.Value = true;
        warningLayout.ForceExpandHeight.Value = false;
        _warningRoot.ActiveSelf = false;
        ReturnTo(ui, body);
        _infoBar = ScrollBar.Create(ui, _infoScroll);
        ReturnTo(ui, panel);
        ui.NestOut();
        ui.PopStyle();
        UpdateTabs();
    }

    private static Slot ScrollRow(UIBuilder ui, string name)
    {
        Slot row = ui.Next(name);
        HorizontalLayout layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 4f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        ui.NestInto(row);
        return row;
    }

    private static void FixHeight(Slot slot)
    {
        LayoutElement? element = slot.GetComponent<LayoutElement>();
        if (element is null)
            return;

        element.UseZeroMetrics.Value = true;
        element.FlexibleHeight.Value = 0f;
    }

    private static void ReturnTo(UIBuilder ui, Slot root)
    {
        while (ui.Root != root && !ui.IsAtRoot)
            ui.NestOut();
    }

    private static void CheckLayout(Slot main)
    {
        string[] expected = ["Buttons", "Heat", "Search", "Sort", "Header", "List", "Info"];
        string[] actual = main.Children.Select(child => child.Name).ToArray();
        string order = string.Join(", ", actual);
        if (expected.SequenceEqual(actual))
            WorldTelemetryMod.Log("Dash layout: " + order + ".");
        else
            WorldTelemetryMod.LogWarning("Dash layout is not as expected: " + order + ".");
    }

    private static void TabButton(UIBuilder ui, InfoTab tab, string label)
    {
        (_, InteractionElement.ColorDriver hover) = TextButton(ui, "Tab " + tab, 140f, $"<nobr>{label}</nobr>", () =>
        {
            _tab = tab;
            UpdateTabs();
        }, TabHeight);
        TabButtons[tab] = hover;
    }

    private static void UpdateTabs()
    {
        if (_infoScroll is not null && !_infoScroll.IsDestroyed)
            _infoScroll.NormalizedPosition.Value = float2.Zero;

        foreach ((InfoTab tab, InteractionElement.ColorDriver hover) in TabButtons)
            if (!hover.IsRemoved)
                hover.NormalColor.Value = tab == _tab ? ActiveColor : colorX.Clear;

        RefreshInfo();
    }

    private static void RefreshInfo()
    {
        string content = _tab switch
        {
            InfoTab.World => WorldText(),
            InfoTab.Activity => ActivityText(),
            InfoTab.History => HistoryText(),
            _ => _detailsText
        };
        SetText(_infoText, content);
        UpdateChart();
        UpdateWarningButtons();
    }

    private static void UpdateWarningButtons()
    {
        if (_warningRoot is null || _warningRoot.IsDestroyed)
            return;

        Snapshot? snapshot = _shown;
        ObjectEntry? entry = _tab == InfoTab.Details && snapshot is not null && _selected != RefID.Null ? snapshot.EntryFor(_selected) : null;
        if (entry is null || entry.Warnings.Count == 0 || entry.Warnings.Count != entry.WarningKinds.Count)
        {
            if (_warningRoot.ActiveSelf)
                _warningRoot.ActiveSelf = false;

            _warningShownFor = RefID.Null;
            return;
        }
        if (_warningShownFor == entry.Id && _warningShownVersion == Telemetry.Version && _warningRoot.ActiveSelf)
            return;

        _warningShownFor = entry.Id;
        _warningShownVersion = Telemetry.Version;
        _warningRoot.DestroyChildren();
        var ui = new UIBuilder(_warningRoot);
        RadiantUI_Constants.SetupDefaultStyle(ui, false);
        for (int i = 0; i < entry.Warnings.Count; i++)
        {
            WarningKind kind = entry.WarningKinds[i];
            string text = entry.Warnings[i];
            bool inspect = WarningActions.HasTargets(kind);
            string label = inspect ? $"<nobr>Inspect: {text}</nobr>" : $"<nobr>Select: {text}</nobr>";
            ObjectEntry current = entry;
            (Text _, InteractionElement.ColorDriver hover) = TextButton(ui, "Warning " + kind, -1f, label, () => FixWarning(snapshot!, current, kind), 34f);
            hover.NormalColor.Value = RadiantUI_Constants.Hero.YELLOW.SetA(0.28f);
            hover.HighlightColor.Value = RadiantUI_Constants.Hero.YELLOW.SetA(0.5f);
            hover.PressColor.Value = RadiantUI_Constants.Hero.YELLOW.SetA(0.7f);
        }
        _warningRoot.ActiveSelf = true;
    }

    private static void FixWarning(Snapshot snapshot, ObjectEntry entry, WarningKind kind)
    {
        Select(entry.Id);
        Telemetry.SetStatus($"Looking for what {entry.Name} is warning about...");
        WarningActions.Run(snapshot, entry, kind, Telemetry.SetStatus);
    }

    private const int ChartBars = 60;
    private static Slot? _chart;
    private static readonly List<(Slot Fill, Image Image)> ChartBarsList = new();
    private static int _chartVersion = -1;

    private static void BuildChart(UIBuilder ui)
    {
        ChartBarsList.Clear();
        ui.Style.MinHeight = 56f;
        ui.Style.PreferredHeight = 56f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Slot chart = ui.Next("Chart");
        HorizontalLayout layout = chart.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 2f;
        layout.ForceExpandWidth.Value = true;
        layout.ForceExpandHeight.Value = true;
        chart.AttachComponent<Image>().Tint.Value = new colorX(1f, 1f, 1f, 0.03f);
        ui.NestInto(chart);
        ui.Style.MinWidth = 2f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        for (int i = 0; i < ChartBars; i++)
        {
            Slot bar = ui.Next("Bar");
            Slot fill = bar.AddSlot("Fill");
            RectTransform rect = fill.AttachComponent<RectTransform>();
            rect.AnchorMin.Value = float2.Zero;
            rect.AnchorMax.Value = new float2(1f, 0f);
            Image image = fill.AttachComponent<Image>();
            image.Tint.Value = colorX.Clear;
            ChartBarsList.Add((fill, image));
        }
        ui.NestOut();
        _chart = chart;
        _chartVersion = -1;
    }

    private static void UpdateChart()
    {
        if (_chart is null || _chart.IsDestroyed)
            return;

        bool show = _tab == InfoTab.History;
        if (_chart.ActiveSelf != show)
            _chart.ActiveSelf = show;

        if (!show || _chartVersion == Telemetry.HistoryVersion)
            return;

        _chartVersion = Telemetry.HistoryVersion;
        List<HistoryPoint> history = Telemetry.History;
        int start = Math.Max(0, history.Count - ChartBars);
        double[] values = history.Skip(start).Select(point => point.Fps > 0 ? 1000.0 / point.Fps : 0).ToArray();
        double top = values.Length == 0 ? 1 : Math.Max(0.001, values.Max());
        double bottom = values.Length == 0 ? 0 : values.Min();
        for (int i = 0; i < ChartBars; i++)
        {
            (Slot fill, Image image) = ChartBarsList[i];
            if (fill.IsDestroyed)
                continue;

            int index = i - (ChartBars - values.Length);
            if (index < 0)
            {
                fill.GetComponent<RectTransform>().AnchorMax.Value = new float2(1f, 0f);
                image.Tint.Value = colorX.Clear;
                continue;
            }
            double value = values[index];
            float height = (float)Math.Clamp(value / top, 0.03, 1);
            fill.GetComponent<RectTransform>().AnchorMax.Value = new float2(1f, height);
            double rank = top > bottom ? (value - bottom) / (top - bottom) : 0;
            image.Tint.Value = Heatmap.Color(rank).SetA(0.85f);
        }
    }

    private static string HistoryText()
    {
        List<HistoryPoint> history = Telemetry.History;
        if (history.Count == 0)
            return "<color=#9aa3ad>No scans yet. Turn on Auto scan to build a history of this world.</color>";

        var text = new StringBuilder();
        HistoryPoint last = history[^1];
        double frame = last.Fps > 0 ? 1000.0 / last.Fps : 0;
        double average = history.Where(point => point.Fps > 0).Select(point => 1000.0 / point.Fps).DefaultIfEmpty(0).Average();
        text.AppendLine($"Frame time per scan (bars above, newest on the right): now {Format.Ms(frame)} ({last.Fps:F0} FPS), average {Format.Ms(average)} over {history.Count} scans.");
        AppendChanges(text);
        text.AppendLine("<b>Recent scans</b>, newest first:");
        for (int i = history.Count - 1; i >= Math.Max(0, history.Count - 20); i--)
        {
            HistoryPoint point = history[i];
            string cpu = point.CpuMs is double ms ? Format.Ms(ms) : "-";
            text.AppendLine($"<color=#9aa3ad>{point.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}</color>  {point.Fps:F0} FPS, CPU {cpu}, bones {Format.Ms(point.BonesMs)}, GPU {Format.Score(point.GpuScore)}, light {Format.Ms(point.LightMs)}, net {Format.Bytes((long)point.NetBytesPerSecond)}/s, {point.Users} users, {point.Objects:N0} objects, {point.Slots:N0} slots");
        }
        return text.ToString().TrimEnd();
    }

    private static (Snapshot? Now, Snapshot? Before) _changesFor;
    private static List<string> _changes = new();

    private static void AppendChanges(StringBuilder text)
    {
        Snapshot? now = Telemetry.Latest;
        Snapshot? before = Telemetry.Previous;
        if (now is null || before is null)
        {
            text.AppendLine("Changes appear after the second scan of this world.");
            return;
        }
        if (!ReferenceEquals(_changesFor.Now, now) || !ReferenceEquals(_changesFor.Before, before))
        {
            _changes = ComputeChanges(now, before);
            _changesFor = (now, before);
        }
        List<string> lines = _changes;
        double fpsNow = now.Fps;
        double fpsBefore = before.Fps;
        string frame = fpsNow > 0 && fpsBefore > 0 ? $" Frame time {Format.Ms(1000.0 / fpsBefore)} to {Format.Ms(1000.0 / fpsNow)}." : "";
        text.AppendLine(lines.Count == 0 ? $"No changes since the previous scan ({Format.Age(before.TakenTick)}).{frame}" : $"Since the previous scan ({Format.Age(before.TakenTick)}): {string.Join(". ", lines)}.{frame}");
    }

    private static List<string> ComputeChanges(Snapshot now, Snapshot before)
    {
        Dictionary<RefID, ObjectEntry> old = before.Entries.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        Dictionary<RefID, ObjectEntry> current = now.Entries.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        var lines = new List<string>();
        List<string> joined = current.Values.Where(entry => entry.IsUser && !old.ContainsKey(entry.Id)).Select(entry => entry.Name).ToList();
        List<string> left = old.Values.Where(entry => entry.IsUser && !current.ContainsKey(entry.Id)).Select(entry => entry.Name).ToList();
        if (joined.Count > 0)
            lines.Add("joined: " + string.Join(", ", joined));

        if (left.Count > 0)
            lines.Add("left: " + string.Join(", ", left));

        List<ObjectEntry> added = current.Values.Where(entry => !entry.IsUser && !old.ContainsKey(entry.Id)).OrderByDescending(entry => entry.SizeBytes).ToList();
        List<ObjectEntry> removed = old.Values.Where(entry => !entry.IsUser && !current.ContainsKey(entry.Id)).ToList();
        if (added.Count > 0)
            lines.Add($"{added.Count} objects added ({string.Join(", ", added.Take(3).Select(entry => entry.Name))})");

        if (removed.Count > 0)
            lines.Add($"{removed.Count} objects removed");

        if (now.Cpu is not null && before.Cpu is not null)
        {
            var cpuUp = current.Values.Where(entry => old.ContainsKey(entry.Id))
                .Select(entry => (entry.Name, Delta: entry.CpuTotalMs - old[entry.Id].CpuTotalMs))
                .Where(pair => pair.Delta > 0.01)
                .OrderByDescending(pair => pair.Delta)
                .Take(3)
                .ToList();
            if (cpuUp.Count > 0)
                lines.Add("CPU up: " + string.Join(", ", cpuUp.Select(pair => $"{pair.Name} +{Format.Ms(pair.Delta)}")));
        }
        var gpuUp = current.Values.Where(entry => old.ContainsKey(entry.Id))
            .Select(entry => (entry.Name, Delta: entry.GpuScore - old[entry.Id].GpuScore))
            .Where(pair => pair.Delta >= 10)
            .OrderByDescending(pair => pair.Delta)
            .Take(3)
            .ToList();
        if (gpuUp.Count > 0)
            lines.Add("GPU up: " + string.Join(", ", gpuUp.Select(pair => $"{pair.Name} +{Format.Score(pair.Delta)}")));

        return lines;
    }

    private static string ActivityText()
    {
        if (Telemetry.Activity.Count == 0)
            return "<color=#9aa3ad>Nothing has happened yet.</color>";

        var text = new StringBuilder();
        for (int i = Telemetry.Activity.Count - 1; i >= 0; i--)
        {
            (DateTime time, string message) = Telemetry.Activity[i];
            text.AppendLine($"<color=#9aa3ad>{time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}</color>  {message}");
        }
        return text.ToString().TrimEnd();
    }

    private static string PhysicsButtonLabel() => PhysicsProfiler.Running
        ? $"<nobr>Stop physics ({PhysicsProfiler.Done}/{PhysicsProfiler.Total})</nobr>"
        : "<nobr>Measure physics</nobr>";

    private static string LightButtonLabel() => LightProfiler.Running
        ? $"<nobr>Stop lights ({LightProfiler.Done}/{LightProfiler.Total})</nobr>"
        : "<nobr>Measure lights</nobr>";

    private static string HeatLabel() => Heatmap.Enabled ? $"<nobr>Heatmap: {SortName(_sort)}</nobr>" : "<nobr>Heatmap: OFF</nobr>";

    internal static SortMode CurrentSort => _sort;

    internal static string SortName(SortMode mode) => mode switch
    {
        SortMode.Cpu => "CPU",
        SortMode.Components => "Components",
        SortMode.ProtoFlux => "ProtoFlux",
        SortMode.Bones => "Dynamic bones",
        SortMode.Gpu => "GPU",
        SortMode.Lighting => "Lighting",
        SortMode.Physics => "Physics",
        SortMode.Network => "Network",
        SortMode.Warnings => "Warnings",
        SortMode.Slots => "Slots",
        _ => "File size"
    };

    private static string ShortSortName(SortMode mode) => mode switch
    {
        SortMode.Components => "Components",
        SortMode.ProtoFlux => "ProtoFlux",
        SortMode.Bones => "Bones",
        SortMode.Lighting => "Light",
        SortMode.Network => "Network",
        SortMode.Warnings => "Warnings",
        SortMode.Size => "File Size",
        _ => SortName(mode)
    };

    private static void BuildSortRow(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Sort");
        FixHeight(row);
        Row(row, 8f, Alignment.MiddleLeft);
        ui.NestInto(row);

        (_viewLabel, InteractionElement.ColorDriver viewHover) = TextButton(ui, "View", 240f, ViewLabel(), () =>
        {
            _view = _view switch
            {
                ViewMode.Objects => ViewMode.Users,
                ViewMode.Users => ViewMode.Hierarchy,
                ViewMode.Hierarchy => ViewMode.EverySlot,
                _ => ViewMode.Objects
            };
            _page = 0;
            _dirty = true;
            UpdateToggles();
        });
        viewHover.NormalColor.Value = ActiveColor;

        ui.Style.MinWidth = 90f;
        ui.Style.PreferredWidth = 90f;
        ui.Style.FlexibleWidth = -1f;
        Text label = ui.Text("Sort by", 24f, true, Alignment.MiddleRight, true);
        label.AutoSizeMax.Value = 24f;
        label.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;

        foreach (SortMode mode in Enum.GetValues<SortMode>())
            SortButton(ui, mode, ShortSortName(mode));

        ui.Style.MinWidth = 10f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Next("Spacer");
        ui.NestOut();
        ui.PopStyle();
    }

    private static void BuildHeatRow(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Heat");
        FixHeight(row);
        Row(row, 10f, Alignment.MiddleLeft);
        ui.NestInto(row);
        (_heatLabel, _heatHover) = TextButton(ui, "Heatmap", 250f, HeatLabel(), () =>
        {
            Heatmap.Toggle();
            UpdateToggles();
        });
        (_usersLabel, _usersHover) = TextButton(ui, "Users", 210f, UsersLabel(), () =>
        {
            Heatmap.ToggleUsers();
            UpdateToggles();
        });
        (_showAllLabel, _showAllHover) = TextButton(ui, "ShowAll", 230f, ShowAllLabel(), () =>
        {
            Heatmap.ToggleShowAll();
            UpdateToggles();
        });
        (_outlineLabel, _) = TextButton(ui, "Style", 330f, OutlineLabel(), () =>
        {
            Heatmap.CycleStyle();
            UpdateToggles();
        });
        ui.Style.MinWidth = 10f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Next("Spacer");
        ui.NestOut();
        ui.PopStyle();
    }

    private const float PageGroupWidth = 330f;
    private const float PageGroupHeight = 90f;

    private static void BuildSearchRow(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = PageGroupHeight;
        ui.Style.PreferredHeight = PageGroupHeight;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Search");
        FixHeight(row);
        Row(row, 10f, Alignment.MiddleLeft);
        ui.NestInto(row);
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;

        ui.Style.MinWidth = 90f;
        ui.Style.PreferredWidth = 90f;
        ui.Style.FlexibleWidth = -1f;
        Text label = ui.Text("Search", 24f, true, Alignment.MiddleRight, true);
        label.AutoSizeMax.Value = 24f;
        label.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;

        ui.Style.MinWidth = 240f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        _searchField = ui.TextField("", undo: false, parseRTF: false, promptText: "<alpha=#77>Part of a slot name or a path, like Props/Chair");
        _searchField.Text.Align = Alignment.MiddleLeft;
        if (_searchField.Editor.Target is TextEditor editor)
        {
            editor.LocalEditingChanged += _ =>
            {
                _pendingSearch = _searchField?.Text.Content.Value ?? "";
                _pendingSearchAt = Environment.TickCount64;
            };
            editor.LocalEditingFinished += _ => ApplySearch(_searchField?.Text.Content.Value ?? "");
            editor.LocalSubmitPressed += _ => ApplySearch(_searchField?.Text.Content.Value ?? "");
        }
        TextButton(ui, "Clear", 100f, "<nobr>Clear</nobr>", () =>
        {
            if (_searchField is not null && !_searchField.IsDestroyed)
                _searchField.Text.Content.Value = "";

            ApplySearch("");
        });

        ui.Style.MinWidth = 160f;
        ui.Style.PreferredWidth = 160f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;
        _matchLabel = ui.Text("", 22f, true, Alignment.MiddleLeft, true);
        _matchLabel.AutoSizeMax.Value = 22f;

        TextButton(ui, "Reveal", 240f, "<nobr>Show in hierarchy</nobr>", RevealSelected);

        TextButton(ui, "Previous", 120f, "<nobr>Previous</nobr>", () =>
        {
            if (_page > 0)
            {
                _page--;
                _dirty = true;
            }
        });
        ui.Style.MinWidth = PageGroupWidth;
        ui.Style.PreferredWidth = PageGroupWidth;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = PageGroupHeight;
        ui.Style.PreferredHeight = PageGroupHeight;
        ui.Style.FlexibleHeight = -1f;
        Slot group = ui.Next("Page Group");
        FixHeight(group);
        VerticalLayout groupLayout = group.AttachComponent<VerticalLayout>();
        groupLayout.Spacing.Value = 2f;
        groupLayout.ChildAlignment = Alignment.MiddleCenter;
        groupLayout.ForceExpandWidth.Value = false;
        groupLayout.ForceExpandHeight.Value = false;
        ui.NestInto(group);
        (_rowsLabel, _) = TextButton(ui, "MaxItems", PageGroupWidth, RowsLabel(), () =>
        {
            WorldTelemetryMod.CycleRowsPerPage();
            UpdateToggles();
            _dirty = true;
        }, 34f);
        ui.Style.MinWidth = PageGroupWidth;
        ui.Style.PreferredWidth = PageGroupWidth;
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;
        Slot pageRow = ui.Next("Page Row");
        FixHeight(pageRow);
        Row(pageRow, 10f, Alignment.MiddleCenter);
        ui.NestInto(pageRow);
        ui.Style.MinHeight = 52f;
        ui.Style.PreferredHeight = 52f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 60f;
        ui.Style.PreferredWidth = 60f;
        Text pageWord = ui.Text("Page", 22f, true, Alignment.MiddleRight, true);
        pageWord.AutoSizeMax.Value = 22f;
        ui.Style.MinWidth = 80f;
        ui.Style.PreferredWidth = 80f;
        _pageField = ui.TextField("1", undo: false, parseRTF: false);
        _pageField.Text.Align = Alignment.MiddleCenter;
        if (_pageField.Editor.Target is TextEditor pageEditor)
        {
            pageEditor.LocalEditingFinished += JumpToPage;
            pageEditor.LocalSubmitPressed += JumpToPage;
        }
        ui.Style.MinWidth = 90f;
        ui.Style.PreferredWidth = 90f;
        _pageLabel = ui.Text("", 22f, true, Alignment.MiddleLeft, true);
        _pageLabel.AutoSizeMax.Value = 22f;
        ui.NestOut();
        ui.NestOut();
        TextButton(ui, "Next", 90f, "<nobr>Next</nobr>", () =>
        {
            if (_page + 1 < PageStarts(WorldTelemetryMod.RowsPerPage).Count)
            {
                _page++;
                _dirty = true;
            }
        });
        ui.NestOut();
        ui.PopStyle();
    }

    private static readonly System.Text.RegularExpressions.Regex PathSeparator = new(@"\s*/\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string NormalizeSearch(string text)
    {
        string query = PathSeparator.Replace(text.Trim().ToLowerInvariant(), "/");
        return query.Trim('/').Length == 0 ? "" : query;
    }

    private static void ApplySearch(string text)
    {
        _pendingSearch = null;
        string query = NormalizeSearch(text);
        if (query == _search)
            return;

        _search = query;
        _page = 0;
        SearchCollapsed.Clear();
        _dirty = true;
    }

    private static void TickSearch()
    {
        if (_pendingSearch is string pending && Environment.TickCount64 - _pendingSearchAt >= SearchDelayMs)
            ApplySearch(pending);

        string label = _search.Length == 0 ? "" : _matchCount == 0 ? $"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>No matches</color>" : $"<nobr>{_matchCount:N0} {(_matchCount == 1 ? "match" : "matches")}</nobr>";
        SetText(_matchLabel, label);
    }

    private static void RevealSelected()
    {
        SlotNode? node = _shown?.Find(_selected);
        if (node is null)
        {
            Telemetry.SetStatus("Click a row first, then press Show in hierarchy.");
            return;
        }
        if (_searchField is not null && !_searchField.IsDestroyed)
            _searchField.Text.Content.Value = "";

        _pendingSearch = null;
        _search = "";
        SearchCollapsed.Clear();
        for (SlotNode? parent = node.Parent; parent is not null; parent = parent.Parent)
            Expanded.Add(parent.Id);

        _expandVersion++;
        _view = ViewMode.Hierarchy;
        _revealId = node.Id;
        _dirty = true;
        UpdateToggles();
    }

    private static void JumpToPage(TextEditor editor)
    {
        string typed = _pageField?.Text.Content.Value ?? "";
        int pages = PageStarts(WorldTelemetryMod.RowsPerPage).Count;
        if (int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page))
        {
            int target = Math.Clamp(page, 1, pages) - 1;
            if (target != _page)
            {
                _page = target;
                _dirty = true;
            }
        }
        ShowPage(pages, true);
    }

    private static void ShowPage(int pages, bool force)
    {
        SetText(_pageLabel, $"<nobr>of {pages}</nobr>");
        if (_pageField is null || _pageField.IsDestroyed)
            return;

        if (!force && _pageField.Editor.Target is TextEditor editor && editor.IsEditing)
            return;

        string value = (_page + 1).ToString(CultureInfo.InvariantCulture);
        if (_pageField.Text.Content.Value != value)
            _pageField.Text.Content.Value = value;
    }

    private static void SortButton(UIBuilder ui, SortMode mode, string label)
    {
        (_, InteractionElement.ColorDriver hover) = TextButton(ui, "Sort " + mode, 118f, $"<nobr>{label}</nobr>", () =>
        {
            _sort = mode;
            _page = 0;
            _dirty = true;
            if (Heatmap.Enabled)
                Heatmap.EnsureProfile();

            UpdateToggles();
        });
        SortButtons[mode] = hover;
    }

    private static void UpdateToggles()
    {
        foreach ((SortMode mode, InteractionElement.ColorDriver hover) in SortButtons)
            if (!hover.IsRemoved)
                hover.NormalColor.Value = mode == _sort ? ActiveColor : colorX.Clear;

        if (_autoHover is not null && !_autoHover.IsRemoved)
            _autoHover.NormalColor.Value = WorldTelemetryMod.AutoScan ? RadiantUI_Constants.Hero.GREEN.SetA(0.35f) : colorX.Clear;

        if (_heatHover is not null && !_heatHover.IsRemoved)
            _heatHover.NormalColor.Value = Heatmap.Enabled ? RadiantUI_Constants.Hero.ORANGE.SetA(0.35f) : colorX.Clear;

        if (_usersHover is not null && !_usersHover.IsRemoved)
            _usersHover.NormalColor.Value = Heatmap.HideUsers ? RadiantUI_Constants.Hero.ORANGE.SetA(0.35f) : colorX.Clear;

        if (_shareHover is not null && !_shareHover.IsRemoved)
            _shareHover.NormalColor.Value = WorldTelemetryMod.SelectionBoxHidden ? colorX.Clear : WorldTelemetryMod.ShareSelection ? RadiantUI_Constants.Hero.GREEN.SetA(0.35f) : RadiantUI_Constants.Hero.ORANGE.SetA(0.35f);

        if (_showAllHover is not null && !_showAllHover.IsRemoved)
            _showAllHover.NormalColor.Value = Heatmap.ShowAll ? RadiantUI_Constants.Hero.ORANGE.SetA(0.35f) : colorX.Clear;

        SetText(_showAllLabel, ShowAllLabel());
        SetText(_outlineLabel, OutlineLabel());
        SetText(_usersLabel, UsersLabel());
        SetText(_shareLabel, ShareLabel());
        SetText(_rowsLabel, RowsLabel());
        if (_headerSort != (int)_sort)
            FillHeader();

        SetText(_autoLabel, AutoLabel());
        SetText(_heatLabel, HeatLabel());
        SetText(_viewLabel, ViewLabel());
        UpdateHeaderTexts();
    }

    private static string HeaderName() => _view switch
    {
        ViewMode.Objects => "Object",
        ViewMode.Users => "User (their avatar and everything they carry)",
        ViewMode.Hierarchy => "Slot, press + to show what is inside",
        _ => "Slot (every slot in the world, its own components only)"
    };

    private static string[] HeaderNames() => _view switch
    {
        ViewMode.Hierarchy => ["Size+", "CPU+", "Comp.", "Flux", "Bones", "GPU", "Light", "Physics", "Net/s", "Warn", "Slots"],
        ViewMode.EverySlot => ["Own size", "Own CPU", "Comp.", "Flux", "Bones", "GPU", "Light", "Physics", "Net/s", "Warn", "Below"],
        _ => ["Size", "CPU", "Comp.", "Flux", "Bones", "GPU", "Light", "Physics", "Net/s", "Warn", "Slots"]
    };

    private static string ShowAllLabel() => Heatmap.ShowAll ? "<nobr>Show all: ON</nobr>" : "<nobr>Show all: OFF</nobr>";

    private static string OutlineLabel() => Heatmap.Style switch
    {
        HeatStyle.Outline => "<nobr>Style: outline boxes</nobr>",
        HeatStyle.Meshes => "<nobr>Style: meshes only</nobr>",
        HeatStyle.MeshesOrOutline => "<nobr>Style: meshes or outline</nobr>",
        _ => "<nobr>Style: solid boxes</nobr>"
    };

    private static string UsersLabel() => Heatmap.HideUsers ? "<nobr>Players: hidden</nobr>" : "<nobr>Players: shown</nobr>";

    private static string RowsLabel() => $"<nobr>Max Items Per Page: {WorldTelemetryMod.RowsPerPage}</nobr>";

    private static string ShareLabel() => WorldTelemetryMod.SelectionBoxHidden ? "<nobr>Box: NONE</nobr>" : WorldTelemetryMod.ShareSelection ? "<nobr>Box: everyone</nobr>" : "<nobr>Box: only me</nobr>";

    private static int[] ColumnOrder()
    {
        int first = (int)_sort;
        var order = new List<int>(ColumnWidths.Length) { first };
        for (int i = 0; i < ColumnWidths.Length; i++)
            if (i != first)
                order.Add(i);

        return order.ToArray();
    }

    private static void SetText(Text? text, string content)
    {
        if (text is not null && !text.IsDestroyed && text.Content.Value != content)
            text.Content.Value = content;
    }

    private static string AutoLabel() => WorldTelemetryMod.AutoScan ? "<nobr>Auto scan: ON</nobr>" : "<nobr>Auto scan: OFF</nobr>";

    private static string ViewLabel() => _view switch
    {
        ViewMode.Users => "<nobr>View: Users</nobr>",
        ViewMode.Hierarchy => "<nobr>View: Hierarchy</nobr>",
        ViewMode.EverySlot => "<nobr>View: Every slot</nobr>",
        _ => "<nobr>View: Objects</nobr>"
    };

    private static void BuildHeaderRow(UIBuilder ui)
    {
        ui.PushStyle();
        ui.Style.MinHeight = 32f;
        ui.Style.PreferredHeight = 32f;
        ui.Style.FlexibleHeight = -1f;
        Slot row = ui.Next("Header");
        FixHeight(row);
        Row(row, 12f, Alignment.MiddleLeft, 8f);
        _headerRow = row;
        _headerSort = -1;
        ui.PopStyle();
        FillHeader();
    }

    private static void FillHeader()
    {
        if (_headerRow is null || _headerRow.IsDestroyed)
            return;

        _headerRow.DestroyChildren();
        _headerSort = (int)_sort;
        var ui = new UIBuilder(_headerRow);
        RadiantUI_Constants.SetupDefaultStyle(ui, false);
        ui.Style.MinHeight = 32f;
        ui.Style.PreferredHeight = 32f;
        ui.Style.FlexibleHeight = -1f;
        HeaderCell(ui, "", ThumbSize, false);
        _nameHeader = HeaderCell(ui, "Object", -1f, true);
        bool leading = true;
        foreach (int column in ColumnOrder())
        {
            Text cell = HeaderCell(ui, "", ColumnWidths[column], false);
            if (leading)
                cell.Color.Value = ActiveHeaderColor;

            leading = false;
            ValueHeaders[column] = cell;
        }
        HeaderCell(ui, "Saved", PersistentWidth, false);
        ui.Style.MinWidth = ScrollBar.Width;
        ui.Style.PreferredWidth = ScrollBar.Width;
        ui.Style.FlexibleWidth = -1f;
        ui.Next("Scroll Bar Space");
        UpdateHeaderTexts();
    }

    private static void UpdateHeaderTexts()
    {
        SetText(_nameHeader, HeaderName());
        string[] headers = HeaderNames();
        for (int i = 0; i < ValueHeaders.Length; i++)
            SetText(ValueHeaders[i], headers[i]);
    }

    private static Text HeaderCell(UIBuilder ui, string label, float width, bool flexible)
    {
        ui.Style.MinWidth = flexible ? 200f : width;
        ui.Style.PreferredWidth = flexible ? -1f : width;
        ui.Style.FlexibleWidth = flexible ? 1f : -1f;
        Text text = ui.Text(label, 20f, true, flexible ? Alignment.MiddleLeft : Alignment.MiddleRight, true);
        text.AutoSizeMin.Value = 10f;
        text.AutoSizeMax.Value = 20f;
        text.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        return text;
    }

    private static void RebuildList()
    {
        Snapshot? snapshot = Telemetry.Latest;
        int rowsPerPage = WorldTelemetryMod.RowsPerPage;
        (int, SortMode, ViewMode, int, string) key = (Telemetry.Version, _sort, _view, _expandVersion, _search);
        if (key != _itemsKey || !ReferenceEquals(snapshot, _shown))
        {
            _items = snapshot is null ? new List<RowItem>() : BuildItems(snapshot);
            _itemsKey = key;
        }
        List<int> pageStarts = PageStarts(rowsPerPage);
        if (_revealId != RefID.Null)
        {
            RefID reveal = _revealId;
            _revealId = RefID.Null;
            int index = _items.FindIndex(candidate => candidate.Id == reveal);
            if (index >= 0)
                _page = Math.Max(0, pageStarts.FindLastIndex(start => start <= index));
        }
        int pages = pageStarts.Count;
        _page = Math.Clamp(_page, 0, pages - 1);
        _shown = snapshot;
        _shownVersion = Telemetry.Version;
        _shownRowsPerPage = rowsPerPage;
        _dirty = false;

        if (_listContent is null || _listContent.IsDestroyed)
        {
            Thumbnails.ClearQueue();
            Rows.Clear();
            return;
        }
        if (snapshot is not null && _selected != RefID.Null)
            Selection.SetLabel(SelectionLabel(snapshot, _selected));

        if (_page != _lastListPage && _listScroll is not null && !_listScroll.IsDestroyed)
            _listScroll.NormalizedPosition.Value = float2.Zero;

        _lastListPage = _page;
        ShowPage(pages, false);
        int first = pageStarts[_page];
        int last = _page + 1 < pages ? pageStarts[_page + 1] : _items.Count;
        if (snapshot is not null && CanReuseRows(first, last))
        {
            for (int i = first; i < last; i++)
                UpdateRow(Rows[i - first], snapshot, _items[i]);
        }
        else
        {
            Thumbnails.ClearQueue();
            Rows.Clear();
            _listContent.DestroyChildren();
            _rowsSort = _sort;
            _rowsView = _view;
            if (snapshot is not null)
            {
                var ui = new UIBuilder(_listContent);
                RadiantUI_Constants.SetupDefaultStyle(ui, false);
                _pageIndent = 0f;
                for (int i = first; i < last; i++)
                {
                    _pageIndent = MathX.Max(_pageIndent, IndentFor(_items[i]));
                    Rows.Add(BuildRow(ui, snapshot, _items[i], i));
                }
                if (WorldTelemetryMod.ThumbnailsEnabled)
                    foreach (RowView row in Rows)
                        RequestThumbnail(snapshot.World, row);
            }
        }
        UpdateDetails();
        if (_tab != InfoTab.Details)
            RefreshInfo();
    }

    private static bool CanReuseRows(int first, int last)
    {
        if (_rowsSort != _sort || _rowsView != _view || Rows.Count != last - first || Rows.Count == 0)
            return false;

        for (int i = 0; i < Rows.Count; i++)
        {
            RowView row = Rows[i];
            RowItem item = _items[first + i];
            if (row.Index != first + i || row.Item.Id != item.Id || row.Item.Depth != item.Depth || row.Item.Expandable != item.Expandable || row.Item.Open != item.Open)
                return false;

            if (row.Hover.IsRemoved || row.Name.IsDestroyed)
                return false;
        }
        return true;
    }

    private static void UpdateRow(RowView row, Snapshot snapshot, RowItem item)
    {
        row.Item = item;
        SetText(row.Name, NameText(item));
        string[] cells = CellTexts(snapshot, item);
        int[] order = ColumnOrder();
        for (int i = 0; i < order.Length && i < row.Cells.Length; i++)
            SetText(row.Cells[i], cells[order[i]]);

        SetText(row.Cells[^1], PersistentText(item));
        colorX normal = item.Id == _selected ? SelectedColor : row.BaseColor;
        if (row.Hover.NormalColor.Value != normal)
            row.Hover.NormalColor.Value = normal;
    }

    private static List<RowItem> BuildItems(Snapshot snapshot)
    {
        string query = _search;
        switch (_view)
        {
            case ViewMode.Hierarchy:
            {
                var items = new List<RowItem>();
                if (query.Length == 0)
                {
                    _matchCount = 0;
                    AddTree(snapshot, snapshot.Roots, items, null, null);
                    return items;
                }
                var matches = new HashSet<SlotNode>(snapshot.Nodes.Where(node => node.Matches(query)), ReferenceEqualityComparer.Instance);
                var keep = new HashSet<SlotNode>(ReferenceEqualityComparer.Instance);
                foreach (SlotNode match in matches)
                {
                    SlotNode? node = match;
                    while (node is not null && keep.Add(node))
                        node = node.Parent;
                }
                _matchCount = matches.Count;
                AddTree(snapshot, snapshot.Roots, items, matches, keep);
                return items;
            }
            case ViewMode.EverySlot:
            {
                IEnumerable<SlotNode> nodes = query.Length == 0 ? snapshot.Nodes : snapshot.Nodes.Where(node => node.Matches(query));
                List<RowItem> items = Sort(nodes.Select(node => OwnItem(snapshot, node, query.Length > 0)));
                _matchCount = query.Length == 0 ? 0 : items.Count;
                return items;
            }
            case ViewMode.Users:
            {
                IEnumerable<ObjectEntry> users = snapshot.Entries.Where(entry => entry.IsUser && (query.Length == 0 || snapshot.Find(entry.Id)?.Matches(query) == true));
                List<RowItem> items = Sort(users.Select(entry => ObjectItem(snapshot, entry, query.Length > 0)));
                _matchCount = query.Length == 0 ? 0 : items.Count;
                return items;
            }
            default:
            {
                IEnumerable<ObjectEntry> entries = query.Length == 0 ? snapshot.Entries : snapshot.Entries.Where(entry => snapshot.Find(entry.Id)?.Matches(query) == true);
                List<RowItem> items = Sort(entries.Select(entry => ObjectItem(snapshot, entry, query.Length > 0)));
                _matchCount = query.Length == 0 ? 0 : items.Count;
                return items;
            }
        }
    }

    private static void AddTree(Snapshot snapshot, List<SlotNode> nodes, List<RowItem> items, HashSet<SlotNode>? matches, HashSet<SlotNode>? keep)
    {
        IEnumerable<SlotNode> shown = keep is null ? nodes : nodes.Where(keep.Contains);
        foreach (RowItem item in Sort(shown.Select(node => TreeItem(snapshot, node, matches, keep))))
        {
            items.Add(item);
            if (!item.Open || item.Node?.Children is not List<SlotNode> children)
                continue;

            if (item.SearchBranch)
                AddTree(snapshot, children, items, matches, keep);
            else
                AddTree(snapshot, children, items, null, null);
        }
    }

    private static List<RowItem> Sort(IEnumerable<RowItem> items)
    {
        IOrderedEnumerable<RowItem> ordered = _sort switch
        {
            SortMode.Cpu => items.OrderByDescending(item => item.Cpu),
            SortMode.Components => items.OrderByDescending(item => item.Component),
            SortMode.ProtoFlux => items.OrderByDescending(item => item.Flux),
            SortMode.Bones => items.OrderByDescending(item => item.Bones).ThenByDescending(item => item.BoneChains),
            SortMode.Network => items.OrderByDescending(item => item.Network),
            SortMode.Warnings => items.OrderByDescending(item => item.Warnings),
            SortMode.Gpu => items.OrderByDescending(item => item.Gpu),
            SortMode.Lighting => items.OrderByDescending(item => item.Light),
            SortMode.Physics => items.OrderByDescending(item => item.Physics),
            SortMode.Slots => items.OrderByDescending(item => item.Slots),
            _ => items.OrderByDescending(item => item.Size)
        };
        return ordered.ThenByDescending(item => item.Size).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static RowItem ObjectItem(Snapshot snapshot, ObjectEntry entry, bool match)
    {
        string kind = entry.IsUser ? "user" : entry.IsContainer ? "group" : "object";
        return new RowItem
        {
            Id = entry.Id,
            Slot = entry.Slot,
            Name = entry.Name,
            SubtitleSource = () => $"{entry.Path}  ({kind}, {entry.Components:N0} components)",
            Persistent = entry.Persistent,
            Size = entry.SizeBytes,
            Cpu = entry.CpuTotalMs,
            Gpu = entry.GpuScore,
            Component = entry.ComponentMs,
            Flux = entry.FluxMs,
            Light = entry.Light.ValueMs,
            LightsMeasured = entry.Light.Measured,
            Lights = entry.Light.Lights,
            Physics = snapshot.PhysicsMeasured > 0 ? entry.Physics.ValueMs : entry.Physics.Score,
            PhysicsState = entry.Physics.Colliders == 0 ? 0 : snapshot.PhysicsMeasured == 0 ? 1 : entry.Physics.Measured > 0 ? 2 : 3,
            Slots = entry.Slots,
            Bones = entry.BonesMs,
            BoneChains = entry.Extra.BoneChains,
            Network = entry.Extra.NetBytesPerSecond,
            Warnings = entry.Warnings.Count,
            Match = match,
            Entry = entry
        };
    }

    private static RowItem TreeItem(Snapshot snapshot, SlotNode node, HashSet<SlotNode>? matches, HashSet<SlotNode>? keep)
    {
        int children = node.Children?.Count ?? 0;
        bool match = matches?.Contains(node) == true;
        bool below = keep is not null && node.Children is not null && node.Children.Any(keep.Contains);
        bool expanded = Expanded.Contains(node.Id);
        bool collapsed = SearchCollapsed.Contains(node.Id);
        bool branch = keep is not null && (!match || (below && !expanded));
        bool open = keep is null ? expanded : !match ? !collapsed : expanded || (below && !collapsed);
        string own = node.OwnSize is null ? "" : $", own {Format.Bytes(node.OwnSize.TotalBytes)}";
        return new RowItem
        {
            Id = node.Id,
            Slot = node.Slot,
            Name = node.Name,
            SubtitleSource = () => $"{children:N0} {(children == 1 ? "child" : "children")}, {node.OwnComponents:N0} own components{own}{(node.Active ? "" : ", inactive")}",
            Persistent = node.Persistent,
            Size = node.Size.TotalBytes,
            Cpu = snapshot.Ms(node.Cpu),
            Gpu = node.Gpu?.Score ?? 0,
            Component = snapshot.ComponentMs(node.Cpu),
            Flux = snapshot.FluxMs(node.Cpu),
            Light = node.Light?.ValueMs ?? 0,
            LightsMeasured = node.Light?.Measured ?? 0,
            Lights = node.Light?.Lights ?? 0,
            Physics = node.Physics?.Score ?? 0,
            Slots = node.Slots,
            Depth = node.Depth,
            Expandable = children > 0,
            Bones = snapshot.Ms(node.Cpu, (int)CpuCategory.DynamicBones),
            BoneChains = node.Extra?.BoneChains ?? 0,
            Network = node.Extra?.NetBytesPerSecond ?? 0,
            Warnings = snapshot.EntryFor(node.Id)?.Warnings.Count ?? 0,
            Open = open && children > 0,
            Match = match,
            SearchBranch = branch,
            Node = node
        };
    }

    private static RowItem OwnItem(Snapshot snapshot, SlotNode node, bool match)
    {
        return new RowItem
        {
            Id = node.Id,
            Slot = node.Slot,
            Name = node.Name,
            SubtitleSource = () => $"{node.Path()}  ({node.OwnComponents:N0} components{(node.Active ? "" : ", inactive")})",
            Persistent = node.Persistent,
            Size = node.OwnSize?.TotalBytes ?? 0,
            Cpu = snapshot.Ms(node.OwnCpu),
            Gpu = node.OwnGpu?.Score ?? 0,
            Component = snapshot.ComponentMs(node.OwnCpu),
            Flux = snapshot.FluxMs(node.OwnCpu),
            Light = node.OwnLight?.ValueMs ?? 0,
            LightsMeasured = node.OwnLight?.Measured ?? 0,
            Lights = node.OwnLight?.Lights ?? 0,
            Physics = node.OwnPhysics?.Score ?? 0,
            Slots = node.Slots,
            Bones = snapshot.Ms(node.OwnCpu, (int)CpuCategory.DynamicBones),
            BoneChains = node.OwnExtra?.BoneChains ?? 0,
            Network = node.OwnExtra?.NetBytesPerSecond ?? 0,
            Warnings = snapshot.EntryFor(node.Id)?.Warnings.Count ?? 0,
            Match = match,
            Node = node
        };
    }

    private static List<int> PageStarts(int rowsPerPage)
    {
        var starts = new List<int> { 0 };
        if (_view != ViewMode.Hierarchy)
        {
            for (int i = rowsPerPage; i < _items.Count; i += rowsPerPage)
                starts.Add(i);

            return starts;
        }
        var blockEnd = new int[_items.Count];
        int end = _items.Count;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            blockEnd[i] = end;
            if (_items[i].Depth == 0)
                end = i;
        }
        int pageRows = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            bool root = _items[i].Depth == 0;
            bool full = root ? pageRows + (blockEnd[i] - i) > rowsPerPage : pageRows >= rowsPerPage;
            if (pageRows > 0 && full)
            {
                starts.Add(i);
                pageRows = 0;
            }
            pageRows++;
        }
        return starts;
    }

    private static float IndentFor(RowItem item) => _view == ViewMode.Hierarchy ? item.Depth * IndentStep : 0f;

    private static float RowFixedWidth()
    {
        float columns = 0f;
        foreach (float width in ColumnWidths)
            columns += width;

        return ExpandWidth + ThumbSize + 200f + PersistentWidth + columns + 12f * (ColumnWidths.Length + 4) + 16f;
    }

    private static void UpdateListWidth()
    {
        if (_listContent is null || _listContent.IsDestroyed || _listContent.GetComponent<RectTransform>() is not RectTransform content)
            return;

        float viewport = content.RectParent?.LocalComputeRect.size.x ?? 0f;
        float spare = MathX.Max(0f, viewport - RowFixedWidth());
        float extra = viewport <= 0f || _view != ViewMode.Hierarchy ? 0f : MathX.Max(0f, _pageIndent - spare);
        if (MathX.Abs(extra - _listExtraWidth) < 0.5f)
            return;

        _listExtraWidth = extra;
        content.OffsetMax.Value = new float2(extra, content.OffsetMax.Value.y);
        if (extra <= 0f && _listScroll is not null && !_listScroll.IsDestroyed)
        {
            float2 position = _listScroll.NormalizedPosition.Value;
            _listScroll.NormalizedPosition.Value = new float2(0f, position.y);
        }
    }

    private static RowView BuildRow(UIBuilder ui, Snapshot snapshot, RowItem item, int index)
    {
        RowView? view = null;
        colorX baseColor = index % 2 == 0 ? new colorX(1f, 1f, 1f, 0.04f) : colorX.Clear;
        ui.PushStyle();
        ui.Style.MinHeight = RowHeight;
        ui.Style.PreferredHeight = RowHeight;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Slot row = ui.Next("Row");
        Row(row, 6f, Alignment.MiddleLeft);
        ui.NestInto(row);

        if (_view == ViewMode.Hierarchy)
        {
            float indent = IndentFor(item);
            if (indent > 0f)
            {
                ui.Style.MinWidth = indent;
                ui.Style.PreferredWidth = indent;
                ui.Style.FlexibleWidth = -1f;
                ui.Next("Indent");
            }
            if (item.Expandable)
            {
                TextButton(ui, "Expand", ExpandWidth, item.Open ? "<b>-</b>" : "<b>+</b>", () =>
                {
                    if (view is not null)
                        ToggleExpanded(view.Item);
                }, RowHeight - 16f);
            }
            else
            {
                ui.Style.MinWidth = ExpandWidth;
                ui.Style.PreferredWidth = ExpandWidth;
                ui.Style.FlexibleWidth = -1f;
                ui.Next("NoChildren");
            }
        }

        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = RowHeight;
        ui.Style.PreferredHeight = RowHeight;
        Slot body = ui.Next("Body");
        Image background = body.AttachComponent<Image>();
        background.Tint.Value = baseColor;
        Button button = body.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = ConfigureHover(button, background, item.Id == _selected ? SelectedColor : baseColor);
        button.LocalPressed += (_, _) =>
        {
            if (view is not null)
                RowPressed(view);
        };
        Row(body, 12f, Alignment.MiddleLeft, 4f);
        ui.NestInto(body);

        ui.Style.MinWidth = ThumbSize;
        ui.Style.PreferredWidth = ThumbSize;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = ThumbSize;
        ui.Style.PreferredHeight = ThumbSize;
        Slot thumb = ui.Next("Thumbnail");
        thumb.AttachComponent<Image>().Tint.Value = new colorX(0f, 0f, 0f, 0.35f);
        ui.NestInto(thumb);
        Text placeholder = ui.Text(WorldTelemetryMod.ThumbnailsEnabled ? "..." : "", 18f, true, Alignment.MiddleCenter, true);
        placeholder.AutoSizeMax.Value = 18f;
        placeholder.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        Slot pictureSlot = ui.Next("Picture");
        StaticTexture2D texture = pictureSlot.AttachComponent<StaticTexture2D>();
        texture.WrapModeU.Value = Renderite.Shared.TextureWrapMode.Clamp;
        texture.WrapModeV.Value = Renderite.Shared.TextureWrapMode.Clamp;
        RawImage picture = pictureSlot.AttachComponent<RawImage>();
        picture.Texture.Target = texture;
        picture.PreserveAspect.Value = true;
        picture.Tint.Value = colorX.Clear;
        ui.NestOut();

        ui.Style.MinHeight = RowHeight - 8f;
        ui.Style.PreferredHeight = RowHeight - 8f;
        ui.Style.MinWidth = 200f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        Text name = ui.Text(NameText(item), 24f, true, Alignment.MiddleLeft, true);
        name.AutoSizeMin.Value = 12f;
        name.AutoSizeMax.Value = 24f;

        var cellTexts = new List<Text>();
        string[] cells = CellTexts(snapshot, item);
        foreach (int column in ColumnOrder())
            cellTexts.Add(Cell(ui, cells[column], ColumnWidths[column]));
        cellTexts.Add(Cell(ui, PersistentText(item), PersistentWidth));
        ui.NestOut();
        ui.NestOut();
        ui.PopStyle();

        view = new RowView
        {
            Item = item,
            Index = index,
            Name = name,
            Cells = cellTexts.ToArray(),
            Hover = hover,
            Texture = texture,
            Picture = picture,
            Placeholder = placeholder,
            BaseColor = baseColor
        };
        return view;
    }

    private static string NameText(RowItem item) => $"<b>{(item.Match ? $"<color={MatchColor}>{item.Name}</color>" : item.Name)}</b>\n<size=70%><color=#9aa3ad>{item.Subtitle}</color></size>";

    private static string PersistentText(RowItem item) => item.Persistent ? "Yes" : $"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>No</color>";

    private static string[] CellTexts(Snapshot snapshot, RowItem item)
    {
        return
        [
            Format.Bytes(item.Size),
            CpuCell(snapshot, item.Cpu),
            CpuCell(snapshot, item.Component),
            CpuCell(snapshot, item.Flux),
            BonesCell(snapshot, item),
            ScoreCell(item.Gpu),
            item.Lights == 0 ? "<color=#7f8790>-</color>" : LightValue(item.Light, item.LightsMeasured, item.Lights),
            PhysicsCell(item),
            item.Network > 0 ? Format.Bytes((long)item.Network) : snapshot.Cpu is null ? "<color=#7f8790>-</color>" : "<color=#7f8790>0</color>",
            item.Warnings > 0 ? $"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>{item.Warnings}</color>" : "<color=#7f8790>0</color>",
            item.Slots.ToString("N0", CultureInfo.InvariantCulture)
        ];
    }

    private static string CpuCell(Snapshot snapshot, double ms)
    {
        if (snapshot.Cpu is null)
            return "<color=#7f8790>-</color>";

        return ms > 0 ? Format.Ms(ms) : "<color=#7f8790>0</color>";
    }

    private static string LightValue(double ms, int measured, int lights) => measured >= lights ? Format.Ms(ms) : "~" + Format.Ms(ms);

    private static string BonesCell(Snapshot snapshot, RowItem item)
    {
        if (item.BoneChains == 0 && item.Bones <= 0)
            return "<color=#7f8790>-</color>";

        if (snapshot.Cpu is null)
            return $"<color=#7f8790>{item.BoneChains} ch.</color>";

        return Format.Ms(item.Bones);
    }

    private static string PhysicsCell(RowItem item) => item.PhysicsState switch
    {
        2 => Format.Ms(item.Physics),
        3 => "~" + Format.Ms(item.Physics),
        _ => ScoreCell(item.Physics)
    };

    private static string ScoreCell(double score) => score > 0 ? Format.Score(score) : "<color=#7f8790>0</color>";

    private static void ToggleExpanded(RowItem item)
    {
        if (item.Open)
        {
            Expanded.Remove(item.Id);
            if (_search.Length > 0)
                SearchCollapsed.Add(item.Id);
        }
        else if (!item.Match && item.SearchBranch)
            SearchCollapsed.Remove(item.Id);
        else
        {
            Expanded.Add(item.Id);
            SearchCollapsed.Remove(item.Id);
        }

        _expandVersion++;
        _dirty = true;
    }

    private static Text Cell(UIBuilder ui, string content, float width)
    {
        ui.Style.MinWidth = width;
        ui.Style.PreferredWidth = width;
        ui.Style.FlexibleWidth = -1f;
        Text text = ui.Text(content, 24f, true, Alignment.MiddleRight, true);
        text.AutoSizeMin.Value = 12f;
        text.AutoSizeMax.Value = 24f;
        return text;
    }

    private static void RequestThumbnail(World world, RowView row)
    {
        Thumbnails.Enqueue(world, row.Item.Slot, row.Item.Id, uri =>
        {
            if (row.Texture.IsDestroyed || row.Picture.IsDestroyed || row.Placeholder.IsDestroyed)
                return;

            if (uri is null)
            {
                row.Placeholder.Content.Value = "No picture";
                return;
            }
            row.Texture.URL.Value = uri;
            row.Picture.Tint.Value = colorX.White;
            row.Placeholder.Content.Value = "";
        });
    }

    private static void RowPressed(RowView view)
    {
        Snapshot? snapshot = _shown;
        RowItem item = view.Item;
        if (snapshot is null)
            return;

        long now = Stopwatch.GetTimestamp();
        float interval = InteractionHandler.DoubleClickInterval;
        if (interval < 0.1f)
            interval = 0.5f;

        bool doubleClick = item.Id == _lastPressId && (now - _lastPressTimestamp) / (double)Stopwatch.Frequency <= interval;
        _lastPressId = doubleClick ? RefID.Null : item.Id;
        _lastPressTimestamp = now;
        if (!doubleClick && item.Id == _selected)
        {
            Unselect(false);
            return;
        }
        Select(item.Id);
        if (!doubleClick)
            return;

        Telemetry.SetStatus($"Teleporting to {item.Name}...");
        Teleporter.TeleportTo(snapshot.World, item.Slot, item.Name, Telemetry.SetStatus);
    }

    private static void Unselect(bool everyone)
    {
        if (everyone && Telemetry.FocusedWorld is World world)
            Selection.ClearAll(world);
        else
            Selection.Clear();

        _selected = RefID.Null;
        foreach (RowView row in Rows)
            if (!row.Hover.IsRemoved)
                row.Hover.NormalColor.Value = row.BaseColor;

        UpdateDetails();
    }

    private static string SelectionLabel(Snapshot snapshot, RefID id)
    {
        ObjectEntry? entry = snapshot.Entries.FirstOrDefault(candidate => candidate.Id == id);
        SlotNode? node = snapshot.Find(id);
        if (entry is null && node is null)
            return "";

        var text = new StringBuilder();
        text.AppendLine($"<b>{entry?.Name ?? node!.Name}</b>");
        double cpu = entry?.CpuTotalMs ?? snapshot.Ms(node!.Cpu);
        double component = entry?.ComponentMs ?? snapshot.ComponentMs(node!.Cpu);
        double flux = entry?.FluxMs ?? snapshot.FluxMs(node!.Cpu);
        text.AppendLine(snapshot.Cpu is null ? "CPU not measured" : $"CPU {Format.Ms(cpu)} (components {Format.Ms(component)}, ProtoFlux {Format.Ms(flux)})");
        double gpu = entry?.GpuScore ?? node!.Gpu?.Score ?? 0;
        LightTotals light = entry?.Light ?? node!.Light ?? new LightTotals();
        PhysicsTotals physics = entry?.Physics ?? node!.Physics ?? new PhysicsTotals();
        string lighting = light.Lights == 0 ? "no lights" : LightValue(light.ValueMs, light.Measured, light.Lights);
        string collision = physics.Colliders == 0 ? "no colliders" : physics.Measured > 0 ? Format.Ms(physics.MeasuredMs) : snapshot.PhysicsMeasured > 0 ? "~" + Format.Ms(physics.ValueMs) : Format.Score(physics.Score) + " points";
        text.AppendLine($"GPU {Format.Score(gpu)}, lighting {lighting}, physics {collision}");
        long size = entry?.SizeBytes ?? node!.Size.TotalBytes;
        int slots = entry?.Slots ?? node!.Slots;
        int components = entry?.Components ?? node!.Components;
        text.Append($"{Format.Bytes(size)}, {slots:N0} slots, {components:N0} components");
        return text.ToString();
    }

    private static void Select(RefID id)
    {
        if (_selected != id && _tab != InfoTab.Details)
        {
            _tab = InfoTab.Details;
            _selected = id;
            UpdateTabs();
        }
        _selected = id;
        if (_shown is Snapshot shown && (shown.Find(id)?.Slot ?? shown.Entries.FirstOrDefault(entry => entry.Id == id)?.Slot) is Slot target)
        {
            Selection.Show(shown.World, target, SelectionLabel(shown, id));
            if (!WorldTelemetryMod.SelectionBoxHidden && !Access.CanSelect(shown.World))
                Telemetry.SetStatus("You do not have permission to select items in this world, so no selection box is drawn.");
        }

        foreach (RowView row in Rows)
            if (!row.Hover.IsRemoved)
                row.Hover.NormalColor.Value = row.Item.Id == _selected ? SelectedColor : row.BaseColor;

        UpdateDetails();
    }

    private static void UpdateDetails()
    {
        Snapshot? snapshot = _shown;
        RowItem? item = _items.FirstOrDefault(candidate => candidate.Id == _selected);
        ObjectEntry? entry = item?.Entry ?? snapshot?.Entries.FirstOrDefault(candidate => candidate.Id == _selected);
        SlotNode? node = item?.Node ?? snapshot?.Find(_selected);
        if (snapshot is null || (entry is null && node is null))
        {
            _detailsText = "<color=#9aa3ad>Click a row to see its details here. Double-click it to teleport next to it. View switches between whole objects, the slot hierarchy and every single slot.</color>";
            if (_tab == InfoTab.Details)
                RefreshInfo();

            return;
        }
        var text = new StringBuilder();
        string name = entry?.Name ?? node!.Name;
        string path = entry?.Path ?? node!.Path();
        text.AppendLine($"<b>{name}</b>  <color=#9aa3ad>{path}</color>");
        if (_view == ViewMode.Users && entry is not null)
            AppendUser(text, snapshot, entry);
        else if (_view == ViewMode.Objects && entry is not null)
            AppendObject(text, snapshot, entry);
        else if (node is not null)
            AppendNode(text, snapshot, node);

        _detailsText = text.ToString().TrimEnd();
        if (_tab == InfoTab.Details)
            RefreshInfo();
    }

    private static void AppendObject(StringBuilder text, Snapshot snapshot, ObjectEntry entry)
    {
        AssetTotals assets = entry.Assets;
        text.AppendLine($"File size {Format.Bytes(entry.SizeBytes)}: textures {Format.Bytes(assets.TextureBytes)} ({assets.Textures}), meshes {Format.Bytes(assets.MeshBytes)} ({assets.Meshes}), audio {Format.Bytes(assets.AudioBytes)} ({assets.AudioClips}). {entry.Slots:N0} slots, {entry.Components:N0} components, {entry.FluxNodes:N0} ProtoFlux nodes, {(entry.Persistent ? "persistent" : "not persistent")}.");
        if (snapshot.Cpu is not null)
            text.AppendLine($"CPU {Format.Ms(entry.CpuTotalMs)} per frame: components {Format.Ms(entry.ComponentMs)} (updates {Format.Ms(entry.CpuMs[(int)CpuCategory.Updates])}, changes {Format.Ms(entry.CpuMs[(int)CpuCategory.Changes])}, startups {Format.Ms(entry.CpuMs[(int)CpuCategory.Startups])}), ProtoFlux {Format.Ms(entry.FluxMs)}.{TopTypes(snapshot, snapshot.Find(entry.Id), true)}");
        else
            text.AppendLine("CPU not measured yet. Press Profile CPU or turn on Auto scan.");

        AppendGpu(text, "GPU", entry.Gpu);
        AppendLightAndPhysics(text, "", entry.Light, entry.Physics);
        AppendExtras(text, snapshot, entry);
        if (entry.Physics.Measured > 0)
            text.AppendLine($"Physics measured: {Format.Ms(entry.Physics.MeasuredMs)} of physics step time per frame with its colliders on.");
        else if (snapshot.PhysicsMeasured > 0 && entry.Physics.Colliders > 0)
            text.AppendLine($"Physics estimated from the measured objects: about {Format.Ms(entry.Physics.ValueMs)} per frame.");
    }

    private static void AppendExtras(StringBuilder text, Snapshot snapshot, ObjectEntry entry)
    {
        ExtraTotals extra = entry.Extra;
        var parts = new List<string>();
        if (extra.BoneChains > 0)
            parts.Add($"{extra.BoneChains} dynamic bone chains with {extra.Bones:N0} bones{(snapshot.Cpu is null ? "" : $" costing {Format.Ms(entry.BonesMs)}")}");

        if (extra.AudioOutputs > 0)
            parts.Add($"{extra.AudioOutputs} audio outputs{(snapshot.Cpu is null ? "" : $" ({Format.Ms(entry.AudioMs)} on the audio thread)")}");

        if (extra.Particles > 0)
            parts.Add($"{extra.Particles:N0} live particles ({Format.Ms(entry.ParticlesMs)} background simulation)");

        if (extra.MeshRebuildsPerSecond > 0)
            parts.Add($"procedural meshes rebuilt {extra.MeshRebuildsPerSecond:F1} times a second ({Format.Count((long)extra.RebuiltVerticesPerSecond)} vertices a second)");

        if (snapshot.Cpu is not null)
            parts.Add(extra.NetUpdatesPerSecond > 0 ? $"network {extra.NetUpdatesPerSecond:F0} updates a second, {Format.Bytes((long)extra.NetOutBytesPerSecond)} out and {Format.Bytes((long)extra.NetInBytesPerSecond)} in per second" : "no network traffic");

        if (parts.Count > 0)
            text.AppendLine(string.Join(". ", parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + ".");

        if (entry.Warnings.Count > 0)
            text.AppendLine($"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>Warnings: {string.Join(". ", entry.Warnings)}.</color>");
    }

    private static void AppendUser(StringBuilder text, Snapshot snapshot, ObjectEntry entry)
    {
        GpuTotals gpu = entry.Gpu;
        AssetTotals assets = entry.Assets;
        if (snapshot.Cpu is not null)
            text.AppendLine($"CPU {Format.Ms(entry.CpuTotalMs)} per frame: components {Format.Ms(entry.ComponentMs)}, ProtoFlux {Format.Ms(entry.FluxMs)}, dynamic bones {Format.Ms(entry.BonesMs)}, audio {Format.Ms(entry.AudioMs)}, particles {Format.Ms(entry.ParticlesMs)}.{TopTypes(snapshot, snapshot.Find(entry.Id), true)}");
        else
            text.AppendLine("CPU not measured yet. Press Profile CPU or turn on Auto scan.");

        text.AppendLine($"Avatar: {Format.Count(gpu.Triangles)} triangles, {gpu.DrawCalls:N0} draw calls ({gpu.TransparentDraws:N0} transparent), {gpu.Renderers:N0} meshes, {Format.Count(gpu.SkinnedVertices)} skinned vertices, {Format.Count(gpu.BlendshapeVertices)} active blendshape vertices, GPU estimate {Format.Score(gpu.Score)}.");
        text.AppendLine($"Memory: textures {Format.Bytes(assets.TextureBytes)} ({assets.Textures}), meshes {Format.Bytes(assets.MeshBytes)} ({assets.Meshes}), audio {Format.Bytes(assets.AudioBytes)}. {entry.Slots:N0} slots, {entry.Components:N0} components, {entry.FluxNodes:N0} ProtoFlux nodes.");
        AppendLightAndPhysics(text, "", entry.Light, entry.Physics);
        AppendExtras(text, snapshot, entry);
    }

    private static void AppendNode(StringBuilder text, Snapshot snapshot, SlotNode node)
    {
        AssetTotals own = node.OwnSize ?? new AssetTotals();
        string cpuOwn = snapshot.Cpu is null ? "not measured" : Format.Ms(snapshot.Ms(node.OwnCpu));
        string cpuAll = snapshot.Cpu is null ? "not measured" : Format.Ms(snapshot.Ms(node.Cpu));
        text.AppendLine($"Own: {Format.Bytes(own.TotalBytes)} (textures {Format.Bytes(own.TextureBytes)}, meshes {Format.Bytes(own.MeshBytes)}, audio {Format.Bytes(own.AudioBytes)}), CPU {cpuOwn}, GPU {Format.Score(node.OwnGpu?.Score ?? 0)}, {node.OwnComponents:N0} components. With children: {Format.Bytes(node.Size.TotalBytes)}, CPU {cpuAll}, GPU {Format.Score(node.Gpu?.Score ?? 0)}, {node.Slots:N0} slots, {node.Components:N0} components, {node.FluxNodes:N0} ProtoFlux nodes. {(node.Persistent ? "Persistent" : "Not persistent")}{(node.Active ? "" : ", inactive")}.");
        bool subtree = _view != ViewMode.EverySlot;
        long[]? ticks = subtree ? node.Cpu : node.OwnCpu;
        if (snapshot.Cpu is not null && ticks is not null)
            text.AppendLine($"CPU {(subtree ? "with children" : "own")}: components {Format.Ms(snapshot.ComponentMs(ticks))} (updates {Format.Ms(snapshot.Ms(ticks, (int)CpuCategory.Updates))}, changes {Format.Ms(snapshot.Ms(ticks, (int)CpuCategory.Changes))}, startups {Format.Ms(snapshot.Ms(ticks, (int)CpuCategory.Startups))}), ProtoFlux {Format.Ms(snapshot.FluxMs(ticks))}.{TopTypes(snapshot, node, subtree)}");

        AppendGpu(text, subtree ? "GPU with children" : "Own GPU", (subtree ? node.Gpu : node.OwnGpu) ?? new GpuTotals());
        AppendLightAndPhysics(text, subtree ? " with children" : " own", (subtree ? node.Light : node.OwnLight) ?? new LightTotals(), (subtree ? node.Physics : node.OwnPhysics) ?? new PhysicsTotals());
    }

    private static string TopTypes(Snapshot snapshot, SlotNode? root, bool subtree)
    {
        if (root is null)
            return "";

        var totals = new Dictionary<string, long>();
        var pending = new Stack<SlotNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            SlotNode node = pending.Pop();
            if (node.OwnCpuTypes is not null)
                foreach ((string type, long ticks) in node.OwnCpuTypes)
                    totals[type] = totals.GetValueOrDefault(type) + ticks;

            if (subtree && node.Children is not null)
                foreach (SlotNode child in node.Children)
                    pending.Push(child);
        }
        if (totals.Count == 0)
            return "";

        IEnumerable<string> top = totals.OrderByDescending(pair => pair.Value).Take(4)
            .Select(pair => $"{pair.Key} {Format.Ms(pair.Value * snapshot.MsPerTick)}");
        return " Heaviest: " + string.Join(", ", top) + ".";
    }

    private static void AppendLightAndPhysics(StringBuilder text, string suffix, LightTotals light, PhysicsTotals physics)
    {
        string measured = light.Measured == 0 ? "estimated" : light.Measured == light.Lights ? "measured" : $"{light.Measured} of {light.Lights} measured";
        string lighting = light.Lights == 0
            ? $"Lighting{suffix}: no lights."
            : $"Lighting{suffix} {LightValue(light.ValueMs, light.Measured, light.Lights)} per frame ({measured}): {light.Lights} lights ({light.Shadowed} with shadows, {light.Directional} directional) lighting {Format.Count(light.LitDraws)} draws, {Format.Count(light.ShadowDraws)} shadow map draws.";
        string collision = physics.Colliders == 0
            ? $"Physics{suffix}: no colliders."
            : $"Physics{suffix} {Format.Score(physics.Score)}: {physics.Colliders} colliders ({physics.Primitives} simple, {physics.MeshColliders} mesh with {Format.Count(physics.CollisionTriangles)} triangles, {physics.Hulls} convex hulls from {Format.Count(physics.HullSourceVertices)} vertices, {physics.Moving} moving, {physics.Triggers} triggers).";
        text.AppendLine(lighting);
        text.AppendLine(collision);
    }

    private static void AppendGpu(StringBuilder text, string label, GpuTotals gpu)
    {
        text.AppendLine($"{label} {Format.Score(gpu.Score)}: {Format.Count(gpu.Triangles)} triangles, {gpu.DrawCalls:N0} draw calls ({gpu.TransparentDraws:N0} transparent with {Format.Count(gpu.TransparentTriangles)} triangles, {gpu.CutoutDraws:N0} cutout), {Format.Count(gpu.SkinnedVertices)} skinned vertices, {Format.Count(gpu.BlendshapeVertices)} blendshape vertices, {gpu.Cameras} render cameras, {gpu.RealtimeProbes} realtime probes, {Format.Count(gpu.Particles)} particles.");
    }

    private static void UpdateStatus()
    {
        SetText(_profileLabel, ProfileLabel());
        SetText(_autoLabel, AutoLabel());
        SetText(_lightLabel, LightButtonLabel());
        SetText(_physicsLabel, PhysicsButtonLabel());
        string status;
        if (PhysicsProfiler.Running)
            status = $"Measuring physics {PhysicsProfiler.Done + 1} of {PhysicsProfiler.Total}: {PhysicsProfiler.CurrentName}";
        else if (LightProfiler.Running)
            status = $"Measuring lights {LightProfiler.Done + 1} of {LightProfiler.Total}: {LightProfiler.CurrentName}";
        else if (CpuProfiler.Running)
            status = $"Profiling the CPU, {Format.Seconds(Math.Ceiling(CpuProfiler.SecondsLeft))} left...";
        else if (Telemetry.Scanning)
            status = "Scanning the world...";
        else if (WorldTelemetryMod.AutoScan && !Telemetry.AutoCycleRunning)
            status = $"Auto scan in {Format.Seconds(Math.Ceiling(Telemetry.SecondsToNextAuto))}. {Telemetry.Status}";
        else
            status = Telemetry.Status;

        SetText(_statusLine, "<nobr>" + status + "</nobr>");
        long now = Environment.TickCount64;
        if (_tab is InfoTab.World or InfoTab.History && now >= _nextInfoRefresh)
        {
            _nextInfoRefresh = now + InfoRefreshMs;
            RefreshInfo();
        }
    }

    private static string WorldText()
    {
        var text = new StringBuilder();
        Snapshot? snapshot = _shown;
        if (snapshot is null)
            return Telemetry.FocusedWorld is null ? "No world is focused." : "Scanning the focused world...";

        int persistent = snapshot.Entries.Count(entry => entry.Persistent);
        text.AppendLine($"<b>{snapshot.WorldName}</b>");
        text.AppendLine($"{snapshot.Entries.Count:N0} objects ({persistent:N0} persistent), {snapshot.TotalSlots:N0} slots, {snapshot.TotalComponents:N0} components, {Format.Bytes(snapshot.UniqueAssetBytes)} of unique assets.");
        text.AppendLine($"Scanned {Format.Age(snapshot.TakenTick)} in {snapshot.ScanMs:F0} ms.");
        if (snapshot.Cpu is CpuSummary cpu)
        {
            text.AppendLine($"CPU measured for {Format.Seconds(cpu.Seconds)} ({cpu.Frames:N0} frames): {Format.Ms(cpu.TotalMs)} per frame. Components {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Updates] + cpu.CategoryMs[(int)CpuCategory.Changes] + cpu.CategoryMs[(int)CpuCategory.Startups])}, ProtoFlux {Format.Ms(cpu.CategoryMs[(int)CpuCategory.ProtoFlux])}.");
            if (cpu.Coverage < 0.9 || cpu.MissingHooks > 0)
                text.AppendLine($"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>Only {cpu.Coverage * 100:F0}% of calls were timed.</color>");
        }
        else
            text.AppendLine("CPU not measured yet. Press Profile CPU or turn on Auto scan.");

        int lights = snapshot.Entries.Sum(entry => entry.Light.Lights);
        text.AppendLine(snapshot.MeasuredLights > 0
            ? $"Lighting: {snapshot.MeasuredLights} of {lights} lights measured. The rest are estimates, marked ~."
            : $"Lighting: {lights} lights, all estimated (marked ~). Press Measure lights to time each one.");
        int colliders = snapshot.Entries.Sum(entry => entry.Physics.Colliders);
        text.AppendLine(snapshot.PhysicsMeasured > 0
            ? $"Physics: {snapshot.PhysicsMeasured} objects measured in milliseconds of the physics step. The rest are estimated from them, marked ~."
            : $"Physics: {colliders:N0} colliders, scored by complexity. Press Measure physics to time the heaviest objects.");
        int flagged = snapshot.Entries.Count(entry => entry.Warnings.Count > 0);
        if (flagged > 0)
            text.AppendLine($"Warnings: {flagged} objects have something worth fixing. Sort by Warn to see them.");

        if (snapshot.Extra is ExtraResult extra)
        {
            double net = snapshot.Entries.Sum(entry => entry.Extra.NetBytesPerSecond) + snapshot.NetUnattributedBytesPerSecond;
            text.AppendLine($"Dynamic bones: {Format.Ms(extra.BonesManagerMs)} per frame for the whole world (engine total). Network: {Format.Bytes((long)net)} per second, {Format.Bytes((long)snapshot.NetUnattributedBytesPerSecond)} of it not tied to an object.");
        }
        if (Heatmap.Enabled)
        {
            text.AppendLine($"Heatmap by {Heatmap.Label}: {Heatmap.Colored:N0} objects colored, <color=#59ff73>green</color> lowest to <color=#ff1414>red</color> highest{(Heatmap.SkippedLarge > 0 ? $", {Heatmap.SkippedLarge} larger than {WorldTelemetryMod.HeatmapMaximumSize:F0} m skipped" : "")}.");
            if (Heatmap.Metric == HeatMetric.Lighting)
                text.AppendLine($"Light ranges: {Heatmap.Volumes:N0} lights drawn (spheres for point lights, cones for spot lights, small cones for directional lights), colored by each light's own cost. Ranges over {WorldTelemetryMod.LightVolumeMaximum:F0} m are shown as a small marker.");
        }

        if (Telemetry.FocusedWorld is World focused && focused != snapshot.World)
            text.AppendLine($"<color={Hex(RadiantUI_Constants.Hero.YELLOW_HEX)}>This is not the focused world. Press Rescan.</color>");

        return text.ToString().TrimEnd();
    }

    private static string ProfileLabel() => CpuProfiler.Running
        ? $"<nobr>Profiling... {Math.Ceiling(CpuProfiler.SecondsLeft):F0}</nobr>"
        : $"<nobr>Profile CPU ({WorldTelemetryMod.ProfileSeconds} seconds)</nobr>";

    private static (Text Label, InteractionElement.ColorDriver Hover) TextButton(UIBuilder ui, string name, float width, string content, Action pressed, float height = 52f)
    {
        ui.Style.MinWidth = width;
        ui.Style.PreferredWidth = width;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = height;
        ui.Style.PreferredHeight = height;
        ui.Style.FlexibleHeight = -1f;
        Slot slot = ui.Next(name);
        Image background = slot.AttachComponent<Image>();
        background.Tint.Value = colorX.Clear;
        Button button = slot.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = ConfigureHover(button, background, colorX.Clear);
        button.LocalPressed += (_, _) => pressed();

        ui.NestInto(slot);
        Text label = ui.Text(content, 22f, true, Alignment.MiddleCenter, true);
        label.AutoSizeMin.Value = 12f;
        label.AutoSizeMax.Value = 22f;
        label.Color.Value = colorX.White;
        RectTransform labelRect = label.Slot.GetComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = new float2(10f, 4f);
        labelRect.OffsetMax.Value = new float2(-10f, -4f);
        ui.NestOut();
        return (label, hover);
    }

    private static InteractionElement.ColorDriver ConfigureHover(Button button, Image background, colorX normal)
    {
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = normal;
        hover.HighlightColor.Value = RadiantUI_Constants.Hero.CYAN.SetA(0.2f);
        hover.PressColor.Value = RadiantUI_Constants.Hero.CYAN.SetA(0.45f);
        hover.DisabledColor.Value = colorX.Clear;
        return hover;
    }

    private static void Row(Slot slot, float spacing, Alignment alignment, float padding = 0f)
    {
        HorizontalLayout row = slot.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = spacing;
        row.PaddingTop.Value = padding;
        row.PaddingRight.Value = padding;
        row.PaddingBottom.Value = padding;
        row.PaddingLeft.Value = padding;
        row.ChildAlignment = alignment;
        row.ForceExpandWidth.Value = false;
        row.ForceExpandHeight.Value = false;
    }

    private static string Hex(string hex) => hex.StartsWith('#') ? hex : "#" + hex;
}
