using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2Mcp;

internal sealed class OverlayPayload
{
    public string TimestampUtc { get; set; } = string.Empty;
    public string Mode { get; set; } = "singleplayer";
    public bool IsInCombat { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string CombatPanel { get; set; } = string.Empty;
    public string TotalPanel { get; set; } = string.Empty;
    public string RoutePanel { get; set; } = string.Empty;
    public string LogsPanel { get; set; } = string.Empty;
    public string CombatBoardScope { get; set; } = "当前战斗";
    public List<OverlayDamageEntry> CombatDamageBoard { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
}

internal sealed class OverlayDamageEntry
{
    public string Name { get; set; } = string.Empty;
    public int Damage { get; set; }
    public double Ratio { get; set; }
    public bool IsLocalPlayer { get; set; }
}

internal static class ProbeOverlayManager
{
    private const string OverlayNodeName = "Sts2McpOverlay";
    private static readonly object StateLock = new();
    private static ProbeOverlayNode? _overlay;
    private static OverlayPayload? _pendingPayload;
    private static Action<string>? _logger;
    private static int _flushQueued;
    private static DateTime _lastNoHostLogUtc = DateTime.MinValue;
    private static DateTime _lastNoOverlayLogUtc = DateTime.MinValue;
    private static string _lastHostSignature = string.Empty;

    public static void SetLogger(Action<string> logger)
    {
        _logger = logger;
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
        {
            _overlay.DebugLogger = logger;
        }

        QueueMainThreadFlush();
    }

    public static void Update(OverlayPayload payload)
    {
        lock (StateLock)
        {
            _pendingPayload = payload;
        }

        QueueMainThreadFlush();
    }

    private static void QueueMainThreadFlush(bool forceDeferred = false)
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1)
        {
            return;
        }

        if (!forceDeferred && NGame.IsMainThread())
        {
            FlushOnMainThread();
            return;
        }

        Callable.From(FlushOnMainThread).CallDeferred();
    }

    private static void FlushOnMainThread()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        EnsureAttached();

