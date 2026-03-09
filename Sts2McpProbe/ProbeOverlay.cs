using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public List<string> Alerts { get; set; } = new();
}

internal static class ProbeOverlayManager
{
    private const string OverlayNodeName = "Sts2McpOverlay";
    private static ProbeOverlayNode? _overlay;

    public static void Update(OverlayPayload payload)
    {
        EnsureAttached();
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
        {
            _overlay.ApplyPayload(payload);
        }
    }

    private static void EnsureAttached()
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
        {
            Node? parent = _overlay.GetParent();
            if (parent != null && GodotObject.IsInstanceValid(parent))
            {
                return;
            }
        }

        Node? host = (Node?)NRun.Instance?.GlobalUi ?? NGame.Instance;
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            return;
        }

        ProbeOverlayNode? existing = host.GetNodeOrNull<ProbeOverlayNode>(OverlayNodeName);
        if (existing != null && GodotObject.IsInstanceValid(existing))
        {
            _overlay = existing;
            return;
        }

        _overlay = new ProbeOverlayNode
        {
            Name = OverlayNodeName
        };
        host.AddChildSafely(_overlay);
    }
}

internal sealed class ProbeOverlayNode : PanelContainer
{
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
    private MarginContainer _contentContainer = null!;
    private Button _collapseButton = null!;
    private bool _collapsed;
    private bool _dragging;
    private Vector2 _dragOffset;
    private PanelType _activePanel = PanelType.Combat;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = new Vector2(18, 108);
        CustomMinimumSize = new Vector2(360, 230);
        Size = new Vector2(360, 230);

        AddThemeStyleboxOverride("panel", CreatePanelStyle());
        BuildUi();
        RefreshVisualState();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent &&
            keyEvent.Pressed &&
            !keyEvent.Echo &&
            keyEvent.Keycode == Key.F8)
        {
            Visible = !Visible;
            GetViewport()?.SetInputAsHandled();
        }
    }

    public void ApplyPayload(OverlayPayload payload)
    {
        _payload = payload;
        _panelContent[PanelType.Combat] = payload.CombatPanel;
        _panelContent[PanelType.Total] = payload.TotalPanel;
        _panelContent[PanelType.Route] = payload.RoutePanel;
        _panelContent[PanelType.Logs] = payload.LogsPanel;
        RefreshVisualState();
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.07f, 0.09f, 0.12f, 0.84f),
            BorderColor = new Color(0.34f, 0.80f, 0.92f, 0.85f),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.28f),
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

        PanelContainer dragBar = new();
        dragBar.MouseFilter = MouseFilterEnum.Stop;
        dragBar.AddThemeStyleboxOverride("panel", CreateDragBarStyle());
        dragBar.GuiInput += OnDragBarInput;
        root.AddChild(dragBar);

        HBoxContainer titleRow = new();
        titleRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        dragBar.AddChild(titleRow);

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
            Text = "拖拽标题栏移动 | F8 显示/隐藏",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        tips.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.84f));
        body.AddChild(tips);
    }

    private static StyleBoxFlat CreateDragBarStyle()
    {
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.13f, 0.19f, 0.26f, 0.92f),
            BorderColor = new Color(0.33f, 0.67f, 0.84f, 0.96f),
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

    private void RefreshVisualState()
    {
        _statusLabel.Text = $"{(_payload.IsInCombat ? "战斗中" : "观察")} / {_payload.Mode}";
        _summaryLabel.Text = _payload.Summary;
        _alertsLabel.Text = _payload.Alerts.Count == 0
            ? string.Empty
            : string.Join(global::System.Environment.NewLine, _payload.Alerts.Take(2).Select(static a => "提醒: " + a));

        if (!_panelContent.TryGetValue(_activePanel, out string? content))
        {
            content = "暂无数据";
        }

        _contentLabel.Text = content;

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
        CustomMinimumSize = _collapsed ? new Vector2(360, 38) : new Vector2(360, 230);
        if (_collapsed)
        {
            Size = new Vector2(Mathf.Max(Size.X, 360), 38);
        }

        TooltipText = "更新时间: " + _payload.TimestampUtc;
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
}
