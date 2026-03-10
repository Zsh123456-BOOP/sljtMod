using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

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
    public List<OverlayRouteLegendEntry> RouteLegendEntries { get; set; } = new();
    public List<OverlayRouteOption> RouteOptions { get; set; } = new();
    public int SelectedRouteOptionIndex { get; set; } = -1;
    public OverlayModifierState ModifierState { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
}

internal sealed class OverlayDamageEntry
{
    public string Name { get; set; } = string.Empty;
    public double ContributionScore { get; set; }
    public double Ndps { get; set; }
    public double SupportDamage { get; set; }
    public double Mitigation { get; set; }
    public double TeamBlock { get; set; }
    public double StatusScore { get; set; }
    public double Ratio { get; set; }
    public bool IsLocalPlayer { get; set; }
}

internal sealed class OverlayRouteOption
{
    public int OptionIndex { get; set; }
    public string Label { get; set; } = string.Empty;
    public string PointTypeLabel { get; set; } = string.Empty;
    public double Score { get; set; }
    public string RiskTag { get; set; } = string.Empty;
    public List<string> PathTypeKeys { get; set; } = new();
    public List<OverlayRouteCoord> PathCoords { get; set; } = new();
}

internal sealed class OverlayRouteLegendEntry
{
    public string TypeKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal sealed class OverlayRouteCoord
{
    public int Row { get; set; }
    public int Col { get; set; }
}

internal sealed class OverlayModifierState
{
    public string PlayerName { get; set; } = string.Empty;
    public string CharacterId { get; set; } = string.Empty;
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public int RelicCount { get; set; }
    public int DeckCount { get; set; }
    public List<OverlayModifierOwnedRelic> Relics { get; set; } = new();
    public List<OverlayModifierDeckCardEntry> DeckCards { get; set; } = new();
}

internal sealed class OverlayModifierCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
}

internal sealed class OverlayModifierOwnedRelic
{
    public string RelicId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

internal sealed class OverlayModifierDeckCardEntry
{
    public int DeckIndex { get; set; }
    public string CardId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int UpgradeLevel { get; set; }
    public bool IsUpgradable { get; set; }
}

internal static class ProbeOverlayManager
{
    private const string OverlayNodeName = "Sts2McpOverlay";
    private static readonly object StateLock = new();
    private static ProbeOverlayNode? _overlay;
    private static OverlayPayload? _pendingPayload;
    private static List<OverlayRouteOption> _latestRouteOptions = new();
    private static int _selectedRouteOptionIndex = -1;
    private static int _previewRouteOptionIndex = -1;
    private static string? _previewRouteTypeKey;
    private static string? _lockedRouteTypeKey;
    private static Action<string>? _logger;
    private static int _flushQueued;
    private static DateTime _lastNoHostLogUtc = DateTime.MinValue;
    private static DateTime _lastNoOverlayLogUtc = DateTime.MinValue;
    private static string _lastHostSignature = string.Empty;
    private static string _lastRouteHighlightSignature = string.Empty;

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
            if (!string.IsNullOrEmpty(_lockedRouteTypeKey))
            {
                _selectedRouteOptionIndex = FindBestRouteOptionIndex(payload.RouteOptions, _lockedRouteTypeKey!);
            }
            payload.SelectedRouteOptionIndex = NormalizeSelectedRouteIndex(payload.RouteOptions, _selectedRouteOptionIndex);
            _selectedRouteOptionIndex = payload.SelectedRouteOptionIndex;
            _pendingPayload = payload;
        }