        OverlayPayload? payload = null;
        lock (StateLock)
        {
            if (_pendingPayload != null)
            {
                payload = _pendingPayload;
                _pendingPayload = null;
            }
        }

        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay) || !_overlay.IsInsideTree())
        {
            if (payload != null)
            {
                lock (StateLock)
                {
                    _pendingPayload = payload;
                }
            }

            DateTime utcNow = DateTime.UtcNow;
            if (utcNow - _lastNoOverlayLogUtc >= TimeSpan.FromSeconds(2))
            {
                _lastNoOverlayLogUtc = utcNow;
                Log("Flush skipped: overlay not ready (null/invalid/not_in_tree). Retrying.");
            }

            QueueMainThreadFlush(forceDeferred: true);
            return;
        }

        if (payload != null)
        {
            _overlay.EnsureInitialized();
            _overlay.Visible = true;
            _overlay.Show();
            _overlay.MoveToFront();
            _overlay.ApplyPayload(payload);
        }

        bool hasMorePayload;
        lock (StateLock)
        {
            hasMorePayload = _pendingPayload != null;
        }

        if (hasMorePayload)
        {
            QueueMainThreadFlush(forceDeferred: true);
        }
    }

    private static void EnsureAttached()
    {
        Node? host = ResolveHostNode();
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            DateTime utcNow = DateTime.UtcNow;
            if (utcNow - _lastNoHostLogUtc >= TimeSpan.FromSeconds(3))
            {
                _lastNoHostLogUtc = utcNow;
                Log("No valid host node yet (GlobalUi/NGame/Root unavailable).");
            }
            return;
        }

        if (!host.IsInsideTree())
        {
            DateTime utcNow = DateTime.UtcNow;
            if (utcNow - _lastNoHostLogUtc >= TimeSpan.FromSeconds(3))
            {
                _lastNoHostLogUtc = utcNow;
                Log("Host exists but is not inside tree yet: " + DescribeNode(host));
            }

            return;
        }

        string hostSignature = DescribeNode(host);
        if (!string.Equals(_lastHostSignature, hostSignature, StringComparison.Ordinal))
        {
            _lastHostSignature = hostSignature;
            Log("Overlay host resolved: " + hostSignature);
        }

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
        {
            Node? parent = _overlay.GetParent();
            if (parent != null && GodotObject.IsInstanceValid(parent))
            {
                if (ReferenceEquals(parent, host))
                {
                    _overlay.EnsureInitialized();
                    _overlay.Visible = true;
                    _overlay.Show();
                    _overlay.MoveToFront();
                    return;
                }

                parent.RemoveChildSafely(_overlay);
                host.AddChildSafely(_overlay);
                _overlay.DebugLogger = _logger;
                _overlay.EnsureInitialized();
                _overlay.Visible = true;
                _overlay.Show();
                _overlay.MoveToFront();
                Log("Overlay reparented to host.");
                return;
            }
        }

        ProbeOverlayNode? existing = host.GetNodeOrNull<ProbeOverlayNode>(OverlayNodeName);
        if (existing != null && GodotObject.IsInstanceValid(existing))
        {
            _overlay = existing;
            _overlay.DebugLogger = _logger;
            _overlay.EnsureInitialized();
            _overlay.Visible = true;
            _overlay.Show();
            _overlay.MoveToFront();
            Log("Overlay node reused from existing host child.");
            return;
        }

        _overlay = new ProbeOverlayNode
        {
            Name = OverlayNodeName,
            DebugLogger = _logger
        };
        host.AddChildSafely(_overlay);
        _overlay.EnsureInitialized();
        _overlay.Visible = true;
        _overlay.Show();
        _overlay.MoveToFront();
        Log("Overlay node created and attached. main_thread=" + NGame.IsMainThread() + " inside_tree=" + _overlay.IsInsideTree());
    }

    private static Node? ResolveHostNode()
    {
        Node? runUi = (Node?)NRun.Instance?.GlobalUi;
        if (runUi != null && GodotObject.IsInstanceValid(runUi))
        {
            return runUi;
        }

        Node? run = (Node?)NRun.Instance;
        if (run != null && GodotObject.IsInstanceValid(run))
        {
            return run;
        }

        Node? game = (Node?)NGame.Instance;
        if (game != null && GodotObject.IsInstanceValid(game))
        {
            Node? root = game.GetTree()?.Root;
            if (root != null && GodotObject.IsInstanceValid(root))
            {
                return root;
            }

            return game;
        }

        return null;
    }

    private static string DescribeNode(Node node)
    {
        string type = node.GetType().Name;
        string name = node.Name.ToString();
        string parent = node.GetParent()?.Name.ToString() ?? "<root>";
        return $"{type}({name}) <- {parent}";
    }

    private static void Log(string message)
    {
        _logger?.Invoke("[Overlay] " + message);
    }
}

internal sealed class ProbeOverlayNode : PanelContainer
{
    private const float MinOpacity = 0.35f;
    private const float MaxOpacity = 0.95f;
    private const float OpacityStep = 0.05f;
    private const float DefaultOpacity = 0.62f;
    private const float ExpandedHeight = 230f;
    private const float CollapsedHeight = 38f;

    private enum PanelType
    {
        Combat,
        Total,
        Route,
        Logs
    }

    private readonly Dictionary<PanelType, Button> _tabButtons = new();
    private readonly Dictionary<PanelType, string> _panelContent = new();
    private OverlayPayload _payload = new();
    private Label _titleLabel = null!;
    private Label _statusLabel = null!;
    private Label _summaryLabel = null!;
    private Label _alertsLabel = null!;
    private RichTextLabel _contentLabel = null!;
    private MarginContainer _combatBoardContainer = null!;
    private Label _combatBoardTitleLabel = null!;
    private Label _combatBoardScopeLabel = null!;
    private VBoxContainer _combatBoardRows = null!;
    private MarginContainer _contentContainer = null!;
    private PanelContainer _dragBar = null!;
    private Button _collapseButton = null!;
    private Button _combatViewToggleButton = null!;
    private Label _opacityLabel = null!;
    private StyleBoxFlat _panelStyle = null!;
    private StyleBoxFlat _dragBarStyle = null!;
    private bool _collapsed;
    private bool _uiReady;
    private bool _dragging;
    private bool _showCombatBoard = true;
    private float _opacity = DefaultOpacity;
    private Vector2 _dragOffset;
    private PanelType _activePanel = PanelType.Combat;
    private DateTime _lastProcessLogUtc = DateTime.MinValue;
    private bool _initialized;
    internal Action<string>? DebugLogger { get; set; }

    public override void _EnterTree()
    {
        LogDebug("EnterTree");
    }

    public override void _Ready()
    {
        EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ProcessMode = ProcessModeEnum.Always;
        TopLevel = false;
        ZIndex = 2200;
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = new Vector2(18, 108);
        CustomMinimumSize = new Vector2(360, ExpandedHeight);
        Size = new Vector2(360, ExpandedHeight);
        Visible = true;
        Show();

        _panelStyle = CreatePanelStyle(_opacity);
        AddThemeStyleboxOverride("panel", _panelStyle);
        BuildUi();
        _uiReady = true;
        RefreshVisualState();
        MoveToFront();
        LogDebug($"EnsureInitialized visible={Visible} pos={Position} size={Size} parent={GetParent()?.Name}");
    }

    public override void _Process(double delta)
    {
        DateTime utcNow = DateTime.UtcNow;
        if (utcNow - _lastProcessLogUtc < TimeSpan.FromSeconds(4))
        {
            return;
        }

        _lastProcessLogUtc = utcNow;
        LogDebug($"Tick visible={Visible} global={GlobalPosition} size={Size} collapsed={_collapsed}");
    }

    public override void _ExitTree()
    {
        LogDebug("ExitTree");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent &&
            keyEvent.Pressed &&
            !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F8)
            {
                Visible = !Visible;
                GetViewport()?.SetInputAsHandled();
                LogDebug("F8 toggled visible=" + Visible);
                return;
            }