        QueueMainThreadFlush();
    }

    public static void SelectRouteOption(int optionIndex)
    {
        lock (StateLock)
        {
            _lockedRouteTypeKey = null;
            _previewRouteTypeKey = null;
            _previewRouteOptionIndex = -1;
            _selectedRouteOptionIndex = optionIndex;
            if (_pendingPayload != null)
            {
                _pendingPayload.SelectedRouteOptionIndex = optionIndex;
            }
        }

        QueueMainThreadFlush();
    }

    public static void PreviewRouteType(string routeTypeKey, bool enabled)
    {
        lock (StateLock)
        {
            if (enabled)
            {
                _previewRouteTypeKey = routeTypeKey;
                _previewRouteOptionIndex = FindBestRouteOptionIndex(_latestRouteOptions, routeTypeKey);
            }
            else
            {
                _previewRouteTypeKey = null;
                _previewRouteOptionIndex = -1;
            }
        }

        QueueMainThreadFlush();
    }

    public static void SelectRouteType(string routeTypeKey)
    {
        lock (StateLock)
        {
            _lockedRouteTypeKey = routeTypeKey;
            _previewRouteTypeKey = null;
            _previewRouteOptionIndex = -1;
            _selectedRouteOptionIndex = FindBestRouteOptionIndex(_latestRouteOptions, routeTypeKey);
            if (_pendingPayload != null)
            {
                _pendingPayload.SelectedRouteOptionIndex = _selectedRouteOptionIndex;
            }
        }

        LogRouteTypeSelection(routeTypeKey);
        QueueMainThreadFlush();
    }

    public static void ClearRouteSelection()
    {
        lock (StateLock)
        {
            _lockedRouteTypeKey = null;
            _previewRouteTypeKey = null;
            _previewRouteOptionIndex = -1;
            _selectedRouteOptionIndex = -1;
            if (_pendingPayload != null)
            {
                _pendingPayload.SelectedRouteOptionIndex = -1;
            }
        }

        QueueMainThreadFlush();
    }

    public static string? GetLockedRouteTypeKey()
    {
        lock (StateLock)
        {
            return _lockedRouteTypeKey;
        }
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
            if (!string.IsNullOrEmpty(_lockedRouteTypeKey))
            {
                _selectedRouteOptionIndex = FindBestRouteOptionIndex(payload.RouteOptions, _lockedRouteTypeKey!);
            }
            payload.SelectedRouteOptionIndex = NormalizeSelectedRouteIndex(payload.RouteOptions, payload.SelectedRouteOptionIndex);
            _selectedRouteOptionIndex = payload.SelectedRouteOptionIndex;
            _overlay.EnsureInitialized();
            _overlay.Visible = true;
            _overlay.Show();
            _overlay.MoveToFront();
            _latestRouteOptions = payload.RouteOptions ?? new List<OverlayRouteOption>();
            payload.SelectedRouteOptionIndex = ResolveEffectiveRouteIndex(_latestRouteOptions);
            _overlay.ApplyPayload(payload);
            TryApplyRouteHighlight(_latestRouteOptions, payload.SelectedRouteOptionIndex, ResolveEffectiveRouteTypeKey());
        }
        else
        {
            TryApplyRouteHighlight(_latestRouteOptions, ResolveEffectiveRouteIndex(_latestRouteOptions), ResolveEffectiveRouteTypeKey());
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

    private static int NormalizeSelectedRouteIndex(IReadOnlyList<OverlayRouteOption>? options, int selectedIndex)
    {
        if (options == null || options.Count == 0)
        {
            return -1;
        }

        return selectedIndex < 0 || selectedIndex >= options.Count ? -1 : selectedIndex;
    }

    private static int ResolveEffectiveRouteIndex(IReadOnlyList<OverlayRouteOption>? options)
    {
        int previewIndex = NormalizeSelectedRouteIndex(options, _previewRouteOptionIndex);
        if (previewIndex >= 0)
        {
            return previewIndex;
        }

        return NormalizeSelectedRouteIndex(options, _selectedRouteOptionIndex);
    }

    private static string? ResolveEffectiveRouteTypeKey()
    {
        if (!string.IsNullOrWhiteSpace(_previewRouteTypeKey))
        {
            return _previewRouteTypeKey;
        }

        if (!string.IsNullOrWhiteSpace(_lockedRouteTypeKey))
        {
            return _lockedRouteTypeKey;
        }

        return null;
    }

    private static int FindBestRouteOptionIndex(IReadOnlyList<OverlayRouteOption>? options, string routeTypeKey)
    {
        if (options == null || options.Count == 0 || string.IsNullOrWhiteSpace(routeTypeKey))
        {
            return -1;
        }

        string key = routeTypeKey.Trim().ToLowerInvariant();
        int bestIndex = -1;
        int bestCount = -1;
        int bestFirstMatchIndex = int.MaxValue;
        double bestScore = double.MinValue;
        foreach (OverlayRouteOption option in options)
        {
            int count = option.PathTypeKeys.Count(type =>
                string.Equals(type, key, StringComparison.OrdinalIgnoreCase));
            if (count <= 0)
            {
                continue;
            }

            int firstMatchIndex = FindFirstRouteTypeIndex(option, key);
            if (count > bestCount
                || (count == bestCount && firstMatchIndex < bestFirstMatchIndex)
                || (count == bestCount && firstMatchIndex == bestFirstMatchIndex && option.Score > bestScore))
            {
                bestCount = count;
                bestFirstMatchIndex = firstMatchIndex;
                bestScore = option.Score;
                bestIndex = option.OptionIndex;
            }
        }

        return bestIndex;
    }

    private static int FindFirstRouteTypeIndex(OverlayRouteOption option, string routeTypeKey)
    {
        for (int i = 0; i < option.PathTypeKeys.Count; i++)
        {
            if (string.Equals(option.PathTypeKeys[i], routeTypeKey, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static void TryApplyRouteHighlight(IReadOnlyList<OverlayRouteOption>? options, int selectedIndex, string? routeTypeKey)
    {
        if (options == null)
        {
            options = Array.Empty<OverlayRouteOption>();
        }

        if ((selectedIndex < 0 || selectedIndex >= options.Count) && string.IsNullOrWhiteSpace(routeTypeKey))
        {
            string clearSignature = "cleared";
            if (!string.Equals(_lastRouteHighlightSignature, clearSignature, StringComparison.Ordinal))
            {
                if (OverlayRouteHighlighter.ClearHighlight(_logger))
                {
                    _lastRouteHighlightSignature = clearSignature;
                    Log("Route highlight cleared.");
                }
                else
                {
                    _lastRouteHighlightSignature = string.Empty;
                }
            }

            return;
        }

        string signature = BuildRouteHighlightSignature(options, selectedIndex, routeTypeKey);
        if (string.Equals(signature, _lastRouteHighlightSignature, StringComparison.Ordinal))
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            OverlayRouteHighlighter.ClearHighlight(_logger);
        }

        bool pointTypeHighlighted = OverlayRouteHighlighter.TryHighlightPointType(routeTypeKey, _logger);
        bool routeHighlighted = OverlayRouteHighlighter.TryHighlight(options, selectedIndex, _logger);
        if (!pointTypeHighlighted && !routeHighlighted)
        {
            _lastRouteHighlightSignature = string.Empty;
            return;
        }

        _lastRouteHighlightSignature = signature;
        Log("Route highlight updated.");
    }

    private static string BuildRouteHighlightSignature(IReadOnlyList<OverlayRouteOption> options, int selectedIndex, string? routeTypeKey)
    {
        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            return "type:" + (routeTypeKey ?? "none");
        }

        OverlayRouteOption option = options[selectedIndex];
        string path = string.Join(
            "->",
            option.PathCoords.Select(static p => p.Row.ToString(CultureInfo.InvariantCulture) + "," + p.Col.ToString(CultureInfo.InvariantCulture)));
        return "type:" + (routeTypeKey ?? "none") + "|" + selectedIndex.ToString(CultureInfo.InvariantCulture) + "|" + path;
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

    private static void LogRouteTypeSelection(string routeTypeKey)
    {
        int selectedIndex;
        IReadOnlyList<OverlayRouteOption> options;
        lock (StateLock)
        {
            selectedIndex = _selectedRouteOptionIndex;
            options = _latestRouteOptions;
        }

        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            Log("Route type selection: type=" + routeTypeKey + " selected=<none>");
            return;
        }

        OverlayRouteOption option = options[selectedIndex];
        int count = option.PathTypeKeys.Count(type => string.Equals(type, routeTypeKey, StringComparison.OrdinalIgnoreCase));
        int firstIndex = FindFirstRouteTypeIndex(option, routeTypeKey);
        Log(
            "Route type selection: type="
            + routeTypeKey
            + " option="
            + selectedIndex.ToString(CultureInfo.InvariantCulture)
            + " matches="
            + count.ToString(CultureInfo.InvariantCulture)
            + " firstIndex="
            + (firstIndex == int.MaxValue ? "none" : firstIndex.ToString(CultureInfo.InvariantCulture))
            + " score="
            + option.Score.ToString("F2", CultureInfo.InvariantCulture));
    }
}

internal static class OverlayRouteHighlighter
{
    private static readonly FieldInfo? MapPointDictionaryField = typeof(NMapScreen)
        .GetField("_mapPointDictionary", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool TryHighlight(
        IReadOnlyList<OverlayRouteOption> options,
        int selectedIndex,
        Action<string>? logger)
    {
        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            logger?.Invoke(
                "[Overlay] Route highlight skipped: invalid selected index. selected="
                + selectedIndex.ToString(CultureInfo.InvariantCulture)
                + " options="
                + options.Count.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen == null
            || !GodotObject.IsInstanceValid(mapScreen)
            || !mapScreen.IsInsideTree()
            || !mapScreen.IsVisibleInTree())
        {
            logger?.Invoke("[Overlay] Route highlight skipped: map screen unavailable or not visible.");
            return false;
        }

        NMapDrawings? drawings = mapScreen.Drawings;
        if (drawings == null || !GodotObject.IsInstanceValid(drawings) || !drawings.IsInsideTree())
        {
            logger?.Invoke("[Overlay] Route highlight skipped: map drawings unavailable.");
            return false;
        }

        if (MapPointDictionaryField == null)
        {
            logger?.Invoke("[Overlay] Route highlight skipped: map dictionary reflection failed.");
            return false;
        }

        object? dictionaryRaw = MapPointDictionaryField.GetValue(mapScreen);
        if (dictionaryRaw is not Dictionary<MapCoord, NMapPoint> mapPointDictionary)
        {
            logger?.Invoke("[Overlay] Route highlight skipped: map dictionary cast failed.");
            return false;
        }

        OverlayRouteOption option = options[selectedIndex];
        logger?.Invoke(
            "[Overlay] Route highlight start: option="
            + selectedIndex.ToString(CultureInfo.InvariantCulture)
            + " pointType="
            + option.PointTypeLabel
            + " coords="
            + option.PathCoords.Count.ToString(CultureInfo.InvariantCulture)
            + " keys="
            + (option.PathTypeKeys?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
            + " mapPoints="
            + mapPointDictionary.Count.ToString(CultureInfo.InvariantCulture));

        if (option.PathCoords.Count < 2)
        {
            logger?.Invoke("[Overlay] Route highlight skipped: path coords < 2.");
            return false;
        }

        drawings.ClearDrawnLinesLocal();
        mapScreen.HighlightPointType(MapPointType.Unassigned);

        int drawnSegments = 0;
        int missingSegments = 0;
        for (int i = 0; i < option.PathCoords.Count - 1; i++)
        {
            OverlayRouteCoord start = option.PathCoords[i];
            OverlayRouteCoord end = option.PathCoords[i + 1];
            if (!TryGetPointPosition(mapPointDictionary, start, out Vector2 startGlobal)
                || !TryGetPointPosition(mapPointDictionary, end, out Vector2 endGlobal))
            {
                missingSegments++;
                if (missingSegments <= 4)
                {
                    logger?.Invoke(
                        "[Overlay] Route segment skipped (coord miss): "
                        + start.Row.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + start.Col.ToString(CultureInfo.InvariantCulture)
                        + " -> "
                        + end.Row.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + end.Col.ToString(CultureInfo.InvariantCulture));
                }
                continue;
            }

            Vector2 startLocal = drawings.GetGlobalTransform().Inverse() * startGlobal;
            Vector2 endLocal = drawings.GetGlobalTransform().Inverse() * endGlobal;
            drawings.BeginLineLocal(startLocal, DrawingMode.Drawing);
            drawings.UpdateCurrentLinePositionLocal(endLocal);
            drawings.StopLineLocal();
            drawnSegments++;
        }

        if (drawnSegments <= 0)
        {
            logger?.Invoke(
                "[Overlay] Route highlight produced no drawable segments. points="
                + option.PathCoords.Count.ToString(CultureInfo.InvariantCulture)
                + " missingSegments="
                + missingSegments.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        logger?.Invoke(
            "[Overlay] Route highlight drawn segments="
            + drawnSegments.ToString(CultureInfo.InvariantCulture)
            + " missingSegments="
            + missingSegments.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    public static bool ClearHighlight(Action<string>? logger)
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen == null
            || !GodotObject.IsInstanceValid(mapScreen)
            || !mapScreen.IsInsideTree()
            || !mapScreen.IsVisibleInTree())
        {
            return false;
        }

        try
        {
            NMapDrawings? drawings = mapScreen.Drawings;
            if (drawings != null && GodotObject.IsInstanceValid(drawings) && drawings.IsInsideTree())
            {
                drawings.ClearDrawnLinesLocal();
            }

            mapScreen.HighlightPointType(MapPointType.Unassigned);
            logger?.Invoke("[Overlay] Route highlight cleared on map.");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Invoke("[Overlay] Route clear failed: " + ex.Message);
            return false;
        }
    }

    public static bool TryHighlightPointType(string? routeTypeKey, Action<string>? logger)
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen == null
            || !GodotObject.IsInstanceValid(mapScreen)
            || !mapScreen.IsInsideTree()
            || !mapScreen.IsVisibleInTree())
        {
            return false;
        }

        MapPointType pointType = MapPointType.Unassigned;
        if (!string.IsNullOrWhiteSpace(routeTypeKey))
        {
            pointType = RouteTypeKeyToMapPointType(routeTypeKey!);
        }

        try
        {
            mapScreen.HighlightPointType(pointType);
            logger?.Invoke("[Overlay] Map point type highlight: " + pointType);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Invoke("[Overlay] Map point type highlight failed: " + ex.Message);
            return false;
        }
    }

    private static MapPointType RouteTypeKeyToMapPointType(string routeTypeKey)
    {
        return routeTypeKey.Trim().ToLowerInvariant() switch
        {
            "monster" => MapPointType.Monster,
            "elite" => MapPointType.Elite,
            "question" => MapPointType.Unknown,
            "shop" => MapPointType.Shop,
            "treasure" => MapPointType.Treasure,
            "rest" => MapPointType.RestSite,
            "boss" => MapPointType.Boss,
            "ancient" => MapPointType.Ancient,
            _ => MapPointType.Unassigned
        };
    }

    private static bool TryGetPointPosition(
        IReadOnlyDictionary<MapCoord, NMapPoint> mapPointDictionary,
        OverlayRouteCoord coord,
        out Vector2 position)
    {
        MapCoord mapCoord = new(coord.Col, coord.Row);
        if (!mapPointDictionary.TryGetValue(mapCoord, out NMapPoint? mapPoint))
        {
            MapCoord swapped = new(coord.Row, coord.Col);
            if (!mapPointDictionary.TryGetValue(swapped, out mapPoint))
            {
                position = Vector2.Zero;
                return false;
            }
        }

        if (mapPoint == null)
        {
            position = Vector2.Zero;
            return false;
        }

        if (mapPoint is NNormalMapPoint)
        {
            position = mapPoint.GlobalPosition;
            return true;
        }

        position = mapPoint.GlobalPosition + mapPoint.Size * 0.5f;
        return true;
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
    private const float MinPanelWidth = 320f;
    private const float MinPanelHeight = 220f;
    private const float MaxPanelWidth = 760f;
    private const float MaxPanelHeight = 720f;

    private enum PanelType
    {
        Combat,
        Total,
        Route,
        Logs,
        Modifier
    }

    private enum ResizeCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
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
    private MarginContainer _routeOptionContainer = null!;
    private MarginContainer _modifierContainer = null!;
    private HFlowContainer _routeLegendRows = null!;
    private VBoxContainer _routeOptionRows = null!;
    private Label _modifierStatsLabel = null!;
    private SpinBox _modifierHpSpin = null!;
    private SpinBox _modifierGoldSpin = null!;
    private LineEdit _modifierRelicSearch = null!;
    private ItemList _modifierRelicList = null!;
    private LineEdit _modifierCardSearch = null!;
    private ItemList _modifierCardList = null!;
    private ItemList _modifierDeckList = null!;
    private MarginContainer _contentContainer = null!;
    private Control _resizeLayer = null!;
    private PanelContainer _dragBar = null!;
    private Button _collapseButton = null!;
    private Button _combatViewToggleButton = null!;
    private readonly Dictionary<ResizeCorner, PanelContainer> _resizeHandles = new();
    private Label _opacityLabel = null!;
    private StyleBoxFlat _panelStyle = null!;
    private StyleBoxFlat _dragBarStyle = null!;
    private bool _collapsed;
    private bool _uiReady;
    private bool _dragging;
    private bool _resizing;
    private bool _showCombatBoard = true;
    private float _opacity = DefaultOpacity;
    private string _modifierRelicFilter = string.Empty;
    private string _modifierCardFilter = string.Empty;
    private Vector2 _dragOffset;
    private Vector2 _resizeStartMouseGlobal;
    private Vector2 _resizeStartSize;
    private Vector2 _resizeStartPosition;
    private ResizeCorner _activeResizeCorner = ResizeCorner.BottomRight;
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
        TopLevel = true;
        ZIndex = 2200;
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = new Vector2(18, 108);
        CustomMinimumSize = new Vector2(MinPanelWidth, MinPanelHeight);
        Size = new Vector2(360, Math.Max(ExpandedHeight, MinPanelHeight));
        Visible = true;
        Show();

        _panelStyle = CreatePanelStyle(_opacity);
        AddThemeStyleboxOverride("panel", _panelStyle);
        BuildUi();
        _uiReady = true;
        RefreshVisualState();
        UpdateResizeHandlePlacement();
        MoveToFront();
        LogDebug($"EnsureInitialized visible={Visible} pos={Position} size={Size} parent={GetParent()?.Name}");
    }

    public override void _Process(double delta)
    {
        if (_dragging && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _dragging = false;
        }

        if (_resizing)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left))
            {
                _resizing = false;
            }
            else
            {
                Vector2 deltaSize = GetGlobalMousePosition() - _resizeStartMouseGlobal;
                ApplyResizeDelta(deltaSize, _activeResizeCorner);
            }
        }

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
                Size = new Vector2(360, Math.Max(ExpandedHeight, MinPanelHeight));
                RefreshVisualState();
                GetViewport()?.SetInputAsHandled();
                LogDebug("F7 reset overlay position/visibility.");
            }
        }
    }

    public void ApplyPayload(OverlayPayload payload)
    {
        _payload = payload;
        if (_payload.RouteOptions == null)
        {
            _payload.RouteOptions = new List<OverlayRouteOption>();
        }
        _payload.SelectedRouteOptionIndex = _payload.RouteOptions.Count == 0
            ? -1
            : (_payload.SelectedRouteOptionIndex < 0 || _payload.SelectedRouteOptionIndex >= _payload.RouteOptions.Count
                ? -1
                : _payload.SelectedRouteOptionIndex);
        _panelContent[PanelType.Combat] = payload.CombatPanel;
        _panelContent[PanelType.Total] = payload.TotalPanel;
        _panelContent[PanelType.Route] = payload.RoutePanel;
        _panelContent[PanelType.Logs] = payload.LogsPanel;
        _panelContent[PanelType.Modifier] = "修改器";
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
            Text = "STS2 贡献面板",
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
        AddTabButton(tabs, PanelType.Modifier, "修改器");

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

        _routeOptionContainer = new MarginContainer();
        _routeOptionContainer.AddThemeConstantOverride("margin_left", 2);
        _routeOptionContainer.AddThemeConstantOverride("margin_top", 2);
        _routeOptionContainer.AddThemeConstantOverride("margin_right", 2);
        _routeOptionContainer.AddThemeConstantOverride("margin_bottom", 2);
        body.AddChild(_routeOptionContainer);

        VBoxContainer routeRoot = new();
        routeRoot.AddThemeConstantOverride("separation", 4);
        _routeOptionContainer.AddChild(routeRoot);

        Label routeHint = new()
        {
            Text = "点击路线可在地图上高亮整条路径",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        routeHint.AddThemeColorOverride("font_color", new Color(0.72f, 0.84f, 0.95f));
        routeRoot.AddChild(routeHint);

        Label routeLegendHint = new()
        {
            Text = "图例：点击锁定该类型最佳路线",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        routeLegendHint.AddThemeColorOverride("font_color", new Color(0.80f, 0.89f, 0.97f));
        routeRoot.AddChild(routeLegendHint);

        _routeLegendRows = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _routeLegendRows.AddThemeConstantOverride("h_separation", 4);
        _routeLegendRows.AddThemeConstantOverride("v_separation", 4);
        routeRoot.AddChild(_routeLegendRows);

        _routeOptionRows = new VBoxContainer();
        _routeOptionRows.AddThemeConstantOverride("separation", 3);
        routeRoot.AddChild(_routeOptionRows);

        _modifierContainer = new MarginContainer();
        _modifierContainer.AddThemeConstantOverride("margin_left", 2);
        _modifierContainer.AddThemeConstantOverride("margin_top", 2);
        _modifierContainer.AddThemeConstantOverride("margin_right", 2);
        _modifierContainer.AddThemeConstantOverride("margin_bottom", 2);
        body.AddChild(_modifierContainer);

        VBoxContainer modifierRoot = new();
        modifierRoot.AddThemeConstantOverride("separation", 6);
        _modifierContainer.AddChild(modifierRoot);

        _modifierStatsLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "等待玩家数据..."
        };
        _modifierStatsLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 1f));
        modifierRoot.AddChild(_modifierStatsLabel);

        modifierRoot.AddChild(CreateModifierStatEditor());
        modifierRoot.AddChild(CreateModifierRelicEditor());
        modifierRoot.AddChild(CreateModifierCardEditor());
        modifierRoot.AddChild(CreateModifierUpgradeEditor());

        _contentLabel = new RichTextLabel
        {
            FitContent = false,
            ScrollActive = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
            SelectionEnabled = false,
            BbcodeEnabled = false
        };
        _contentLabel.AddThemeColorOverride("default_color", new Color(0.88f, 0.94f, 1f));
        body.AddChild(_contentLabel);

        Label tips = new()
        {
            Text = "拖拽标题栏移动 | 四角缩放 | A-/A+ 调透明 | F8 显示/隐藏 | F7 重置",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        tips.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.84f));
        body.AddChild(tips);

        AddResizeHandle(ResizeCorner.TopLeft, "拖拽左上角调整大小");
        AddResizeHandle(ResizeCorner.TopRight, "拖拽右上角调整大小");
        AddResizeHandle(ResizeCorner.BottomLeft, "拖拽左下角调整大小");
        AddResizeHandle(ResizeCorner.BottomRight, "拖拽右下角调整大小");
        UpdateResizeHandlePlacement();

        ApplyOpacity();
        UpdateOpacityLabel();
    }

    private void AddResizeHandle(ResizeCorner corner, string tooltipText)
    {
        if (_resizeLayer == null || !GodotObject.IsInstanceValid(_resizeLayer))
        {
            _resizeLayer = new Control
            {
                Name = "ResizeLayer",
                MouseFilter = MouseFilterEnum.Ignore,
                FocusMode = FocusModeEnum.None
            };
            AddChild(_resizeLayer);
            _resizeLayer.SetAnchorsPreset(LayoutPreset.FullRect);
            _resizeLayer.OffsetLeft = 0;
            _resizeLayer.OffsetTop = 0;
            _resizeLayer.OffsetRight = 0;
            _resizeLayer.OffsetBottom = 0;
            _resizeLayer.MoveToFront();
        }

        PanelContainer handle = new()
        {
            CustomMinimumSize = new Vector2(14, 14),
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = tooltipText
        };
        handle.AddThemeStyleboxOverride("panel", CreateResizeHandleStyle(corner));
        ResizeCorner capturedCorner = corner;
        handle.GuiInput += @event => OnResizeHandleInput(capturedCorner, @event);
        _resizeLayer.AddChild(handle);
        handle.SetAnchorsPreset(LayoutPreset.TopLeft);
        handle.Size = new Vector2(14, 14);
        _resizeHandles[corner] = handle;
    }

    private static StyleBoxFlat CreateResizeHandleStyle(ResizeCorner corner)
    {
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.36f, 0.52f, 0.69f, 0.62f),
            CornerRadiusTopLeft = corner == ResizeCorner.TopLeft ? 6 : 2,
            CornerRadiusTopRight = corner == ResizeCorner.TopRight ? 6 : 2,
            CornerRadiusBottomLeft = corner == ResizeCorner.BottomLeft ? 6 : 2,
            CornerRadiusBottomRight = corner == ResizeCorner.BottomRight ? 6 : 2
        };
        return style;
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

        _statusLabel.Text = $"{(_payload.IsInCombat ? "战斗中" : "观察")} / {ModeLabel(_payload.Mode)}";
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
        bool routePanelActive = _activePanel == PanelType.Route;
        bool modifierPanelActive = _activePanel == PanelType.Modifier;
        _combatViewToggleButton.Visible = _activePanel == PanelType.Combat;
        _combatViewToggleButton.Text = combatBoardActive ? "详情" : "榜单";
        _combatViewToggleButton.TooltipText = combatBoardActive ? "切换到战斗详情文本" : "切换到伤害排行";

        _summaryLabel.Visible = !combatBoardActive && !routePanelActive && !modifierPanelActive;
        _alertsLabel.Visible = !combatBoardActive && !routePanelActive && !modifierPanelActive;
        _combatBoardContainer.Visible = combatBoardActive;
        _routeOptionContainer.Visible = routePanelActive;
        _modifierContainer.Visible = modifierPanelActive;
        _contentLabel.Visible = !combatBoardActive && !modifierPanelActive;
        if (combatBoardActive)
        {
            RefreshCombatBoardRows();
        }
        else if (routePanelActive)
        {
            RefreshRouteOptionRows();
        }
        else if (modifierPanelActive)
        {
            RefreshModifierRows();
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
        CustomMinimumSize = _collapsed
            ? new Vector2(MinPanelWidth, CollapsedHeight)
            : new Vector2(MinPanelWidth, MinPanelHeight);
        if (_collapsed)
        {
            Size = new Vector2(Mathf.Clamp(Size.X, MinPanelWidth, MaxPanelWidth), CollapsedHeight);
        }
        else
        {
            Size = new Vector2(
                Mathf.Clamp(Size.X, MinPanelWidth, MaxPanelWidth),
                Mathf.Clamp(Size.Y, MinPanelHeight, MaxPanelHeight));
        }
        UpdateResizeHandlePlacement();

        TooltipText = "更新时间: " + _payload.TimestampUtc;
    }

    private void RefreshCombatBoardRows()
    {
        _combatBoardTitleLabel.Text = "综合贡献";
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

    private void RefreshRouteOptionRows()
    {
        foreach (Node child in _routeLegendRows.GetChildren())
        {
            child.QueueFree();
        }
        foreach (Node child in _routeOptionRows.GetChildren())
        {
            child.QueueFree();
        }

        List<OverlayRouteOption> options = _payload.RouteOptions ?? new List<OverlayRouteOption>();
        Button clearButton = new()
        {
            Text = "清除高亮",
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(88, 22),
            TooltipText = "清除当前地图高亮路线"
        };
        clearButton.AddThemeColorOverride("font_color", new Color(0.93f, 0.82f, 0.74f));
        clearButton.Pressed += ProbeOverlayManager.ClearRouteSelection;
        _routeLegendRows.AddChild(clearButton);

        foreach (OverlayRouteLegendEntry legendEntry in BuildRouteLegendTypes(_payload.RouteLegendEntries, options))
        {
            Button typeButton = new()
            {
                Text = legendEntry.Label,
                Flat = true,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(64, 22),
                TooltipText = "点击锁定该类型的最佳路线，再点一次清除"
            };
            string? lockedRouteTypeKey = ProbeOverlayManager.GetLockedRouteTypeKey();
            bool typeLocked = string.Equals(lockedRouteTypeKey, legendEntry.TypeKey, StringComparison.OrdinalIgnoreCase);
            typeButton.AddThemeColorOverride("font_color", typeLocked
                ? new Color(0.98f, 0.88f, 0.66f)
                : new Color(0.88f, 0.95f, 1f));
            string capturedTypeKey = legendEntry.TypeKey;
            typeButton.Pressed += () =>
            {
                string? activeLockedRouteTypeKey = ProbeOverlayManager.GetLockedRouteTypeKey();
                if (string.Equals(activeLockedRouteTypeKey, capturedTypeKey, StringComparison.OrdinalIgnoreCase))
                {
                    ProbeOverlayManager.ClearRouteSelection();
                    return;
                }

                ProbeOverlayManager.SelectRouteType(capturedTypeKey);
            };
            _routeLegendRows.AddChild(typeButton);
        }

        if (options.Count == 0)
        {
            _routeOptionRows.AddChild(CreatePlaceholderRow("当前没有可点击路线"));
            return;
        }

        foreach (OverlayRouteOption option in options.Take(6))
        {
            Button button = new()
            {
                Text = $"[{option.OptionIndex + 1}] {option.PointTypeLabel} | 分 {option.Score:F2} | {option.RiskTag}",
                Flat = true,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Alignment = HorizontalAlignment.Left
            };

            bool selected = option.OptionIndex == _payload.SelectedRouteOptionIndex;
            button.Modulate = selected
                ? new Color(0.96f, 0.88f, 0.66f)
                : new Color(0.74f, 0.86f, 0.97f);
            button.TooltipText = "点击后在地图上高亮路线";
            int capturedIndex = option.OptionIndex;
            button.Pressed += () =>
            {
                if (_payload.SelectedRouteOptionIndex == capturedIndex)
                {
                    ProbeOverlayManager.ClearRouteSelection();
                    return;
                }

                ProbeOverlayManager.SelectRouteOption(capturedIndex);
            };
            _routeOptionRows.AddChild(button);
        }
    }

    private Control CreateModifierStatEditor()
    {
        PanelContainer panel = CreateModifierSection("资源");
        VBoxContainer root = (VBoxContainer)panel.GetChild(0);

        HBoxContainer hpRow = new();
        hpRow.AddThemeConstantOverride("separation", 6);
        root.AddChild(hpRow);

        Label hpLabel = new() { Text = "生命" };
        hpLabel.CustomMinimumSize = new Vector2(48, 0);
        hpRow.AddChild(hpLabel);

        _modifierHpSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 9999,
            Step = 1,
            Rounded = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        hpRow.AddChild(_modifierHpSpin);

        Button hpApply = new()
        {
            Text = "设置生命",
            FocusMode = FocusModeEnum.None
        };
        hpApply.Pressed += () => ProbeModEntry.SetCurrentLocalPlayerHp((int)_modifierHpSpin.Value);
        hpRow.AddChild(hpApply);

        HBoxContainer goldRow = new();
        goldRow.AddThemeConstantOverride("separation", 6);
        root.AddChild(goldRow);

        Label goldLabel = new() { Text = "金币" };
        goldLabel.CustomMinimumSize = new Vector2(48, 0);
        goldRow.AddChild(goldLabel);

        _modifierGoldSpin = new SpinBox
        {
            MinValue = 0,
            MaxValue = 999999,
            Step = 1,
            Rounded = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        goldRow.AddChild(_modifierGoldSpin);

        Button goldApply = new()
        {
            Text = "设置金币",
            FocusMode = FocusModeEnum.None
        };
        goldApply.Pressed += () => ProbeModEntry.SetCurrentLocalPlayerGold((int)_modifierGoldSpin.Value);
        goldRow.AddChild(goldApply);

        return panel;
    }

    private Control CreateModifierRelicEditor()
    {
        PanelContainer panel = CreateModifierSection("添加遗物");
        VBoxContainer root = (VBoxContainer)panel.GetChild(0);

        _modifierRelicSearch = new LineEdit
        {
            PlaceholderText = "搜索遗物 ID 或名称"
        };
        _modifierRelicSearch.TextChanged += text =>
        {
            _modifierRelicFilter = text ?? string.Empty;
            RefreshModifierRelicList();
        };
        root.AddChild(_modifierRelicSearch);

        _modifierRelicList = new ItemList
        {
            SelectMode = ItemList.SelectModeEnum.Single,
            AutoHeight = true,
            FixedColumnWidth = 0,
            CustomMinimumSize = new Vector2(0, 120)
        };
        root.AddChild(_modifierRelicList);

        Button addRelicButton = new()
        {
            Text = "添加所选遗物",
            FocusMode = FocusModeEnum.None
        };
        addRelicButton.Pressed += () =>
        {
            string? relicId = GetSelectedMetadataString(_modifierRelicList);
            if (!string.IsNullOrWhiteSpace(relicId))
            {
                ProbeModEntry.AddRelicToCurrentRun(relicId);
            }
        };
        root.AddChild(addRelicButton);

        return panel;
    }

    private Control CreateModifierCardEditor()
    {
        PanelContainer panel = CreateModifierSection("添加卡牌");
        VBoxContainer root = (VBoxContainer)panel.GetChild(0);

        _modifierCardSearch = new LineEdit
        {
            PlaceholderText = "搜索卡牌 ID 或名称"
        };
        _modifierCardSearch.TextChanged += text =>
        {
            _modifierCardFilter = text ?? string.Empty;
            RefreshModifierCardCatalog();
        };
        root.AddChild(_modifierCardSearch);

        _modifierCardList = new ItemList
        {
            SelectMode = ItemList.SelectModeEnum.Single,
            AutoHeight = true,
            FixedColumnWidth = 0,
            CustomMinimumSize = new Vector2(0, 120)
        };
        root.AddChild(_modifierCardList);

        Button addCardButton = new()
        {
            Text = "加入所选卡牌",
            FocusMode = FocusModeEnum.None
        };
        addCardButton.Pressed += () =>
        {
            string? cardId = GetSelectedMetadataString(_modifierCardList);
            if (!string.IsNullOrWhiteSpace(cardId))
            {
                ProbeModEntry.AddCardToCurrentDeck(cardId);
            }
        };
        root.AddChild(addCardButton);

        return panel;
    }

    private Control CreateModifierUpgradeEditor()
    {
        PanelContainer panel = CreateModifierSection("升级牌库卡牌");
        VBoxContainer root = (VBoxContainer)panel.GetChild(0);

        _modifierDeckList = new ItemList
        {
            SelectMode = ItemList.SelectModeEnum.Single,
            AutoHeight = true,
            FixedColumnWidth = 0,
            CustomMinimumSize = new Vector2(0, 140)
        };
        root.AddChild(_modifierDeckList);

        Button upgradeButton = new()
        {
            Text = "升级所选卡牌",
            FocusMode = FocusModeEnum.None
        };
        upgradeButton.Pressed += () =>
        {
            string? deckIndexRaw = GetSelectedMetadataString(_modifierDeckList);
            if (int.TryParse(deckIndexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int deckIndex))
            {
                ProbeModEntry.UpgradeCurrentDeckCard(deckIndex);
            }
        };
        root.AddChild(upgradeButton);

        return panel;
    }

    private PanelContainer CreateModifierSection(string title)
    {
        PanelContainer panel = new();
        panel.AddThemeStyleboxOverride("panel", CreateModifierSectionStyle());

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 4);
        panel.AddChild(root);

        Label titleLabel = new()
        {
            Text = title
        };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.87f, 0.63f));
        root.AddChild(titleLabel);
        return panel;
    }

    private static StyleBoxFlat CreateModifierSectionStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.16f, 0.21f, 0.62f),
            BorderColor = new Color(0.32f, 0.56f, 0.72f, 0.72f),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginBottom = 6,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6
        };
    }

    private void RefreshModifierRows()
    {
        OverlayModifierState state = _payload.ModifierState ?? new OverlayModifierState();
        _modifierStatsLabel.Text = string.IsNullOrWhiteSpace(state.PlayerName)
            ? "当前未识别到本地玩家。"
            : $"玩家 {state.PlayerName} | 角色 {state.CharacterId} | 生命 {state.CurrentHp}/{state.MaxHp} | 金币 {state.Gold} | 遗物 {state.RelicCount} | 牌库 {state.DeckCount}";

        _modifierHpSpin.MaxValue = Math.Max(1, state.MaxHp);
        _modifierHpSpin.Value = Math.Clamp(state.CurrentHp, 0, (int)_modifierHpSpin.MaxValue);
        _modifierGoldSpin.Value = Math.Max(0, state.Gold);

        RefreshModifierRelicList();
        RefreshModifierCardCatalog();
        RefreshModifierDeckList(state);
    }

    private void RefreshModifierRelicList()
    {
        _modifierRelicList.Clear();
        IEnumerable<OverlayModifierCatalogEntry> rows = ProbeModEntry.GetModifierRelicCatalog();
        string filter = (_modifierRelicFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(entry =>
                entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (OverlayModifierCatalogEntry entry in rows.Take(40))
        {
            int index = _modifierRelicList.AddItem($"{entry.Title} [{entry.Id}]");
            _modifierRelicList.SetItemTooltip(index, entry.Subtitle);
            _modifierRelicList.SetItemMetadata(index, entry.Id);
        }
    }

    private void RefreshModifierCardCatalog()
    {
        _modifierCardList.Clear();
        IEnumerable<OverlayModifierCatalogEntry> rows = ProbeModEntry.GetModifierCardCatalog();
        string filter = (_modifierCardFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(entry =>
                entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (OverlayModifierCatalogEntry entry in rows.Take(40))
        {
            int index = _modifierCardList.AddItem($"{entry.Title} [{entry.Id}]");
            _modifierCardList.SetItemTooltip(index, entry.Subtitle);
            _modifierCardList.SetItemMetadata(index, entry.Id);
        }
    }

    private void RefreshModifierDeckList(OverlayModifierState state)
    {
        _modifierDeckList.Clear();
        foreach (OverlayModifierDeckCardEntry card in state.DeckCards)
        {
            string suffix = card.IsUpgradable ? $"可升级 +{card.UpgradeLevel}" : $"已满级 +{card.UpgradeLevel}";
            int index = _modifierDeckList.AddItem($"#{card.DeckIndex} {card.Title} [{card.CardId}] | {suffix}");
            _modifierDeckList.SetItemMetadata(index, card.DeckIndex.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string? GetSelectedMetadataString(ItemList list)
    {
        int[] selected = list.GetSelectedItems();
        if (selected.Length == 0)
        {
            return null;
        }

        Variant metadata = list.GetItemMetadata(selected[0]);
        return metadata.VariantType == Variant.Type.Nil ? null : metadata.AsString();
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

    private static List<OverlayRouteLegendEntry> BuildRouteLegendTypes(
        IReadOnlyList<OverlayRouteLegendEntry>? legendEntries,
        IReadOnlyList<OverlayRouteOption> options)
    {
        if (legendEntries != null && legendEntries.Count > 0)
        {
            return legendEntries
                .Select(static entry => new OverlayRouteLegendEntry
                {
                    TypeKey = entry.TypeKey,
                    Count = entry.Count,
                    Label = string.IsNullOrWhiteSpace(entry.Label)
                        ? $"{RouteTypeKeyToLabel(entry.TypeKey)} {entry.Count}"
                        : entry.Label
                })
                .ToList();
        }

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayRouteOption option in options)
        {
            IEnumerable<string> keys = option.PathTypeKeys ?? new List<string>();
            if (!keys.Any())
            {
                string fallbackKey = LabelToRouteTypeKey(option.PointTypeLabel);
                if (!string.IsNullOrWhiteSpace(fallbackKey))
                {
                    keys = new[] { fallbackKey };
                }
            }

            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                counts.TryGetValue(key, out int old);
                counts[key] = old + 1;
            }
        }

        string[] preferredOrder = new[]
        {
            "monster",
            "elite",
            "question",
            "shop",
            "rest",
            "treasure",
            "ancient",
            "boss"
        };

        List<OverlayRouteLegendEntry> rows = new();
        foreach (string typeKey in preferredOrder)
        {
            counts.TryGetValue(typeKey, out int count);
            rows.Add(new OverlayRouteLegendEntry
            {
                TypeKey = typeKey,
                Count = count,
                Label = $"{RouteTypeKeyToLabel(typeKey)} {count}"
            });
        }

        foreach (string key in counts.Keys.OrderBy(static x => x, StringComparer.Ordinal))
        {
            if (preferredOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            int count = counts[key];
            rows.Add(new OverlayRouteLegendEntry
            {
                TypeKey = key,
                Count = count,
                Label = $"{RouteTypeKeyToLabel(key)} {count}"
            });
        }

        return rows;
    }

    private static string LabelToRouteTypeKey(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        string text = label.Trim().ToLowerInvariant();
        return text switch
        {
            "小怪" => "monster",
            "精英" => "elite",
            "问号" => "question",
            "商店" => "shop",
            "休息" => "rest",
            "宝箱" => "treasure",
            "首领" => "boss",
            "古遗迹" => "ancient",
            "未知" => "unknown",
            "未定" => "unassigned",
            _ => text
        };
    }

    private static PanelContainer CreateDamageRow(int rank, OverlayDamageEntry row)
    {
        PanelContainer panel = new();
        panel.AddThemeStyleboxOverride("panel", CreateDamageRowStyle(row.IsLocalPlayer));

        VBoxContainer vbox = new();
        panel.AddChild(vbox);

        HBoxContainer hbox = new();
        vbox.AddChild(hbox);

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
            Text = $"{row.ContributionScore:F0}分 ({row.Ratio * 100:F1}%)",
            HorizontalAlignment = HorizontalAlignment.Right,
            CustomMinimumSize = new Vector2(108, 0)
        };
        value.AddThemeColorOverride("font_color", new Color(0.98f, 0.90f, 0.70f));
        hbox.AddChild(value);

        Label breakdown = new()
        {
            Text = $"直:{row.Ndps:F0} 辅:{row.SupportDamage:F0} 减:{row.Mitigation:F0} 盾:{row.TeamBlock:F0} 态:{row.StatusScore:F0}",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        breakdown.AddThemeColorOverride("font_color", new Color(0.82f, 0.91f, 1f));
        vbox.AddChild(breakdown);

        ProgressBar bar = new()
        {
            MinValue = 0,
            MaxValue = 100,
            Value = Mathf.Clamp((float)(row.Ratio * 100), 0f, 100f),
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 8)
        };
        StyleBoxFlat backgroundStyle = new()
        {
            BgColor = new Color(0.14f, 0.20f, 0.30f, 0.8f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        StyleBoxFlat fillStyle = new()
        {
            BgColor = row.IsLocalPlayer
                ? new Color(0.87f, 0.54f, 0.89f, 0.95f)
                : new Color(0.51f, 0.78f, 0.96f, 0.95f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        bar.AddThemeStyleboxOverride("background", backgroundStyle);
        bar.AddThemeStyleboxOverride("fill", fillStyle);
        vbox.AddChild(bar);

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
            PanelType.Modifier => "修改器",
            _ => panelType.ToString()
        };
    }

    private static string ModeLabel(string mode)
    {
        return mode switch
        {
            "singleplayer" => "单机",
            "multiplayer" => "联机",
            _ => mode
        };
    }

    private static string RouteTypeKeyToLabel(string routeTypeKey)
    {
        return routeTypeKey.Trim().ToLowerInvariant() switch
        {
            "monster" => "小怪",
            "elite" => "精英",
            "question" => "问号",
            "shop" => "商店",
            "rest" => "休息",
            "treasure" => "宝箱",
            "boss" => "首领",
            "ancient" => "古遗迹",
            _ => routeTypeKey
        };
    }

    private void OnDragBarInput(InputEvent @event)
    {
        if (_resizing)
        {
            return;
        }

        if (@event is InputEventMouseButton mouseButton &&
            mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                MoveToFront();
                _dragging = true;
                Vector2 mousePos = GetViewport()?.GetMousePosition() ?? mouseButton.GlobalPosition;
                _dragOffset = mousePos - Position;
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
            Vector2 mousePos = GetViewport()?.GetMousePosition() ?? mouseMotion.GlobalPosition;
            Vector2 target = mousePos - _dragOffset;
            Rect2 viewportRect = GetViewportRect();
            float maxX = Mathf.Max(0, viewportRect.Size.X - Size.X - 4);
            float maxY = Mathf.Max(0, viewportRect.Size.Y - Size.Y - 4);
            target.X = Mathf.Clamp(target.X, 4, maxX);
            target.Y = Mathf.Clamp(target.Y, 4, maxY);
            Position = target;
            AcceptEvent();
        }
    }

    private void OnResizeHandleInput(ResizeCorner corner, InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton &&
            mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (mouseButton.Pressed)
            {
                MoveToFront();
                _dragging = false;
                _resizing = true;
                _activeResizeCorner = corner;
                _resizeStartMouseGlobal = mouseButton.GlobalPosition;
                _resizeStartSize = Size;
                _resizeStartPosition = Position;
                AcceptEvent();
            }
            else
            {
                _resizing = false;
                AcceptEvent();
            }

            return;
        }

        if (@event is InputEventMouseMotion mouseMotion && _resizing)
        {
            Vector2 delta = mouseMotion.GlobalPosition - _resizeStartMouseGlobal;
            ApplyResizeDelta(delta, _activeResizeCorner);
            AcceptEvent();
        }
    }

    private void ApplyResizeDelta(Vector2 delta, ResizeCorner corner)
    {
        float oldWidth = _resizeStartSize.X;
        float oldHeight = _resizeStartSize.Y;
        float newX = _resizeStartPosition.X;
        float newY = _resizeStartPosition.Y;

        float newWidth = oldWidth;
        float minHeight = _collapsed ? CollapsedHeight : MinPanelHeight;
        float newHeight = _collapsed ? CollapsedHeight : oldHeight;

        switch (corner)
        {
            case ResizeCorner.TopLeft:
            {
                newWidth = Mathf.Clamp(oldWidth - delta.X, MinPanelWidth, MaxPanelWidth);
                newX = _resizeStartPosition.X + (oldWidth - newWidth);
                if (!_collapsed)
                {
                    newHeight = Mathf.Clamp(oldHeight - delta.Y, minHeight, MaxPanelHeight);
                    newY = _resizeStartPosition.Y + (oldHeight - newHeight);
                }
                break;
            }
            case ResizeCorner.TopRight:
            {
                newWidth = Mathf.Clamp(oldWidth + delta.X, MinPanelWidth, MaxPanelWidth);
                if (!_collapsed)
                {
                    newHeight = Mathf.Clamp(oldHeight - delta.Y, minHeight, MaxPanelHeight);
                    newY = _resizeStartPosition.Y + (oldHeight - newHeight);
                }
                break;
            }
            case ResizeCorner.BottomLeft:
            {
                newWidth = Mathf.Clamp(oldWidth - delta.X, MinPanelWidth, MaxPanelWidth);
                newX = _resizeStartPosition.X + (oldWidth - newWidth);
                if (!_collapsed)
                {
                    newHeight = Mathf.Clamp(oldHeight + delta.Y, minHeight, MaxPanelHeight);
                }
                break;
            }
            case ResizeCorner.BottomRight:
            default:
            {
                newWidth = Mathf.Clamp(oldWidth + delta.X, MinPanelWidth, MaxPanelWidth);
                if (!_collapsed)
                {
                    newHeight = Mathf.Clamp(oldHeight + delta.Y, minHeight, MaxPanelHeight);
                }
                break;
            }
        }

        if (_collapsed)
        {
            newHeight = CollapsedHeight;
            newY = _resizeStartPosition.Y;
        }

        Rect2 viewportRect = GetViewportRect();
        float maxX = Mathf.Max(0, viewportRect.Size.X - newWidth - 4);
        float maxY = Mathf.Max(0, viewportRect.Size.Y - newHeight - 4);
        newX = Mathf.Clamp(newX, 4, maxX);
        newY = Mathf.Clamp(newY, 4, maxY);

        Position = new Vector2(newX, newY);
        Size = new Vector2(newWidth, newHeight);
        UpdateResizeHandlePlacement();
    }

    private void UpdateResizeHandlePlacement()
    {
        if (_resizeHandles.Count == 0)
        {
            return;
        }

        if (_resizeLayer != null && GodotObject.IsInstanceValid(_resizeLayer))
        {
            _resizeLayer.SetAnchorsPreset(LayoutPreset.FullRect);
            _resizeLayer.OffsetLeft = 0;
            _resizeLayer.OffsetTop = 0;
            _resizeLayer.OffsetRight = 0;
            _resizeLayer.OffsetBottom = 0;
            _resizeLayer.MoveToFront();
        }

        foreach ((ResizeCorner corner, PanelContainer handle) in _resizeHandles)
        {
            if (handle == null || !GodotObject.IsInstanceValid(handle))
            {
                continue;
            }

            float x = corner is ResizeCorner.TopRight or ResizeCorner.BottomRight
                ? Mathf.Max(0, Size.X - handle.Size.X - 1)
                : 1;
            float y = corner is ResizeCorner.BottomLeft or ResizeCorner.BottomRight
                ? Mathf.Max(0, Size.Y - handle.Size.Y - 1)
                : 1;
            handle.Position = new Vector2(x, y);
            handle.Visible = !_collapsed;
        }
    }

    private void LogDebug(string message)
    {
        DebugLogger?.Invoke("[OverlayNode] " + message);
    }
}