            if (keyEvent.Keycode == Key.F7)
            {
                Visible = true;
                _collapsed = false;
                Position = new Vector2(18, 108);
                Size = new Vector2(Mathf.Max(Size.X, 360), ExpandedHeight);
                RefreshVisualState();
                GetViewport()?.SetInputAsHandled();
                LogDebug("F7 reset overlay position/visibility.");
            }
        }
    }

    public void ApplyPayload(OverlayPayload payload)
    {
        _payload = payload;
        _panelContent[PanelType.Combat] = payload.CombatPanel;
        _panelContent[PanelType.Total] = payload.TotalPanel;
        _panelContent[PanelType.Route] = payload.RoutePanel;
        _panelContent[PanelType.Logs] = payload.LogsPanel;
        if (!_uiReady)
        {
            return;
        }

        RefreshVisualState();
    }

    private static StyleBoxFlat CreatePanelStyle(float opacity)
    {
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.07f, 0.09f, 0.12f, opacity),
            BorderColor = new Color(0.34f, 0.80f, 0.92f, Mathf.Clamp(opacity + 0.12f, 0f, 1f)),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            ShadowColor = new Color(0, 0, 0, Mathf.Clamp(opacity * 0.38f, 0.12f, 0.42f)),
            ShadowSize = 5
        };
        return style;
    }

    private void BuildUi()
    {
        MarginContainer rootMargin = new();
        rootMargin.AddThemeConstantOverride("margin_left", 8);
        rootMargin.AddThemeConstantOverride("margin_top", 6);
        rootMargin.AddThemeConstantOverride("margin_right", 8);
        rootMargin.AddThemeConstantOverride("margin_bottom", 8);
        AddChild(rootMargin);

        VBoxContainer root = new();
        rootMargin.AddChild(root);

        _dragBar = new PanelContainer();
        _dragBar.MouseFilter = MouseFilterEnum.Stop;
        _dragBarStyle = CreateDragBarStyle(_opacity);
        _dragBar.AddThemeStyleboxOverride("panel", _dragBarStyle);
        _dragBar.GuiInput += OnDragBarInput;
        root.AddChild(_dragBar);

        HBoxContainer titleRow = new();
        titleRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _dragBar.AddChild(titleRow);

        _titleLabel = new Label
        {
            Text = "STS2 MCP Overlay",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.99f, 1f));
        titleRow.AddChild(_titleLabel);

        _statusLabel = new Label
        {
            Text = "Ready"
        };
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.90f, 1f));
        titleRow.AddChild(_statusLabel);

        Button opacityDownButton = new()
        {
            Text = "A-",
            Flat = true,
            CustomMinimumSize = new Vector2(30, 22),
            FocusMode = FocusModeEnum.None,
            TooltipText = "降低面板透明度"
        };
        opacityDownButton.AddThemeColorOverride("font_color", new Color(0.86f, 0.91f, 1f));
        opacityDownButton.Pressed += () => ChangeOpacity(-OpacityStep);
        titleRow.AddChild(opacityDownButton);

        _opacityLabel = new Label
        {
            Text = "62%",
            CustomMinimumSize = new Vector2(44, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _opacityLabel.AddThemeColorOverride("font_color", new Color(0.76f, 0.88f, 1f));
        titleRow.AddChild(_opacityLabel);

        Button opacityUpButton = new()
        {
            Text = "A+",
            Flat = true,
            CustomMinimumSize = new Vector2(30, 22),
            FocusMode = FocusModeEnum.None,
            TooltipText = "提高面板透明度"
        };
        opacityUpButton.AddThemeColorOverride("font_color", new Color(0.86f, 0.91f, 1f));
        opacityUpButton.Pressed += () => ChangeOpacity(OpacityStep);
        titleRow.AddChild(opacityUpButton);

        _combatViewToggleButton = new Button
        {
            Text = "详情",
            Flat = true,
            CustomMinimumSize = new Vector2(42, 22),
            FocusMode = FocusModeEnum.None,
            TooltipText = "战斗页切换：伤害榜/详情"
        };
        _combatViewToggleButton.AddThemeColorOverride("font_color", new Color(0.96f, 0.85f, 0.66f));
        _combatViewToggleButton.Pressed += ToggleCombatViewMode;
        titleRow.AddChild(_combatViewToggleButton);

        _collapseButton = new Button
        {
            Text = "—",
            Flat = true,
            CustomMinimumSize = new Vector2(28, 22),
            FocusMode = FocusModeEnum.None
        };
        _collapseButton.AddThemeColorOverride("font_color", new Color(0.86f, 0.91f, 1f));
        _collapseButton.Pressed += ToggleCollapsed;
        titleRow.AddChild(_collapseButton);

        HBoxContainer tabs = new();
        tabs.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tabs.AddThemeConstantOverride("separation", 6);
        root.AddChild(tabs);

        AddTabButton(tabs, PanelType.Combat, "战斗");
        AddTabButton(tabs, PanelType.Total, "总计");
        AddTabButton(tabs, PanelType.Route, "路线");
        AddTabButton(tabs, PanelType.Logs, "日志");

        _contentContainer = new MarginContainer();
        _contentContainer.AddThemeConstantOverride("margin_left", 4);
        _contentContainer.AddThemeConstantOverride("margin_top", 4);
        _contentContainer.AddThemeConstantOverride("margin_right", 4);
        _contentContainer.AddThemeConstantOverride("margin_bottom", 4);
        root.AddChild(_contentContainer);

        VBoxContainer body = new();
        _contentContainer.AddChild(body);

        _summaryLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "等待数据..."
        };
        _summaryLabel.AddThemeColorOverride("font_color", new Color(0.94f, 0.97f, 1f));
        body.AddChild(_summaryLabel);

        _alertsLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _alertsLabel.AddThemeColorOverride("font_color", new Color(1f, 0.78f, 0.60f));
        body.AddChild(_alertsLabel);

        _combatBoardContainer = new MarginContainer();
        _combatBoardContainer.AddThemeConstantOverride("margin_left", 2);
        _combatBoardContainer.AddThemeConstantOverride("margin_top", 2);
        _combatBoardContainer.AddThemeConstantOverride("margin_right", 2);
        _combatBoardContainer.AddThemeConstantOverride("margin_bottom", 2);
        body.AddChild(_combatBoardContainer);

        VBoxContainer boardRoot = new();
        boardRoot.AddThemeConstantOverride("separation", 4);
        _combatBoardContainer.AddChild(boardRoot);

        _combatBoardTitleLabel = new Label
        {
            Text = "伤害输出",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _combatBoardTitleLabel.AddThemeColorOverride("font_color", new Color(0.97f, 0.84f, 0.33f));
        boardRoot.AddChild(_combatBoardTitleLabel);

        _combatBoardScopeLabel = new Label
        {
            Text = "当前战斗",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _combatBoardScopeLabel.AddThemeColorOverride("font_color", new Color(0.76f, 0.83f, 0.93f));
        boardRoot.AddChild(_combatBoardScopeLabel);

        _combatBoardRows = new VBoxContainer();
        _combatBoardRows.AddThemeConstantOverride("separation", 3);
        boardRoot.AddChild(_combatBoardRows);

        _contentLabel = new RichTextLabel
        {
            FitContent = false,
            ScrollActive = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(330, 130),
            SelectionEnabled = false,
            BbcodeEnabled = false
        };
        _contentLabel.AddThemeColorOverride("default_color", new Color(0.88f, 0.94f, 1f));
        body.AddChild(_contentLabel);

        Label tips = new()
        {
            Text = "拖拽标题栏移动 | A-/A+ 调透明 | F8 显示/隐藏 | F7 重置",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        tips.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.84f));
        body.AddChild(tips);

        ApplyOpacity();
        UpdateOpacityLabel();
    }

    private static StyleBoxFlat CreateDragBarStyle(float opacity)
    {
        float headerOpacity = Mathf.Clamp(opacity + 0.18f, MinOpacity, 1f);
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.13f, 0.19f, 0.26f, headerOpacity),
            BorderColor = new Color(0.33f, 0.67f, 0.84f, Mathf.Clamp(headerOpacity + 0.08f, 0f, 1f)),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
        return style;
    }

    private void AddTabButton(Container parent, PanelType panelType, string title)
    {
        Button button = new()
        {
            Text = title,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(70, 24)
        };
        button.Pressed += () => SwitchPanel(panelType);
        _tabButtons[panelType] = button;
        parent.AddChild(button);
    }

    private void SwitchPanel(PanelType panelType)
    {
        _activePanel = panelType;
        RefreshVisualState();
    }

    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        RefreshVisualState();
    }

    private void ToggleCombatViewMode()
    {
        _showCombatBoard = !_showCombatBoard;
        RefreshVisualState();
    }

    private void RefreshVisualState()
    {
        if (!_uiReady || !GodotObject.IsInstanceValid(_statusLabel))
        {
            return;
        }

        _statusLabel.Text = $"{(_payload.IsInCombat ? "战斗中" : "观察")} / {_payload.Mode}";
        _summaryLabel.Text = _payload.Summary;
        List<string> alerts = _payload.Alerts ?? new List<string>();
        _alertsLabel.Text = alerts.Count == 0
            ? string.Empty
            : string.Join(global::System.Environment.NewLine, alerts.Take(2).Select(static a => "提醒: " + a));

        if (!_panelContent.TryGetValue(_activePanel, out string? content))
        {
            content = "暂无数据";
        }

        _contentLabel.Text = content;

        bool combatBoardActive = _activePanel == PanelType.Combat && _showCombatBoard;
        _combatViewToggleButton.Visible = _activePanel == PanelType.Combat;
        _combatViewToggleButton.Text = combatBoardActive ? "详情" : "榜单";
        _combatViewToggleButton.TooltipText = combatBoardActive ? "切换到战斗详情文本" : "切换到伤害排行";

        _summaryLabel.Visible = !combatBoardActive;
        _alertsLabel.Visible = !combatBoardActive;
        _combatBoardContainer.Visible = combatBoardActive;
        _contentLabel.Visible = !combatBoardActive;
        if (combatBoardActive)
        {
            RefreshCombatBoardRows();
        }

        foreach ((PanelType tab, Button button) in _tabButtons)
        {
            bool active = tab == _activePanel;
            button.Modulate = active
                ? new Color(0.95f, 0.90f, 0.66f)
                : new Color(0.74f, 0.84f, 0.96f);
            button.Text = active ? $"[{TabTitle(tab)}]" : TabTitle(tab);
        }

        _contentContainer.Visible = !_collapsed;
        _collapseButton.Text = _collapsed ? "□" : "—";
        UpdateOpacityLabel();
        CustomMinimumSize = _collapsed ? new Vector2(360, CollapsedHeight) : new Vector2(360, ExpandedHeight);
        if (_collapsed)
        {
            Size = new Vector2(Mathf.Max(Size.X, 360), CollapsedHeight);
        }

        TooltipText = "更新时间: " + _payload.TimestampUtc;
    }

    private void RefreshCombatBoardRows()
    {
        _combatBoardTitleLabel.Text = "伤害输出";
        _combatBoardScopeLabel.Text = _payload.CombatBoardScope;

        foreach (Node child in _combatBoardRows.GetChildren())
        {
            child.QueueFree();
        }

        List<OverlayDamageEntry> rows = _payload.CombatDamageBoard ?? new List<OverlayDamageEntry>();
        if (rows.Count == 0)
        {
            _combatBoardRows.AddChild(CreatePlaceholderRow("暂无伤害数据"));
            return;
        }

        int rank = 1;
        foreach (OverlayDamageEntry row in rows.Take(8))
        {
            _combatBoardRows.AddChild(CreateDamageRow(rank, row));
            rank++;
        }
    }

    private static Control CreatePlaceholderRow(string text)
    {
        Label label = new()
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        label.AddThemeColorOverride("font_color", new Color(0.74f, 0.80f, 0.90f));
        return label;
    }

    private static PanelContainer CreateDamageRow(int rank, OverlayDamageEntry row)
    {
        PanelContainer panel = new();
        panel.AddThemeStyleboxOverride("panel", CreateDamageRowStyle(row.IsLocalPlayer));

        HBoxContainer hbox = new();
        panel.AddChild(hbox);

        Label name = new()
        {
            Text = $"{rank}. {row.Name}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        name.AddThemeColorOverride("font_color", row.IsLocalPlayer
            ? new Color(0.97f, 0.89f, 1f)
            : new Color(0.90f, 0.96f, 1f));
        hbox.AddChild(name);

        Label value = new()
        {
            Text = $"{row.Damage} ({row.Ratio * 100:F1}%)",
            HorizontalAlignment = HorizontalAlignment.Right,
            CustomMinimumSize = new Vector2(108, 0)
        };
        value.AddThemeColorOverride("font_color", new Color(0.98f, 0.90f, 0.70f));
        hbox.AddChild(value);

        return panel;
    }

    private static StyleBoxFlat CreateDamageRowStyle(bool isLocal)
    {
        StyleBoxFlat style = new()
        {
            BgColor = isLocal
                ? new Color(0.45f, 0.22f, 0.46f, 0.88f)
                : new Color(0.20f, 0.34f, 0.47f, 0.84f),
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = isLocal
                ? new Color(0.82f, 0.58f, 0.92f, 0.96f)
                : new Color(0.58f, 0.80f, 0.95f, 0.85f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        return style;
    }

    private void ChangeOpacity(float delta)
    {
        _opacity = Mathf.Clamp(_opacity + delta, MinOpacity, MaxOpacity);
        ApplyOpacity();
        UpdateOpacityLabel();
    }

    private void ApplyOpacity()
    {
        if (_panelStyle != null)
        {
            _panelStyle.BgColor = new Color(0.07f, 0.09f, 0.12f, _opacity);
            _panelStyle.BorderColor = new Color(0.34f, 0.80f, 0.92f, Mathf.Clamp(_opacity + 0.12f, 0f, 1f));
            _panelStyle.ShadowColor = new Color(0, 0, 0, Mathf.Clamp(_opacity * 0.38f, 0.12f, 0.42f));
            AddThemeStyleboxOverride("panel", _panelStyle);
        }

        if (_dragBarStyle != null)
        {
            float headerOpacity = Mathf.Clamp(_opacity + 0.18f, MinOpacity, 1f);
            _dragBarStyle.BgColor = new Color(0.13f, 0.19f, 0.26f, headerOpacity);
            _dragBarStyle.BorderColor = new Color(0.33f, 0.67f, 0.84f, Mathf.Clamp(headerOpacity + 0.08f, 0f, 1f));
            if (_dragBar != null)
            {
                _dragBar.AddThemeStyleboxOverride("panel", _dragBarStyle);
            }
        }
    }

    private void UpdateOpacityLabel()
    {
        if (_opacityLabel == null)
        {
            return;
        }

        int percent = (int)Mathf.Round(_opacity * 100f);
        _opacityLabel.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string TabTitle(PanelType panelType)
    {
        return panelType switch
        {
            PanelType.Combat => "战斗",
            PanelType.Total => "总计",
            PanelType.Route => "路线",
            PanelType.Logs => "日志",
            _ => panelType.ToString()
        };
    }

    private void OnDragBarInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton &&
            mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                _dragging = true;
                _dragOffset = mouseButton.GlobalPosition - GlobalPosition;
                AcceptEvent();
            }
            else
            {
                _dragging = false;
                AcceptEvent();
            }

            return;
        }

        if (@event is InputEventMouseMotion mouseMotion && _dragging)
        {
            Vector2 target = mouseMotion.GlobalPosition - _dragOffset;
            Rect2 viewportRect = GetViewportRect();
            float maxX = Mathf.Max(0, viewportRect.Size.X - Size.X - 4);
            float maxY = Mathf.Max(0, viewportRect.Size.Y - Size.Y - 4);
            target.X = Mathf.Clamp(target.X, 4, maxX);
            target.Y = Mathf.Clamp(target.Y, 4, maxY);
            GlobalPosition = target;
            AcceptEvent();
        }
    }

    private void LogDebug(string message)
    {
        DebugLogger?.Invoke("[OverlayNode] " + message);
    }
}
