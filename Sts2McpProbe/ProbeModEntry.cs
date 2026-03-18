using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodotCallable = Godot.Callable;
using GodotSceneTree = Godot.SceneTree;
using GodotSceneTreeTimer = Godot.SceneTreeTimer;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Mcp;

[ModInitializer("Initialize")]
public static class ProbeModEntry
{
    private const string ModId = "Sts2McpProbe";
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly TimeSpan DumpInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DictionaryRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RouteDebugInterval = TimeSpan.FromSeconds(1);
    private const int MaxHistoryEntries = 80;
    private const int MaxBattleSegments = 120;
    private const int MaxDashboardLogEntries = 30;
    private static DateTime _lastDumpUtc = DateTime.MinValue;
    private static DateTime _lastCommandCheckUtc = DateTime.MinValue;
    private static DateTime _lastDictionaryAttemptUtc = DateTime.MinValue;
    private static DateTime _lastRouteDebugUtc = DateTime.MinValue;
    private static bool _hooksInstalled;
    private static bool _bootstrapTimerStarted;
    private static bool _dictionaryDumped;
    private static bool _wasInCombat;
    private static int _lastProcessedHistoryEntryCount;
    private static readonly StatsAccumulator _totalStats = new();
    private static readonly Dictionary<string, StatsAccumulator> _modeTotals = new(StringComparer.Ordinal)
    {
        ["singleplayer"] = new(),
        ["multiplayer"] = new()
    };
    private static readonly List<BattleSegmentSnapshot> _battleSegments = new();
    private static BattleSegmentRuntime? _activeBattle;
    private static readonly Dictionary<string, string> _powerTypeCache = new(StringComparer.Ordinal);
    private static readonly object ModifierCatalogLock = new();
    private static readonly Dictionary<PowerSourceKey, PowerSourceRecord> _powerSourceTracker = new();
    private static readonly Dictionary<uint, ContributionAccumulator> _combatContributions = new();
    private static readonly Dictionary<ulong, ContributionAccumulator> _totalContributions = new();
    private static readonly Dictionary<string, Dictionary<ulong, ContributionAccumulator>> _modeContributions = new(StringComparer.Ordinal)
    {
        ["singleplayer"] = new Dictionary<ulong, ContributionAccumulator>(),
        ["multiplayer"] = new Dictionary<ulong, ContributionAccumulator>()
    };

    private static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sts2McpProbe");
    private static readonly string StateFilePath = Path.Combine(WorkDir, "state.json");
    private static readonly string StatusFilePath = Path.Combine(WorkDir, "status.json");
    private static readonly string DictionaryFilePath = Path.Combine(WorkDir, "dictionary.json");
    private static readonly string AnalyticsFilePath = Path.Combine(WorkDir, "analytics.json");
    private static readonly string DashboardFilePath = Path.Combine(WorkDir, "dashboard.json");
    private static readonly string CommandFilePath = Path.Combine(WorkDir, "command.json");
    private static readonly string ProbeLogPath = Path.Combine(WorkDir, "probe.log");
    private static readonly string ModsProbeLogPath = ResolveModsProbeLogPath();
    private static readonly FieldInfo? CombatTrackerStateField =
        typeof(CombatStateTracker).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? RunManagerStateField =
        typeof(RunManager).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    private static List<OverlayModifierCatalogEntry>? _cardCatalogCache;
    private static List<OverlayModifierCatalogEntry>? _relicCatalogCache;
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            SafeLog("Initialize() invoked.");
            WriteStatus("initialized", "Mod initialized successfully.");
            ProbeOverlayManager.SetLogger(SafeLog);
            PushBootstrapOverlay("Mod 已加载，正在等待游戏 UI 与运行时...");
            TryDumpDictionary(force: true);
            TryInstallHooks("initialize");
            EnsureBootstrapTimer();
        }
        catch (Exception ex)
        {
            SafeLog("Initialize failed: " + ex);
            WriteStatus("init_error", ex.Message);
        }
    }

    private static void EnsureBootstrapTimer()
    {
        if (_hooksInstalled || _bootstrapTimerStarted)
        {
            return;
        }

        _bootstrapTimerStarted = true;
        GodotCallable.From(BootstrapTick).CallDeferred();
        SafeLog("Bootstrap retry scheduled.");
    }

    private static void ScheduleBootstrapRetry()
    {
        GodotSceneTree? tree = NGame.Instance?.GetTree();
        if (tree == null)
        {
            GodotCallable.From(BootstrapTick).CallDeferred();
            return;
        }

        GodotSceneTreeTimer timer = tree.CreateTimer(1.0);
        timer.Timeout += BootstrapTick;
    }

    private static void BootstrapTick()
    {
        _bootstrapTimerStarted = false;
        try
        {
            if (!TryInstallHooks("bootstrap_timer"))
            {
                _bootstrapTimerStarted = true;
                ScheduleBootstrapRetry();
                return;
            }

            TryDumpFromRunManager("bootstrap_timer");
        }
        catch (Exception ex)
        {
            SafeLog("Bootstrap tick failed: " + ex.Message);
            _bootstrapTimerStarted = true;
            ScheduleBootstrapRetry();
        }
    }

    private static bool TryInstallHooks(string reason)
    {
        if (_hooksInstalled)
        {
            return true;
        }

        CombatManager? combatManager = CombatManager.Instance;
        RunManager? runManager = RunManager.Instance;
        CombatStateTracker? stateTracker = combatManager?.StateTracker;
        if (combatManager == null || runManager == null || stateTracker == null)
        {
            SafeLog(
                "Hook install deferred: reason=" + reason
                + " combatManager=" + (combatManager != null)
                + " runManager=" + (runManager != null)
                + " stateTracker=" + (stateTracker != null));
            return false;
        }

        combatManager.CombatSetUp += OnCombatSetUp;
        combatManager.CombatEnded += OnCombatEnded;
        stateTracker.CombatStateChanged += OnCombatStateChanged;
        runManager.RunStarted += OnRunStarted;
        runManager.ActEntered += OnActEntered;
        runManager.RoomEntered += OnRoomEntered;
        runManager.RoomExited += OnRoomExited;
        _hooksInstalled = true;
        SafeLog("Hooks installed. reason=" + reason);
        PushBootstrapOverlay("Mod 已加载，等待战斗数据...");
        TryDumpFromRunManager("hooks_installed");
        return true;
    }

    private static void PushBootstrapOverlay(string summary)
    {
        ProbeOverlayManager.Update(new OverlayPayload
        {
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = "singleplayer",
            IsInCombat = false,
            Summary = summary,
            CombatPanel = "等待进入战斗...",
            TotalPanel = "等待累计统计...",
            RoutePanel = "等待路线数据...",
            LogsPanel = "等待战斗日志..."
        });
    }

    private static void OnCombatSetUp(CombatState state)
    {
        SafeLog($"CombatSetUp: players={state.Players.Count} enemies={state.Enemies.Count}");
        TryDumpDictionary(force: false);
        DumpState(state, "combat_setup");
    }

    private static void OnCombatEnded(MegaCrit.Sts2.Core.Rooms.CombatRoom _)
    {
        SafeLog("CombatEnded.");
        WriteStatus("combat_ended", "Combat ended.");
        TryDumpFromTrackedState("combat_ended");
    }

    private static void OnCombatStateChanged(CombatState state)
    {
        DateTime utcNow = DateTime.UtcNow;
        TryDumpDictionary(force: false);

        if (utcNow - _lastDumpUtc >= DumpInterval)
        {
            _lastDumpUtc = utcNow;
            DumpState(state, "state_changed");
        }

        if (utcNow - _lastCommandCheckUtc >= TimeSpan.FromMilliseconds(100))
        {
            _lastCommandCheckUtc = utcNow;
            TryExecuteCommand(state);
        }
    }

    private static void OnRoomEntered()
    {
        SafeLog("RoomEntered.");
        TryDumpFromTrackedState("room_entered");
    }

    private static void OnRoomExited()
    {
        SafeLog("RoomExited.");
        TryDumpFromTrackedState("room_exited");
    }

    private static void OnRunStarted(RunState _)
    {
        SafeLog("RunStarted.");
        TryDumpFromRunManager("run_started");
    }

    private static void OnActEntered()
    {
        SafeLog("ActEntered.");
        TryDumpFromRunManager("act_entered");
    }

    private static void TryDumpFromTrackedState(string reason)
    {
        try
        {
            CombatStateTracker? tracker = CombatManager.Instance?.StateTracker;
            if (tracker == null || CombatTrackerStateField == null)
            {
                SafeLog("TryDumpFromTrackedState skipped: tracker/field unavailable.");
                return;
            }

            if (CombatTrackerStateField.GetValue(tracker) is not CombatState trackedState || trackedState.RunState == null)
            {
                SafeLog("TryDumpFromTrackedState skipped: no tracked state. fallback to RunManager state.");
                TryDumpFromRunManager(reason + "_fallback");
                return;
            }

            _lastDumpUtc = DateTime.UtcNow;
            DumpState(trackedState, reason);
        }
        catch (Exception ex)
        {
            SafeLog("TryDumpFromTrackedState failed: " + ex.Message);
        }
    }

    private static void TryDumpFromRunManager(string reason)
    {
        try
        {
            RunManager? runManager = RunManager.Instance;
            if (runManager == null || RunManagerStateField == null)
            {
                SafeLog("TryDumpFromRunManager skipped: run manager/state field unavailable.");
                return;
            }

            if (RunManagerStateField.GetValue(runManager) is not RunState runState)
            {
                SafeLog("TryDumpFromRunManager skipped: run state unavailable.");
                return;
            }

            string modeKey = ResolveModeKeyFromRunManager(runManager);
            Player? localPlayer = ResolveLocalPlayer(runState);
            double hpRatio = 1;
            if (localPlayer?.Creature != null && localPlayer.Creature.MaxHp > 0)
            {
                hpRatio = Math.Clamp((double)localPlayer.Creature.CurrentHp / localPlayer.Creature.MaxHp, 0, 1);
            }

            int gold = localPlayer?.Gold ?? 0;
            RoutePlanSnapshot routePlan = BuildRoutePlanSnapshot(runState, localPlayer, modeKey, hpRatio, gold);
            List<OverlayRouteOption> routeOptions = BuildOverlayRouteOptions(routePlan);
            OverlayPayload payload = new()
            {
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Mode = modeKey,
                IsInCombat = false,
                Summary = "非战斗阶段：路线分析已启用",
                CombatPanel = "当前不在战斗，战斗详情将在进入战斗后刷新。",
                TotalPanel = BuildOverlayTotalText(BuildFallbackAnalytics(modeKey)),
                RoutePanel = BuildOverlayRouteText(routePlan),
                LogsPanel = "当前无战斗日志。",
                CombatBoardScope = "当前战斗",
                CombatDamageBoard = new List<OverlayDamageEntry>(),
                RouteLegendEntries = BuildOverlayRouteLegendEntries(routePlan),
                RouteOptions = routeOptions,
                SelectedRouteOptionIndex = -1,
                ModifierState = BuildOverlayModifierState(localPlayer),
                Alerts = routeOptions.Count == 0
                    ? new List<string> { "当前地图阶段未识别到可选路线分叉。" }
                    : new List<string>()
            };

            ProbeOverlayManager.Update(payload);
            SafeLog(
                "TryDumpFromRunManager ok: reason=" + reason
                + " mode=" + modeKey
                + " hasMap=" + (runState.Map != null)
                + " currentMapPoint=" + (runState.CurrentMapPoint != null)
                + " currentCoord=" + (runState.CurrentMapCoord.HasValue
                    ? $"{runState.CurrentMapCoord.Value.row},{runState.CurrentMapCoord.Value.col}"
                    : "null")
                + " options=" + routePlan.Options.Count.ToString(CultureInfo.InvariantCulture));

            if (routePlan.Options.Count > 0)
            {
                for (int i = 0; i < Math.Min(4, routePlan.Options.Count); i++)
                {
                    RouteOptionSnapshot opt = routePlan.Options[i];
                    SafeLog(
                        "RunRoute.Option[" + i.ToString(CultureInfo.InvariantCulture) + "] "
                        + $"{opt.PointTypeLabel} score={opt.Score:F2} coords="
                        + (opt.PathCoords.Count == 0
                            ? "<empty>"
                            : string.Join(" -> ", opt.PathCoords.Take(8).Select(static c => $"{c.Row},{c.Col}"))));
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog("TryDumpFromRunManager failed: " + ex.Message);
        }
    }

    private static string ResolveModeKeyFromRunManager(RunManager runManager)
    {
        return runManager.IsSinglePlayerOrFakeMultiplayer ? "singleplayer" : "multiplayer";
    }

    private static void TryDumpDictionary(bool force)
    {
        if (_dictionaryDumped)
        {
            return;
        }

        DateTime utcNow = DateTime.UtcNow;
        if (!force && utcNow - _lastDictionaryAttemptUtc < DictionaryRetryInterval)
        {
            return;
        }

        _lastDictionaryAttemptUtc = utcNow;
        try
        {
            DictionarySnapshot payload = BuildDictionarySnapshot();
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            lock (FileLock)
            {
                File.WriteAllText(DictionaryFilePath, json);
            }

            _dictionaryDumped = true;
            SafeLog(
                $"Dictionary dumped: cards={payload.Cards.Count} relics={payload.Relics.Count} potions={payload.Potions.Count} powers={payload.Powers.Count} monsters={payload.Monsters.Count} characters={payload.Characters.Count}");
            WriteStatus("dictionary_ready", "Dictionary exported.");
        }
        catch (Exception ex)
        {
            SafeLog("Dictionary export pending: " + ex.Message);
        }
    }

    private static DictionarySnapshot BuildDictionarySnapshot()
    {
        DictionarySnapshot snapshot = new()
        {
            ModId = ModId,
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Locale = CultureInfo.CurrentUICulture.Name
        };

        foreach (CardModel card in ModelDb.AllCards.OrderBy(static c => c.Id.Entry, StringComparer.Ordinal))
        {
            string id = card.Id.Entry;
            if (snapshot.Cards.ContainsKey(id))
            {
                continue;
            }

            snapshot.Cards[id] = new CardDictionaryEntry
            {
                Title = SafeText(() => card.Title, id),
                Description = SafeText(() => card.Description.GetFormattedText()),
                Type = card.Type.ToString(),
                TargetType = card.TargetType.ToString(),
                Rarity = card.Rarity.ToString()
            };
        }

        foreach (RelicModel relic in ModelDb.AllRelics.OrderBy(static r => r.Id.Entry, StringComparer.Ordinal))
        {
            string id = relic.Id.Entry;
            if (snapshot.Relics.ContainsKey(id))
            {
                continue;
            }

            snapshot.Relics[id] = new RelicDictionaryEntry
            {
                Title = SafeText(() => relic.Title.GetFormattedText(), id),
                Description = SafeText(() => relic.Description.GetFormattedText()),
                Rarity = relic.Rarity.ToString()
            };
        }

        foreach (PotionModel potion in ModelDb.AllPotions.OrderBy(static p => p.Id.Entry, StringComparer.Ordinal))
        {
            string id = potion.Id.Entry;
            if (snapshot.Potions.ContainsKey(id))
            {
                continue;
            }

            snapshot.Potions[id] = new PotionDictionaryEntry
            {
                Title = SafeText(() => potion.Title.GetFormattedText(), id),
                Description = SafeText(() => potion.Description.GetFormattedText()),
                Rarity = potion.Rarity.ToString(),
                Usage = potion.Usage.ToString(),
                TargetType = potion.TargetType.ToString()
            };
        }

        foreach (PowerModel power in ModelDb.AllPowers.OrderBy(static p => p.Id.Entry, StringComparer.Ordinal))
        {
            string id = power.Id.Entry;
            if (snapshot.Powers.ContainsKey(id))
            {
                continue;
            }

            snapshot.Powers[id] = new PowerDictionaryEntry
            {
                Title = SafeText(() => power.Title.GetFormattedText(), id),
                Description = SafeText(() => power.Description.GetFormattedText()),
                Type = power.Type.ToString()
            };
        }

        foreach (MonsterModel monster in ModelDb.Monsters.OrderBy(static m => m.Id.Entry, StringComparer.Ordinal))
        {
            string id = monster.Id.Entry;
            if (snapshot.Monsters.ContainsKey(id))
            {
                continue;
            }

            snapshot.Monsters[id] = new MonsterDictionaryEntry
            {
                Title = SafeText(() => monster.Title.GetFormattedText(), id),
                MinInitialHp = monster.MinInitialHp,
                MaxInitialHp = monster.MaxInitialHp
            };
        }

        foreach (CharacterModel character in ModelDb.AllCharacters.OrderBy(static c => c.Id.Entry, StringComparer.Ordinal))
        {
            string id = character.Id.Entry;
            if (snapshot.Characters.ContainsKey(id))
            {
                continue;
            }

            snapshot.Characters[id] = new CharacterDictionaryEntry
            {
                Title = SafeText(() => character.Title.GetFormattedText(), id),
                StartingHp = character.StartingHp,
                MaxEnergy = character.MaxEnergy,
                StartingDeck = character.StartingDeck
                    .Select(static c => c.Id.Entry)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static v => v, StringComparer.Ordinal)
                    .ToList(),
                StartingRelics = character.StartingRelics
                    .Select(static r => r.Id.Entry)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static v => v, StringComparer.Ordinal)
                    .ToList(),
                StartingPotions = character.StartingPotions
                    .Select(static p => p.Id.Entry)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static v => v, StringComparer.Ordinal)
                    .ToList()
            };
        }

        return snapshot;
    }

    private static string SafeText(Func<string> producer, string fallback = "")
    {
        try
        {
            return producer();
        }
        catch
        {
            return fallback;
        }
    }

    private static void TryExecuteCommand(CombatState state)
    {
        if (!File.Exists(CommandFilePath))
        {
            return;
        }

        CommandRequest? request;
        try
        {
            string json = File.ReadAllText(CommandFilePath);
            request = JsonSerializer.Deserialize<CommandRequest>(json);
        }
        catch (Exception ex)
        {
            SafeLog("Failed to parse command.json: " + ex);
            return;
        }

        if (request == null)
        {
            SafeLog("command.json parsed as null.");
            return;
        }

        try
        {
            if (!string.Equals(request.Action, "play_card", StringComparison.OrdinalIgnoreCase))
            {
                SafeLog("Unsupported command action: " + request.Action);
                WriteStatus("command_error", "Unsupported action: " + request.Action);
                return;
            }

            if (request.CombatCardIndex == null)
            {
                SafeLog("play_card missing combat_card_index.");
                WriteStatus("command_error", "Missing combat_card_index.");
                return;
            }

            if (!NetCombatCardDb.Instance.TryGetCard(request.CombatCardIndex.Value, out CardModel? card) || card == null)
            {
                SafeLog("Card not found in NetCombatCardDb: " + request.CombatCardIndex.Value);
                WriteStatus("command_error", "Card not found: " + request.CombatCardIndex.Value);
                return;
            }

            Creature? target = state.GetCreature(request.TargetCombatId);
            bool ok = card.TryManualPlay(target);
            SafeLog($"play_card index={request.CombatCardIndex.Value} target={request.TargetCombatId?.ToString() ?? "null"} result={ok}");
            WriteStatus(ok ? "command_ok" : "command_rejected", ok ? "Card play queued." : "Card play rejected by game rules.");
        }
        catch (Exception ex2)
        {
            SafeLog("Command execution failed: " + ex2);
            WriteStatus("command_error", ex2.Message);
        }
        finally
        {
            TryArchiveCommandFile();
        }
    }

    private static void TryArchiveCommandFile()
    {
        try
        {
            if (!File.Exists(CommandFilePath))
            {
                return;
            }

            string donePath = Path.Combine(
                WorkDir,
                "command.done." + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json");
            File.Move(CommandFilePath, donePath, overwrite: true);
        }
        catch (Exception ex)
        {
            SafeLog("Failed to archive command file: " + ex);
        }
    }

    private static void DumpState(CombatState state, string reason)
    {
        try
        {
            Player? localPlayerEntity = ResolveLocalPlayer(state);
            PlayerSnapshot? localPlayer = localPlayerEntity == null ? null : BuildPlayerSnapshot(localPlayerEntity);
            List<EnemySnapshot> enemies = state.Enemies.Select(enemy => BuildEnemySnapshot(enemy, state)).ToList();
            List<PlayerSnapshot> allPlayers = state.Players.Select(BuildPlayerSnapshot).ToList();
            RunContextSnapshot runContext = BuildRunContextSnapshot(state, localPlayerEntity);
            ActionSpaceSnapshot actionSpace = BuildActionSpaceSnapshot(state, localPlayerEntity);
            List<CombatHistoryEntry> historyEntries = GetCombatHistoryEntries();
            CombatHistorySnapshot combatHistory = BuildCombatHistorySnapshot(state, historyEntries);
            AnalyticsSnapshot analytics = UpdateAnalytics(state, localPlayerEntity, historyEntries, combatHistory);
            RoutePlanSnapshot routePlan = BuildRoutePlanSnapshot(state, localPlayerEntity);
            TryLogRoutePlanDebug(state, runContext, routePlan);
            analytics.RouteHistory = routePlan.HistoricalStats;
            DashboardSnapshot dashboard = BuildDashboardSnapshot(state, localPlayerEntity, analytics, routePlan, combatHistory);
            OverlayPayload overlayPayload = BuildOverlayPayload(state, historyEntries, localPlayerEntity, analytics, routePlan, combatHistory);

            Snapshot payload = new()
            {
                ModId = ModId,
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Reason = reason,
                IsInCombat = CombatManager.Instance.IsInProgress,
                DictionaryReady = _dictionaryDumped,
                DictionaryPath = DictionaryFilePath,
                RoundNumber = state.RoundNumber,
                CurrentSide = state.CurrentSide.ToString(),
                LocalPlayer = localPlayer,
                Players = allPlayers,
                Enemies = enemies,
                RunContext = runContext,
                ActionSpace = actionSpace,
                CombatHistory = combatHistory,
                Analytics = analytics,
                RoutePlan = routePlan
            };

            string stateJson = JsonSerializer.Serialize(payload, JsonOptions);
            string analyticsJson = JsonSerializer.Serialize(analytics, JsonOptions);
            string dashboardJson = JsonSerializer.Serialize(dashboard, JsonOptions);
            lock (FileLock)
            {
                File.WriteAllText(StateFilePath, stateJson);
                File.WriteAllText(AnalyticsFilePath, analyticsJson);
                File.WriteAllText(DashboardFilePath, dashboardJson);
            }

            ProbeOverlayManager.Update(overlayPayload);
        }
        catch (Exception ex)
        {
            SafeLog("DumpState failed: " + ex);
            WriteStatus("dump_error", ex.Message);
        }
    }

    private static PlayerSnapshot BuildPlayerSnapshot(Player player)
    {
        PlayerCombatState? combatState = player.PlayerCombatState;
        List<CardSnapshot> hand = combatState?.Hand.Cards.Select(card => BuildCardSnapshot(card, evaluatePlayability: true)).ToList() ?? new();
        PilesSnapshot? piles = combatState == null ? null : BuildPilesSnapshot(combatState);
        List<CardSnapshot> runDeck = player.Deck.Cards.Select(card => BuildCardSnapshot(card, evaluatePlayability: false)).ToList();
        List<RelicSnapshot> relics = player.Relics.Select(BuildRelicSnapshot).ToList();
        List<PotionSnapshot> potions = player.PotionSlots.Select(BuildPotionSnapshot).ToList();
        List<PowerSnapshot> powers = player.Creature.Powers.Select(BuildPowerSnapshot).ToList();

        return new PlayerSnapshot
        {
            NetId = player.NetId,
            CharacterId = player.Character.Id.Entry,
            Name = player.Creature.Name,
            CurrentHp = player.Creature.CurrentHp,
            MaxHp = player.Creature.MaxHp,
            Block = player.Creature.Block,
            Gold = player.Gold,
            MaxEnergy = player.MaxEnergy,
            Energy = combatState?.Energy,
            Stars = combatState?.Stars,
            Hand = hand,
            RunDeck = runDeck,
            Piles = piles,
            Relics = relics,
            Potions = potions,
            Powers = powers
        };
    }

    private static PilesSnapshot BuildPilesSnapshot(PlayerCombatState combatState)
    {
        return new PilesSnapshot
        {
            Draw = BuildPileSnapshot(combatState.DrawPile),
            Hand = BuildPileSnapshot(combatState.Hand),
            Discard = BuildPileSnapshot(combatState.DiscardPile),
            Exhaust = BuildPileSnapshot(combatState.ExhaustPile),
            Play = BuildPileSnapshot(combatState.PlayPile)
        };
    }

    private static CardPileSnapshot BuildPileSnapshot(CardPile pile)
    {
        bool evaluatePlayability = pile.Type == PileType.Hand;
        return new CardPileSnapshot
        {
            Type = pile.Type.ToString(),
            Count = pile.Cards.Count,
            Cards = pile.Cards.Select(card => BuildCardSnapshot(card, evaluatePlayability)).ToList()
        };
    }

    private static CardSnapshot BuildCardSnapshot(CardModel card, bool evaluatePlayability)
    {
        uint? combatCardIndex = null;
        if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
        {
            combatCardIndex = id;
        }

        int? currentEnergyCost = null;
        bool? costsX = null;
        int? currentStarCost = null;
        try
        {
            currentEnergyCost = card.EnergyCost.GetWithModifiers(CostModifiers.All);
            costsX = card.EnergyCost.CostsX;
            currentStarCost = card.GetStarCostWithModifiers();
        }
        catch
        {
            // Keep null when card is in a transitional state.
        }

        bool canPlay = false;
        bool canPlayEvaluated = false;
        string? unplayableReason = null;
        string? preventedByModelId = null;
        if (evaluatePlayability)
        {
            canPlayEvaluated = true;
            try
            {
                canPlay = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
                unplayableReason = reason.ToString();
                preventedByModelId = preventer?.Id.Entry;
            }
            catch
            {
                canPlay = false;
                unplayableReason = "EvaluationFailed";
            }
        }

        return new CardSnapshot
        {
            CombatCardIndex = combatCardIndex,
            IdEntry = card.Id.Entry,
            Title = card.Title,
            Type = card.Type.ToString(),
            TargetType = card.TargetType.ToString(),
            CurrentEnergyCost = currentEnergyCost,
            CurrentStarCost = currentStarCost,
            CostsX = costsX,
            CanPlay = canPlay,
            CanPlayEvaluated = canPlayEvaluated,
            UnplayableReason = unplayableReason,
            PreventedByModelId = preventedByModelId
        };
    }

    private static EnemySnapshot BuildEnemySnapshot(Creature enemy, CombatState state)
    {
        List<IntentSnapshot> intents = new();
        if (enemy.Monster?.NextMove?.Intents != null)
        {
            foreach (AbstractIntent intent in enemy.Monster.NextMove.Intents)
            {
                intents.Add(BuildIntentSnapshot(intent, enemy, state));
            }
        }
        List<PowerSnapshot> powers = enemy.Powers.Select(BuildPowerSnapshot).ToList();

        return new EnemySnapshot
        {
            CombatId = enemy.CombatId,
            Name = enemy.Name,
            ModelId = enemy.ModelId.Entry,
            CurrentHp = enemy.CurrentHp,
            MaxHp = enemy.MaxHp,
            Block = enemy.Block,
            IsHittable = enemy.IsHittable,
            Intents = intents,
            Powers = powers
        };
    }

    private static RelicSnapshot BuildRelicSnapshot(RelicModel relic)
    {
        return new RelicSnapshot
        {
            IdEntry = relic.Id.Entry,
            Title = SafeText(() => relic.Title.GetFormattedText(), relic.Id.Entry),
            Rarity = relic.Rarity.ToString()
        };
    }

    private static PotionSnapshot BuildPotionSnapshot(PotionModel? potion, int slotIndex)
    {
        if (potion == null)
        {
            return new PotionSnapshot
            {
                SlotIndex = slotIndex,
                IsEmpty = true
            };
        }

        return new PotionSnapshot
        {
            SlotIndex = slotIndex,
            IsEmpty = false,
            IdEntry = potion.Id.Entry,
            Title = SafeText(() => potion.Title.GetFormattedText(), potion.Id.Entry),
            Rarity = potion.Rarity.ToString(),
            Usage = potion.Usage.ToString(),
            TargetType = potion.TargetType.ToString()
        };
    }

    private static PowerSnapshot BuildPowerSnapshot(PowerModel power)
    {
        return new PowerSnapshot
        {
            IdEntry = power.Id.Entry,
            Title = SafeText(() => power.Title.GetFormattedText(), power.Id.Entry),
            Type = power.Type.ToString(),
            Amount = power.Amount
        };
    }

    private static IntentSnapshot BuildIntentSnapshot(AbstractIntent intent, Creature owner, CombatState state)
    {
        string? label = null;
        try
        {
            label = intent.GetIntentLabel(state.PlayerCreatures, owner).GetFormattedText();
        }
        catch
        {
            // Keep null if label generation fails.
        }

        return new IntentSnapshot
        {
            IntentType = intent.IntentType.ToString(),
            Label = label
        };
    }

    private static RunContextSnapshot BuildRunContextSnapshot(CombatState state, Player? localPlayer)
    {
        IRunState runState = state.RunState;
        List<MapPoint> nextChoices = GetNextRouteChoices(runState);
        RunContextSnapshot snapshot = new()
        {
            IsRunInProgress = RunManager.Instance.IsInProgress,
            IsGameOver = runState.IsGameOver,
            RunTimeSeconds = RunManager.Instance.IsInProgress ? RunManager.Instance.RunTime : null,
            AscensionLevel = runState.AscensionLevel,
            CurrentActIndex = runState.CurrentActIndex,
            CurrentActId = runState.Act.Id.Entry,
            CurrentActTitle = SafeText(() => runState.Act.Title.GetFormattedText(), runState.Act.Id.Entry),
            ActFloor = runState.ActFloor,
            TotalFloor = runState.TotalFloor,
            CurrentRoomCount = runState.CurrentRoomCount,
            CurrentRoomType = runState.CurrentRoom?.RoomType.ToString(),
            CurrentRoomModelId = runState.CurrentRoom?.ModelId?.Entry,
            SeedString = runState.Rng.StringSeed,
            SeedValue = runState.Rng.Seed,
            CurrentMapCoord = BuildMapCoordSnapshot(runState.CurrentMapCoord),
            CurrentMapPointType = runState.CurrentMapPoint?.PointType.ToString(),
            NextMapChoices = nextChoices
                .OrderBy(static p => p.coord.row)
                .ThenBy(static p => p.coord.col)
                .Select(BuildMapPointChoiceSnapshot)
                .ToList(),
            Modifiers = runState.Modifiers
                .Select(static m => m.Id.Entry)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToList()
        };

        if (state.Encounter != null)
        {
            snapshot.Encounter = new EncounterSnapshot
            {
                IdEntry = state.Encounter.Id.Entry,
                Title = SafeText(() => state.Encounter.Title.GetFormattedText(), state.Encounter.Id.Entry),
                RoomType = state.Encounter.RoomType.ToString(),
                Slots = state.Encounter.Slots.ToList()
            };
        }

        if (localPlayer != null)
        {
            snapshot.LocalPlayer = new RunLocalPlayerSnapshot
            {
                NetId = localPlayer.NetId,
                CharacterId = localPlayer.Character.Id.Entry,
                Gold = localPlayer.Gold,
                DeckCount = localPlayer.Deck.Cards.Count,
                RelicCount = localPlayer.Relics.Count,
                PotionCount = localPlayer.Potions.Count()
            };
        }

        MapPointHistoryEntry? currentHistory = runState.CurrentMapPointHistoryEntry;
        if (currentHistory != null)
        {
            RunHistoryPointSnapshot historySnapshot = new()
            {
                MapPointType = currentHistory.MapPointType.ToString(),
                Rooms = currentHistory.Rooms.Select(BuildMapPointRoomSnapshot).ToList()
            };

            if (localPlayer != null)
            {
                try
                {
                    PlayerMapPointHistoryEntry playerStats = currentHistory.GetEntry(localPlayer.NetId);
                    historySnapshot.LocalPlayerStats = new MapPointPlayerStatsSnapshot
                    {
                        PlayerId = playerStats.PlayerId,
                        CurrentGold = playerStats.CurrentGold,
                        CurrentHp = playerStats.CurrentHp,
                        MaxHp = playerStats.MaxHp,
                        GoldGained = playerStats.GoldGained,
                        GoldSpent = playerStats.GoldSpent,
                        DamageTaken = playerStats.DamageTaken,
                        HpHealed = playerStats.HpHealed
                    };
                }
                catch
                {
                    // Ignore if entry does not exist for local player.
                }
            }

            snapshot.CurrentMapPointHistory = historySnapshot;
        }

        return snapshot;
    }

    private static List<MapPoint> GetNextRouteChoices(IRunState runState)
    {
        if (runState.CurrentMapPoint != null)
        {
            return runState.CurrentMapPoint.Children
                .Where(static c => c != null)
                .ToList();
        }

        MapPoint? startingMapPoint = runState.Map?.StartingMapPoint;
        if (startingMapPoint != null)
        {
            return startingMapPoint.Children
                .Where(static c => c != null)
                .ToList();
        }

        return new List<MapPoint>();
    }

    private static MapCoordSnapshot? BuildMapCoordSnapshot(MapCoord? mapCoord)
    {
        if (!mapCoord.HasValue)
        {
            return null;
        }

        return new MapCoordSnapshot
        {
            Col = mapCoord.Value.col,
            Row = mapCoord.Value.row
        };
    }

    private static MapPointChoiceSnapshot BuildMapPointChoiceSnapshot(MapPoint mapPoint)
    {
        return new MapPointChoiceSnapshot
        {
            Coord = new MapCoordSnapshot
            {
                Col = mapPoint.coord.col,
                Row = mapPoint.coord.row
            },
            PointType = mapPoint.PointType.ToString(),
            QuestIds = mapPoint.Quests
                .Select(static q => q.Id.Entry)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static q => q, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static MapPointRoomSnapshot BuildMapPointRoomSnapshot(MapPointRoomHistoryEntry room)
    {
        return new MapPointRoomSnapshot
        {
            RoomType = room.RoomType.ToString(),
            ModelId = room.ModelId?.Entry,
            TurnsTaken = room.TurnsTaken,
            MonsterIds = room.MonsterIds.Select(static id => id.Entry).ToList()
        };
    }

    private static ActionSpaceSnapshot BuildActionSpaceSnapshot(CombatState state, Player? localPlayer)
    {
        ActionSpaceSnapshot snapshot = new()
        {
            IsPlayerTurn = state.CurrentSide == CombatSide.Player,
            PlayerActionsDisabled = CombatManager.Instance.PlayerActionsDisabled
        };

        if (localPlayer?.PlayerCombatState == null)
        {
            return snapshot;
        }

        foreach (CardModel card in localPlayer.PlayerCombatState.Hand.Cards)
        {
            snapshot.PlayCardActions.Add(BuildPlayCardActionOption(card, state, localPlayer));
        }

        snapshot.PlayCardActions = snapshot.PlayCardActions
            .OrderBy(static action => action.SortOrder)
            .ToList();
        snapshot.ActionCount = snapshot.PlayCardActions.Count;
        snapshot.PlayableActionCount = snapshot.PlayCardActions.Count(static a => a.CanPlayNow);
        return snapshot;
    }

    private static PlayCardActionOptionSnapshot BuildPlayCardActionOption(CardModel card, CombatState state, Player owner)
    {
        uint? combatCardIndex = null;
        if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
        {
            combatCardIndex = id;
        }

        bool canPlay;
        string unplayableReason;
        string? preventedByModelId = null;
        try
        {
            canPlay = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
            unplayableReason = reason.ToString();
            preventedByModelId = preventer?.Id.Entry;
        }
        catch
        {
            canPlay = false;
            unplayableReason = "EvaluationFailed";
        }

        bool requiresTarget = RequiresExplicitTarget(card.TargetType);
        List<TargetOptionSnapshot> validTargets = BuildValidTargets(card, state, owner);
        bool canPlayNow = canPlay && (!requiresTarget || validTargets.Count > 0);

        if (canPlay && requiresTarget && validTargets.Count == 0)
        {
            unplayableReason = unplayableReason == "None" ? "NoValidTargets" : unplayableReason + ", NoValidTargets";
        }

        return new PlayCardActionOptionSnapshot
        {
            SortOrder = combatCardIndex ?? uint.MaxValue,
            CombatCardIndex = combatCardIndex,
            CardId = card.Id.Entry,
            CardTitle = card.Title,
            TargetType = card.TargetType.ToString(),
            RequiresTarget = requiresTarget,
            CanPlayByRules = canPlay,
            CanPlayNow = canPlayNow,
            UnplayableReason = unplayableReason,
            PreventedByModelId = preventedByModelId,
            ValidTargets = validTargets,
            ExampleCommand = new PlayCardCommandExample
            {
                Action = "play_card",
                CombatCardIndex = combatCardIndex,
                TargetCombatId = validTargets.FirstOrDefault()?.CombatId
            }
        };
    }

    private static bool RequiresExplicitTarget(TargetType targetType)
    {
        return targetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer;
    }

    private static List<TargetOptionSnapshot> BuildValidTargets(CardModel card, CombatState state, Player owner)
    {
        List<TargetOptionSnapshot> validTargets = new();
        foreach (Creature creature in EnumeratePotentialTargets(card, state, owner))
        {
            bool canTarget;
            try
            {
                canTarget = card.CanPlayTargeting(creature);
            }
            catch
            {
                canTarget = false;
            }

            if (!canTarget)
            {
                continue;
            }

            validTargets.Add(BuildTargetOptionSnapshot(creature));
        }

        return validTargets;
    }

    private static IEnumerable<Creature> EnumeratePotentialTargets(CardModel card, CombatState state, Player owner)
    {
        switch (card.TargetType)
        {
            case TargetType.AnyEnemy:
                return state.Enemies.Where(static e => e.IsAlive && e.IsHittable);
            case TargetType.AnyAlly:
                return state.GetCreaturesOnSide(owner.Creature.Side)
                    .Where(c => c.IsAlive && c.CombatId != owner.Creature.CombatId);
            case TargetType.AnyPlayer:
                return state.PlayerCreatures.Where(static c => c.IsAlive);
            case TargetType.Osty:
                return owner.Osty != null && owner.Osty.IsAlive
                    ? new[] { owner.Osty }
                    : Array.Empty<Creature>();
            default:
                return Array.Empty<Creature>();
        }
    }

    private static TargetOptionSnapshot BuildTargetOptionSnapshot(Creature creature)
    {
        return new TargetOptionSnapshot
        {
            CombatId = creature.CombatId,
            ModelId = creature.ModelId.Entry,
            Name = creature.Name,
            Side = creature.Side.ToString(),
            IsAlive = creature.IsAlive,
            IsHittable = creature.IsHittable
        };
    }

    private static List<CombatHistoryEntry> GetCombatHistoryEntries()
    {
        try
        {
            return CombatManager.Instance.History.Entries.ToList();
        }
        catch
        {
            return new List<CombatHistoryEntry>();
        }
    }

    private static CombatHistorySnapshot BuildCombatHistorySnapshot(CombatState state, IReadOnlyList<CombatHistoryEntry> entries)
    {
        int totalEntries = entries.Count;
        int start = Math.Max(0, totalEntries - MaxHistoryEntries);
        List<CombatHistoryEntrySnapshot> recentEntries = new(totalEntries - start);
        for (int i = start; i < totalEntries; i++)
        {
            CombatHistoryEntry entry = entries[i];
            try
            {
                recentEntries.Add(BuildCombatHistoryEntrySnapshot(entry, state, i));
            }
            catch (Exception ex)
            {
                recentEntries.Add(BuildCombatHistoryEntryErrorSnapshot(entry, i, ex));
            }
        }

        return new CombatHistorySnapshot
        {
            TotalEntries = totalEntries,
            RecentEntries = recentEntries
        };
    }

    private static CombatHistoryEntrySnapshot BuildCombatHistoryEntrySnapshot(CombatHistoryEntry entry, CombatState state, int index)
    {
        string actorModelId = string.Empty;
        string actorName = string.Empty;
        uint? actorCombatId = null;
        try
        {
            if (entry.Actor != null)
            {
                actorCombatId = entry.Actor.CombatId;
                actorModelId = entry.Actor.ModelId.Entry;
                actorName = entry.Actor.Name;
            }
        }
        catch
        {
            // Ignore transient actor state.
        }

        bool happenedThisTurn = false;
        try
        {
            happenedThisTurn = entry.HappenedThisTurn(state);
        }
        catch
        {
            // Ignore history helper failures.
        }

        string description = SafeText(() => entry.Description);
        string humanReadable = SafeText(() => entry.HumanReadableString);

        CombatHistoryEntrySnapshot snapshot = new()
        {
            Index = index,
            EntryType = entry.GetType().Name,
            RoundNumber = entry.RoundNumber,
            CurrentSide = entry.CurrentSide.ToString(),
            ActorCombatId = actorCombatId,
            ActorModelId = actorModelId,
            ActorName = actorName,
            HappenedThisTurn = happenedThisTurn,
            Description = description,
            HumanReadable = humanReadable
        };

        Dictionary<string, string?> details = snapshot.Details;
        try
        {
            switch (entry)
            {
                case CardPlayStartedEntry cardPlayStarted:
                    AddDetail(details, "card_id", cardPlayStarted.CardPlay.Card.Id.Entry);
                    if (NetCombatCardDb.Instance.TryGetCardId(cardPlayStarted.CardPlay.Card, out uint cardId1))
                    {
                        AddDetail(details, "combat_card_index", cardId1);
                    }
                    try
                    {
                        AddDetail(details, "energy_cost", cardPlayStarted.CardPlay.Card.EnergyCost.GetWithModifiers(CostModifiers.All));
                        AddDetail(details, "star_cost", cardPlayStarted.CardPlay.Card.GetStarCostWithModifiers());
                    }
                    catch
                    {
                        // Ignore transient card state.
                    }
                    AddDetail(details, "target_combat_id", cardPlayStarted.CardPlay.Target?.CombatId);
                    AddDetail(details, "target_model_id", cardPlayStarted.CardPlay.Target?.ModelId.Entry);
                    break;
                case CardPlayFinishedEntry cardPlayFinished:
                    AddDetail(details, "card_id", cardPlayFinished.CardPlay.Card.Id.Entry);
                    if (NetCombatCardDb.Instance.TryGetCardId(cardPlayFinished.CardPlay.Card, out uint cardId2))
                    {
                        AddDetail(details, "combat_card_index", cardId2);
                    }
                    AddDetail(details, "target_combat_id", cardPlayFinished.CardPlay.Target?.CombatId);
                    AddDetail(details, "target_model_id", cardPlayFinished.CardPlay.Target?.ModelId.Entry);
                    AddDetail(details, "result_pile", cardPlayFinished.CardPlay.ResultPile);
                    AddDetail(details, "is_auto_play", cardPlayFinished.CardPlay.IsAutoPlay);
                    AddDetail(details, "play_index", cardPlayFinished.CardPlay.PlayIndex);
                    AddDetail(details, "play_count", cardPlayFinished.CardPlay.PlayCount);
                    AddDetail(details, "was_ethereal", cardPlayFinished.WasEthereal);
                    try
                    {
                        AddDetail(details, "energy_cost", cardPlayFinished.CardPlay.Card.EnergyCost.GetWithModifiers(CostModifiers.All));
                        AddDetail(details, "star_cost", cardPlayFinished.CardPlay.Card.GetStarCostWithModifiers());
                    }
                    catch
                    {
                        // Ignore transient card state.
                    }
                    break;
                case DamageReceivedEntry damageReceived:
                    AddDetail(details, "receiver_combat_id", damageReceived.Receiver.CombatId);
                    AddDetail(details, "receiver_model_id", damageReceived.Receiver.ModelId.Entry);
                    AddDetail(details, "dealer_combat_id", damageReceived.Dealer?.CombatId);
                    AddDetail(details, "dealer_model_id", damageReceived.Dealer?.ModelId.Entry);
                    AddDetail(details, "card_source_id", damageReceived.CardSource?.Id.Entry);
                    AddDetail(details, "blocked_damage", damageReceived.Result.BlockedDamage);
                    AddDetail(details, "unblocked_damage", damageReceived.Result.UnblockedDamage);
                    AddDetail(details, "overkill_damage", damageReceived.Result.OverkillDamage);
                    AddDetail(details, "was_fully_blocked", damageReceived.Result.WasFullyBlocked);
                    AddDetail(details, "was_target_killed", damageReceived.Result.WasTargetKilled);
                    break;
                case BlockGainedEntry blockGained:
                    AddDetail(details, "receiver_combat_id", blockGained.Receiver.CombatId);
                    AddDetail(details, "receiver_model_id", blockGained.Receiver.ModelId.Entry);
                    AddDetail(details, "amount", blockGained.Amount);
                    AddDetail(details, "value_props", blockGained.Props);
                    AddDetail(details, "card_id", blockGained.CardPlay?.Card.Id.Entry);
                    break;
                case EnergySpentEntry energySpent:
                    AddDetail(details, "amount", energySpent.Amount);
                    break;
                case StarsModifiedEntry starsModified:
                    AddDetail(details, "amount", starsModified.Amount);
                    break;
                case SummonedEntry summoned:
                    AddDetail(details, "amount", summoned.Amount);
                    break;
                case CardDrawnEntry cardDrawn:
                    AddDetail(details, "card_id", cardDrawn.Card.Id.Entry);
                    AddDetail(details, "from_hand_draw", cardDrawn.FromHandDraw);
                    break;
                case CardDiscardedEntry cardDiscarded:
                    AddDetail(details, "card_id", cardDiscarded.Card.Id.Entry);
                    break;
                case CardExhaustedEntry cardExhausted:
                    AddDetail(details, "card_id", cardExhausted.Card.Id.Entry);
                    break;
                case CardGeneratedEntry cardGenerated:
                    AddDetail(details, "card_id", cardGenerated.Card.Id.Entry);
                    AddDetail(details, "generated_by_player", cardGenerated.GeneratedByPlayer);
                    break;
                case CardAfflictedEntry cardAfflicted:
                    AddDetail(details, "card_id", cardAfflicted.Card.Id.Entry);
                    AddDetail(details, "affliction_id", cardAfflicted.Affliction.Id.Entry);
                    break;
                case CreatureAttackedEntry creatureAttacked:
                    AddDetail(details, "damage_results", creatureAttacked.DamageResults.Count);
                    AddDetail(details, "total_damage", creatureAttacked.DamageResults.Sum(static d => d.TotalDamage));
                    AddDetail(details, "targets", string.Join(",", creatureAttacked.DamageResults.Select(static d => d.Receiver.ModelId.Entry)));
                    break;
                case MonsterPerformedMoveEntry monsterMove:
                    AddDetail(details, "monster_id", monsterMove.Monster.Id.Entry);
                    AddDetail(details, "move_id", monsterMove.Move.Id);
                    AddDetail(details, "targets", monsterMove.Targets == null ? null : string.Join(",", monsterMove.Targets.Select(static t => t.ModelId.Entry)));
                    break;
                case PotionUsedEntry potionUsed:
                    AddDetail(details, "potion_id", potionUsed.Potion.Id.Entry);
                    AddDetail(details, "target_combat_id", potionUsed.Target?.CombatId);
                    AddDetail(details, "target_model_id", potionUsed.Target?.ModelId.Entry);
                    break;
                case PowerReceivedEntry powerReceived:
                    AddDetail(details, "power_id", powerReceived.Power.Id.Entry);
                    AddDetail(details, "amount", powerReceived.Amount);
                    AddDetail(details, "applier_combat_id", powerReceived.Applier?.CombatId);
                    AddDetail(details, "applier_model_id", powerReceived.Applier?.ModelId.Entry);
                    break;
                case OrbChanneledEntry orbChanneled:
                    AddDetail(details, "orb_id", orbChanneled.Orb.Id.Entry);
                    break;
            }
        }
        catch (Exception detailEx)
        {
            AddDetail(details, "detail_error", detailEx.GetType().Name + ": " + detailEx.Message);
        }

        return snapshot;
    }

    private static CombatHistoryEntrySnapshot BuildCombatHistoryEntryErrorSnapshot(CombatHistoryEntry entry, int index, Exception ex)
    {
        return new CombatHistoryEntrySnapshot
        {
            Index = index,
            EntryType = entry.GetType().Name,
            RoundNumber = entry.RoundNumber,
            CurrentSide = entry.CurrentSide.ToString(),
            Description = "history_entry_error",
            HumanReadable = ex.GetType().Name + ": " + ex.Message,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["error"] = ex.ToString()
            }
        };
    }

    private static void AddDetail(IDictionary<string, string?> details, string key, object? value)
    {
        if (value == null)
        {
            return;
        }

        string text = value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        details[key] = text;
    }

    private static string ResolveModeKey(CombatState state)
    {
        return state.Players.Count > 1 ? "multiplayer" : "singleplayer";
    }

    private static StatsAccumulator EnsureModeTotalsBucket(string modeKey)
    {
        if (_modeTotals.TryGetValue(modeKey, out StatsAccumulator? bucket))
        {
            return bucket;
        }

        bucket = new StatsAccumulator();
        _modeTotals[modeKey] = bucket;
        return bucket;
    }

    private static AnalyticsSnapshot UpdateAnalytics(
        CombatState state,
        Player? localPlayer,
        IReadOnlyList<CombatHistoryEntry> historyEntries,
        CombatHistorySnapshot combatHistory)
    {
        string modeKey = ResolveModeKey(state);
        bool isInCombat = CombatManager.Instance.IsInProgress;
        uint? localCombatId = localPlayer?.Creature?.CombatId;
        Dictionary<uint, Player> playersByCombatId = state.Players
            .Where(static p => p.Creature.CombatId.HasValue)
            .ToDictionary(static p => p.Creature.CombatId!.Value, static p => p);

        if (isInCombat && !_wasInCombat)
        {
            StartNewBattle(state, modeKey);
        }
        bool combatJustEnded = !isInCombat && _wasInCombat;

        if (_activeBattle != null && historyEntries.Count < _lastProcessedHistoryEntryCount)
        {
            _lastProcessedHistoryEntryCount = 0;
        }

        if (_activeBattle != null && localCombatId.HasValue)
        {
            StatsAccumulator modeTotals = EnsureModeTotalsBucket(_activeBattle.ModeKey);
            for (int i = _lastProcessedHistoryEntryCount; i < historyEntries.Count; i++)
            {
                CombatHistoryEntry entry = historyEntries[i];
                try
                {
                    ProcessHistoryEntryForStats(entry, localCombatId.Value, _totalStats);
                    ProcessHistoryEntryForStats(entry, localCombatId.Value, modeTotals);
                    ProcessHistoryEntryForStats(entry, localCombatId.Value, _activeBattle.Stats);
                    ProcessHistoryEntryForContribution(entry, state, modeKey, playersByCombatId);
                }
                catch (Exception ex)
                {
                    SafeLog(
                        "Analytics entry skipped: type=" + entry.GetType().Name
                        + " round=" + entry.RoundNumber.ToString(CultureInfo.InvariantCulture)
                        + " error=" + ex.Message);
                }
            }

            _lastProcessedHistoryEntryCount = historyEntries.Count;
            PrunePowerSourceTracker(state);
            _activeBattle.LastUpdatedUtc = DateTime.UtcNow;
            _activeBattle.LastEnemyAliveCount = state.Enemies.Count(static e => e.IsAlive);
            _activeBattle.LastRoundNumber = state.RoundNumber;
            _activeBattle.EncounterId = state.Encounter?.Id.Entry ?? _activeBattle.EncounterId;
            _activeBattle.EncounterTitle = state.Encounter == null
                ? _activeBattle.EncounterTitle
                : SafeText(() => state.Encounter.Title.GetFormattedText(), state.Encounter.Id.Entry);
            _activeBattle.ActFloor = state.RunState.ActFloor;
            _activeBattle.TotalFloor = state.RunState.TotalFloor;
        }

        if (combatJustEnded)
        {
            FinalizeActiveBattle(localPlayer, "combat_ended");
        }

        if (!isInCombat && _activeBattle == null)
        {
            _lastProcessedHistoryEntryCount = 0;
        }

        _wasInCombat = isInCombat;

        foreach (Player player in state.Players)
        {
            uint? combatId = player.Creature.CombatId;
            if (combatId.HasValue)
            {
                EnsureCombatContribution(combatId.Value, playersByCombatId);
            }
        }

        StatsAccumulator singleTotal = EnsureModeTotalsBucket("singleplayer");
        StatsAccumulator multiTotal = EnsureModeTotalsBucket("multiplayer");
        CombatMetricsSnapshot currentCombatMetrics = _activeBattle == null
            ? new CombatMetricsSnapshot()
            : BuildMetricsSnapshot(_activeBattle.Stats);

        List<BattleSegmentSnapshot> recentBattles = _battleSegments
            .TakeLast(20)
            .Reverse()
            .ToList();

        List<BattleSegmentSnapshot> recentSingleBattles = _battleSegments
            .Where(static b => b.Mode == "singleplayer")
            .TakeLast(12)
            .Reverse()
            .ToList();

        List<BattleSegmentSnapshot> recentMultiBattles = _battleSegments
            .Where(static b => b.Mode == "multiplayer")
            .TakeLast(12)
            .Reverse()
            .ToList();

        AnalyticsSnapshot analytics = new()
        {
            ModId = ModId,
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = modeKey,
            IsInCombat = isInCombat,
            CurrentCombat = currentCombatMetrics,
            Total = BuildMetricsSnapshot(_totalStats),
            Singleplayer = new ModeMetricsSnapshot
            {
                Mode = "singleplayer",
                Total = BuildMetricsSnapshot(singleTotal),
                RecentBattles = recentSingleBattles
            },
            Multiplayer = new ModeMetricsSnapshot
            {
                Mode = "multiplayer",
                Total = BuildMetricsSnapshot(multiTotal),
                RecentBattles = recentMultiBattles
            },
            RecentBattles = recentBattles,
            RecentCombatLog = BuildRecentCombatLog(combatHistory),
            Highlights = BuildAnalyticsHighlights(state, localPlayer, currentCombatMetrics, modeKey),
            CurrentContributionBoard = BuildContributionBoardSnapshot(_combatContributions.Values),
            TotalContributionBoard = BuildContributionBoardSnapshot(_totalContributions.Values)
        };

        return analytics;
    }

    private static void StartNewBattle(CombatState state, string modeKey)
    {
        if (_activeBattle != null)
        {
            FinalizeActiveBattle(null, "interrupted");
        }

        _activeBattle = new BattleSegmentRuntime
        {
            CombatId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + state.RunState.TotalFloor.ToString(CultureInfo.InvariantCulture),
            ModeKey = modeKey,
            StartedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow,
            EncounterId = state.Encounter?.Id.Entry,
            EncounterTitle = state.Encounter == null
                ? null
                : SafeText(() => state.Encounter.Title.GetFormattedText(), state.Encounter.Id.Entry),
            ActFloor = state.RunState.ActFloor,
            TotalFloor = state.RunState.TotalFloor,
            LastEnemyAliveCount = state.Enemies.Count(static e => e.IsAlive),
            LastRoundNumber = state.RoundNumber
        };
        _combatContributions.Clear();
        _powerSourceTracker.Clear();
        _lastProcessedHistoryEntryCount = 0;
    }

    private static void FinalizeActiveBattle(Player? localPlayer, string reason)
    {
        if (_activeBattle == null)
        {
            return;
        }

        string result = "ended";
        if (string.Equals(reason, "interrupted", StringComparison.Ordinal))
        {
            result = "interrupted";
        }
        else if (localPlayer?.Creature != null && localPlayer.Creature.CurrentHp <= 0)
        {
            result = "defeat";
        }
        else if (_activeBattle.LastEnemyAliveCount <= 0)
        {
            result = "victory";
        }

        BattleSegmentSnapshot segment = new()
        {
            CombatId = _activeBattle.CombatId,
            Mode = _activeBattle.ModeKey,
            EncounterId = _activeBattle.EncounterId,
            EncounterTitle = _activeBattle.EncounterTitle,
            ActFloor = _activeBattle.ActFloor,
            TotalFloor = _activeBattle.TotalFloor,
            RoundCount = _activeBattle.LastRoundNumber,
            StartedAtUtc = _activeBattle.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            EndedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            DurationSeconds = (int)Math.Max(0, (DateTime.UtcNow - _activeBattle.StartedUtc).TotalSeconds),
            Result = result,
            Metrics = BuildMetricsSnapshot(_activeBattle.Stats)
        };

        SafeLog(
            "FinalizeActiveBattle: "
            + "combatId=" + segment.CombatId
            + " result=" + segment.Result
            + " rounds=" + segment.RoundCount.ToString(CultureInfo.InvariantCulture)
            + " duration=" + segment.DurationSeconds.ToString(CultureInfo.InvariantCulture) + "s"
            + " damage=" + segment.Metrics.DamageDealt.ToString(CultureInfo.InvariantCulture)
            + " taken=" + segment.Metrics.DamageTaken.ToString(CultureInfo.InvariantCulture)
            + " cards=" + segment.Metrics.CardsPlayed.ToString(CultureInfo.InvariantCulture)
            + " reason=" + reason);

        _battleSegments.Add(segment);
        if (_battleSegments.Count > MaxBattleSegments)
        {
            _battleSegments.RemoveAt(0);
        }

        _activeBattle = null;
        _lastProcessedHistoryEntryCount = 0;
    }

    private static void ProcessHistoryEntryForStats(CombatHistoryEntry entry, uint localCombatId, StatsAccumulator stats)
    {
        switch (entry)
        {
            case CardPlayFinishedEntry cardPlayFinished
                when cardPlayFinished.Actor.CombatId == localCombatId:
            {
                string cardId = cardPlayFinished.CardPlay.Card.Id.Entry;
                CardStatAccumulator cardStats = GetOrCreateCardStats(stats, cardId);
                cardStats.Played += 1;
                stats.CardsPlayed += 1;

                int energyCost = 0;
                try
                {
                    energyCost = Math.Max(0, cardPlayFinished.CardPlay.Card.EnergyCost.GetWithModifiers(CostModifiers.All));
                }
                catch
                {
                    energyCost = 0;
                }

                cardStats.EnergySpent += energyCost;
                break;
            }
            case DamageReceivedEntry damageReceived:
            {
                int blocked = Math.Max(0, damageReceived.Result.BlockedDamage);
                int unblocked = Math.Max(0, damageReceived.Result.UnblockedDamage);
                int total = blocked + unblocked;
                int overkill = Math.Max(0, damageReceived.Result.OverkillDamage);

                if (damageReceived.Dealer?.CombatId == localCombatId)
                {
                    stats.DamageDealt += total;
                    stats.OverkillDamage += overkill;

                    string? cardSourceId = damageReceived.CardSource?.Id.Entry;
                    if (!string.IsNullOrEmpty(cardSourceId))
                    {
                        CardStatAccumulator cardStats = GetOrCreateCardStats(stats, cardSourceId);
                        cardStats.DamageDealt += total;
                        cardStats.OverkillDamage += overkill;
                    }
                }

                if (damageReceived.Receiver.CombatId == localCombatId)
                {
                    stats.DamageTaken += unblocked;
                    stats.DamageBlocked += blocked;
                }

                break;
            }
            case BlockGainedEntry blockGained
                when blockGained.Receiver.CombatId == localCombatId:
            {
                int amount = Math.Max(0, blockGained.Amount);
                stats.BlockGained += amount;

                string? cardId = blockGained.CardPlay?.Card.Id.Entry;
                if (!string.IsNullOrEmpty(cardId))
                {
                    CardStatAccumulator cardStats = GetOrCreateCardStats(stats, cardId);
                    cardStats.BlockGained += amount;
                }
                break;
            }
            case EnergySpentEntry energySpent
                when energySpent.Actor.CombatId == localCombatId:
                stats.EnergySpent += Math.Max(0, energySpent.Amount);
                break;
            case PowerReceivedEntry powerReceived
                when powerReceived.Applier?.CombatId == localCombatId:
            {
                string powerId = powerReceived.Power.Id.Entry;
                string category = ResolvePowerCategory(powerId);
                if (category == "buff")
                {
                    stats.BuffsApplied += 1;
                    IncrementCount(stats.BuffCounts, powerId);
                }
                else if (category == "debuff")
                {
                    stats.DebuffsApplied += 1;
                    IncrementCount(stats.DebuffCounts, powerId);
                }
                break;
            }
        }
    }

    private static void ProcessHistoryEntryForContribution(
        CombatHistoryEntry entry,
        CombatState state,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        switch (entry)
        {
            case PowerReceivedEntry powerReceived:
                TrackPowerSource(powerReceived, playersByCombatId);
                TrackStatusApplication(powerReceived, modeKey, playersByCombatId);
                break;
            case DamageReceivedEntry damageReceived:
                TrackDynamicDamageContribution(damageReceived, modeKey, playersByCombatId);
                break;
            case BlockGainedEntry blockGained:
                TrackTeamBlockContribution(blockGained, modeKey, playersByCombatId);
                break;
        }
    }

    private static Dictionary<ulong, ContributionAccumulator> EnsureModeContributionBucket(string modeKey)
    {
        if (_modeContributions.TryGetValue(modeKey, out Dictionary<ulong, ContributionAccumulator>? bucket))
        {
            return bucket;
        }

        bucket = new Dictionary<ulong, ContributionAccumulator>();
        _modeContributions[modeKey] = bucket;
        return bucket;
    }

    private static ContributionAccumulator EnsureCombatContribution(
        uint combatId,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        if (_combatContributions.TryGetValue(combatId, out ContributionAccumulator? existing))
        {
            if (playersByCombatId.TryGetValue(combatId, out Player? player))
            {
                bool hadNetId = existing.NetId != 0;
                existing.NetId = player.NetId;
                existing.Name = ResolvePlayerDisplayName(player, combatId);
                if (!hadNetId && existing.NetId != 0 && !existing.HasTotalsMirror)
                {
                    BackfillContributionToAggregateBuckets(existing, _activeBattle?.ModeKey);
                }
            }

            return existing;
        }

        ContributionAccumulator created;
        if (playersByCombatId.TryGetValue(combatId, out Player? sourcePlayer))
        {
            created = new ContributionAccumulator
            {
                NetId = sourcePlayer.NetId,
                Name = ResolvePlayerDisplayName(sourcePlayer, combatId),
                HasTotalsMirror = true
            };
        }
        else
        {
            created = new ContributionAccumulator
            {
                Name = "玩家#" + combatId.ToString(CultureInfo.InvariantCulture)
            };
        }

        _combatContributions[combatId] = created;
        return created;
    }

    private static void BackfillContributionToAggregateBuckets(ContributionAccumulator combat, string? modeKey)
    {
        if (combat.NetId == 0 || combat.HasTotalsMirror)
        {
            return;
        }

        ContributionAccumulator total = EnsureTotalContribution(_totalContributions, combat.NetId, combat.Name);
        total.Ndps += combat.Ndps;
        total.SupportDamage += combat.SupportDamage;
        total.Mitigation += combat.Mitigation;
        total.TeamBlock += combat.TeamBlock;
        total.StatusApplications += combat.StatusApplications;
        total.OffensiveDebuffsApplied += combat.OffensiveDebuffsApplied;
        total.DefensiveBuffsApplied += combat.DefensiveBuffsApplied;

        string aggregateModeKey = string.IsNullOrWhiteSpace(modeKey) ? "singleplayer" : modeKey;
        Dictionary<ulong, ContributionAccumulator> modeBucket = EnsureModeContributionBucket(aggregateModeKey);
        ContributionAccumulator mode = EnsureTotalContribution(modeBucket, combat.NetId, combat.Name);
        mode.Ndps += combat.Ndps;
        mode.SupportDamage += combat.SupportDamage;
        mode.Mitigation += combat.Mitigation;
        mode.TeamBlock += combat.TeamBlock;
        mode.StatusApplications += combat.StatusApplications;
        mode.OffensiveDebuffsApplied += combat.OffensiveDebuffsApplied;
        mode.DefensiveBuffsApplied += combat.DefensiveBuffsApplied;

        combat.HasTotalsMirror = true;
    }

    private static ContributionAccumulator EnsureTotalContribution(
        IDictionary<ulong, ContributionAccumulator> bucket,
        ulong netId,
        string name)
    {
        if (bucket.TryGetValue(netId, out ContributionAccumulator? existing))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name;
            }

            return existing;
        }

        ContributionAccumulator created = new()
        {
            NetId = netId,
            Name = name
        };
        bucket[netId] = created;
        return created;
    }

    private static void AddContribution(
        uint actorCombatId,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId,
        double ndps = 0,
        double support = 0,
        double mitigation = 0,
        double teamBlock = 0,
        int statusApplications = 0,
        int offensiveDebuffs = 0,
        int defensiveBuffs = 0)
    {
        ContributionAccumulator combat = EnsureCombatContribution(actorCombatId, playersByCombatId);
        combat.Ndps += ndps;
        combat.SupportDamage += support;
        combat.Mitigation += mitigation;
        combat.TeamBlock += teamBlock;
        combat.StatusApplications += statusApplications;
        combat.OffensiveDebuffsApplied += offensiveDebuffs;
        combat.DefensiveBuffsApplied += defensiveBuffs;

        if (combat.NetId == 0)
        {
            return;
        }

        ContributionAccumulator total = EnsureTotalContribution(_totalContributions, combat.NetId, combat.Name);
        total.Ndps += ndps;
        total.SupportDamage += support;
        total.Mitigation += mitigation;
        total.TeamBlock += teamBlock;
        total.StatusApplications += statusApplications;
        total.OffensiveDebuffsApplied += offensiveDebuffs;
        total.DefensiveBuffsApplied += defensiveBuffs;

        Dictionary<ulong, ContributionAccumulator> modeBucket = EnsureModeContributionBucket(modeKey);
        ContributionAccumulator mode = EnsureTotalContribution(modeBucket, combat.NetId, combat.Name);
        mode.Ndps += ndps;
        mode.SupportDamage += support;
        mode.Mitigation += mitigation;
        mode.TeamBlock += teamBlock;
        mode.StatusApplications += statusApplications;
        mode.OffensiveDebuffsApplied += offensiveDebuffs;
        mode.DefensiveBuffsApplied += defensiveBuffs;
    }

    private static string ResolvePlayerDisplayName(Player player, uint combatId)
    {
        string name = player.Creature.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return "玩家#" + combatId.ToString(CultureInfo.InvariantCulture);
    }

    private static uint? ResolveContributorCombatId(
        Creature? creature,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        if (creature == null)
        {
            return null;
        }

        uint? ownCombatId = creature.CombatId;
        if (ownCombatId.HasValue && playersByCombatId.ContainsKey(ownCombatId.Value))
        {
            return ownCombatId.Value;
        }

        uint? ownerCombatId = creature.PetOwner?.Creature?.CombatId;
        if (ownerCombatId.HasValue && playersByCombatId.ContainsKey(ownerCombatId.Value))
        {
            return ownerCombatId.Value;
        }

        return null;
    }

    private static void TrackPowerSource(
        PowerReceivedEntry powerReceived,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        Creature? receiver = powerReceived.Actor;
        PowerModel? power = powerReceived.Power;
        if (receiver == null || power == null)
        {
            return;
        }

        uint? receiverId = receiver.CombatId;
        string powerId = power.Id.Entry;
        if (!receiverId.HasValue || string.IsNullOrWhiteSpace(powerId))
        {
            return;
        }

        PowerSourceKey key = new()
        {
            ReceiverCombatId = receiverId.Value,
            PowerId = powerId
        };

        uint? applierCombatId = ResolveContributorCombatId(powerReceived.Applier, playersByCombatId);
        ulong? applierNetId = null;
        string applierName = string.Empty;
        if (applierCombatId.HasValue && playersByCombatId.TryGetValue(applierCombatId.Value, out Player? applierPlayer))
        {
            applierNetId = applierPlayer.NetId;
            applierName = ResolvePlayerDisplayName(applierPlayer, applierCombatId.Value);
        }

        _powerSourceTracker[key] = new PowerSourceRecord
        {
            ApplierCombatId = applierCombatId,
            ApplierNetId = applierNetId,
            ApplierName = applierName,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static void TrackStatusApplication(
        PowerReceivedEntry powerReceived,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        uint? applierCombatId = ResolveContributorCombatId(powerReceived.Applier, playersByCombatId);
        if (!applierCombatId.HasValue || !playersByCombatId.ContainsKey(applierCombatId.Value))
        {
            return;
        }

        string powerType = powerReceived.Power.Type.ToString();
        uint? receiverContributorCombatId = ResolveContributorCombatId(powerReceived.Actor, playersByCombatId);
        bool isDebuffOnEnemy = powerReceived.Actor.Side == CombatSide.Enemy
            && powerType.Contains("Debuff", StringComparison.OrdinalIgnoreCase);
        bool isBuffOnAlly = powerReceived.Actor.Side == CombatSide.Player
            && powerType.Contains("Buff", StringComparison.OrdinalIgnoreCase)
            && receiverContributorCombatId.HasValue
            && receiverContributorCombatId.Value != applierCombatId.Value;

        if (!isDebuffOnEnemy && !isBuffOnAlly)
        {
            return;
        }

        AddContribution(
            applierCombatId.Value,
            modeKey,
            playersByCombatId,
            statusApplications: 1,
            offensiveDebuffs: isDebuffOnEnemy ? 1 : 0,
            defensiveBuffs: isBuffOnAlly ? 1 : 0);
    }

    private static void TrackTeamBlockContribution(
        BlockGainedEntry blockGained,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        if (blockGained.Amount <= 0 || blockGained.Receiver.Side != CombatSide.Player)
        {
            return;
        }

        uint? receiverCombatId = ResolveContributorCombatId(blockGained.Receiver, playersByCombatId);
        uint? sourceCombatId = ResolveBlockSourceCombatId(blockGained, playersByCombatId);
        if (!sourceCombatId.HasValue || !receiverCombatId.HasValue)
        {
            return;
        }

        if (sourceCombatId.Value == receiverCombatId.Value)
        {
            return;
        }

        if (!playersByCombatId.ContainsKey(sourceCombatId.Value))
        {
            return;
        }

        AddContribution(
            sourceCombatId.Value,
            modeKey,
            playersByCombatId,
            teamBlock: blockGained.Amount);
    }

    private static uint? ResolveBlockSourceCombatId(
        BlockGainedEntry blockGained,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        uint? cardSourceCombatId = ResolveContributorCombatId(blockGained.CardPlay?.Card?.Owner?.Creature, playersByCombatId);
        if (cardSourceCombatId.HasValue)
        {
            return cardSourceCombatId;
        }

        Creature? receiver = blockGained.Receiver;
        uint? receiverCombatId = ResolveContributorCombatId(receiver, playersByCombatId);
        uint? receiverRawCombatId = receiver?.CombatId;
        if (receiver == null || !receiverCombatId.HasValue || !receiverRawCombatId.HasValue)
        {
            return null;
        }

        List<uint> teammateAppliers = receiver.Powers
            .Select(power =>
            {
                _powerSourceTracker.TryGetValue(new PowerSourceKey
                {
                    ReceiverCombatId = receiverRawCombatId.Value,
                    PowerId = power.Id.Entry
                }, out PowerSourceRecord? source);
                return source?.ApplierCombatId;
            })
            .Where(static applierCombatId => applierCombatId.HasValue)
            .Select(static applierCombatId => applierCombatId!.Value)
            .Where(applierCombatId =>
                applierCombatId != receiverCombatId.Value
                && playersByCombatId.ContainsKey(applierCombatId))
            .Distinct()
            .ToList();

        return teammateAppliers.Count == 1 ? teammateAppliers[0] : null;
    }

    private static void TrackDynamicDamageContribution(
        DamageReceivedEntry damageReceived,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        int observedDamage = Math.Max(0, damageReceived.Result.TotalDamage);
        Creature? dealer = damageReceived.Dealer;
        if (observedDamage <= 0 || dealer == null)
        {
            return;
        }

        uint? dealerRawCombatId = dealer.CombatId;
        if (!dealerRawCombatId.HasValue)
        {
            return;
        }

        if (dealer.Side == CombatSide.Player && damageReceived.Receiver.Side == CombatSide.Enemy)
        {
            uint? dealerCombatId = ResolveContributorCombatId(dealer, playersByCombatId);
            if (!dealerCombatId.HasValue)
            {
                return;
            }

            List<ResolvedPowerModifier> targetDebuffModifiers = ResolveDynamicDamageMultipliers(
                damageReceived.Receiver,
                damageReceived.Receiver,
                dealer,
                damageReceived.CardSource,
                damageReceived.Result.Props,
                observedDamage,
                playersByCombatId)
                .Where(m =>
                    m.Multiplier > 1.0001
                    && m.ApplierCombatId.HasValue
                    && m.ApplierCombatId.Value != dealerCombatId.Value
                    && playersByCombatId.ContainsKey(m.ApplierCombatId.Value))
                .ToList();

            double teammateMultiplier = targetDebuffModifiers.Aggregate(1d, static (current, m) => current * m.Multiplier);
            teammateMultiplier = Math.Max(1d, teammateMultiplier);
            double ndps = observedDamage / teammateMultiplier;
            double supportDamage = Math.Max(0, observedDamage - ndps);
            AddContribution(
                dealerCombatId.Value,
                modeKey,
                playersByCombatId,
                ndps: ndps);

            DistributeContributionByMultiplierWeight(
                targetDebuffModifiers,
                supportDamage,
                modeKey,
                playersByCombatId,
                isMitigation: false);
            return;
        }

        if (dealer.Side != CombatSide.Enemy || damageReceived.Receiver.Side != CombatSide.Player)
        {
            return;
        }

        List<ResolvedPowerModifier> outgoingDebuffModifiers = ResolveDynamicDamageMultipliers(
            dealer,
            damageReceived.Receiver,
            dealer,
            damageReceived.CardSource,
            damageReceived.Result.Props,
            observedDamage,
            playersByCombatId)
            .Where(m =>
                m.Multiplier < 0.9999
                && m.ApplierCombatId.HasValue
                && playersByCombatId.ContainsKey(m.ApplierCombatId.Value))
            .ToList();

        if (outgoingDebuffModifiers.Count == 0)
        {
            return;
        }

        double teamDebuffMultiplier = outgoingDebuffModifiers.Aggregate(1d, static (current, m) => current * m.Multiplier);
        if (teamDebuffMultiplier <= 0 || teamDebuffMultiplier >= 1d)
        {
            return;
        }

        double noDebuffDamage = observedDamage / teamDebuffMultiplier;
        double mitigation = Math.Max(0, noDebuffDamage - observedDamage);
        DistributeContributionByMultiplierWeight(
            outgoingDebuffModifiers,
            mitigation,
            modeKey,
            playersByCombatId,
            isMitigation: true);
    }

    private static void DistributeContributionByMultiplierWeight(
        IReadOnlyList<ResolvedPowerModifier> modifiers,
        double totalContribution,
        string modeKey,
        IReadOnlyDictionary<uint, Player> playersByCombatId,
        bool isMitigation)
    {
        if (totalContribution <= 0 || modifiers.Count == 0)
        {
            return;
        }

        List<(uint applier, double weight)> weighted = new();
        double totalWeight = 0;
        foreach (ResolvedPowerModifier modifier in modifiers)
        {
            if (!modifier.ApplierCombatId.HasValue)
            {
                continue;
            }

            uint applierId = modifier.ApplierCombatId.Value;
            if (!playersByCombatId.ContainsKey(applierId))
            {
                continue;
            }

            double weight = modifier.Multiplier > 1
                ? Math.Log(modifier.Multiplier)
                : Math.Abs(Math.Log(Math.Max(0.0001, modifier.Multiplier)));
            if (weight <= 0)
            {
                continue;
            }

            weighted.Add((applierId, weight));
            totalWeight += weight;
        }

        if (weighted.Count == 0)
        {
            return;
        }

        if (totalWeight <= 0)
        {
            totalWeight = weighted.Count;
            weighted = weighted
                .Select(static pair => (pair.applier, weight: 1d))
                .ToList();
        }

        foreach ((uint applier, double weight) in weighted)
        {
            double amount = totalContribution * weight / totalWeight;
            if (isMitigation)
            {
                AddContribution(
                    applier,
                    modeKey,
                    playersByCombatId,
                    mitigation: amount);
            }
            else
            {
                AddContribution(
                    applier,
                    modeKey,
                    playersByCombatId,
                    support: amount);
            }
        }
    }

    private static List<ResolvedPowerModifier> ResolveDynamicDamageMultipliers(
        Creature powerOwner,
        Creature target,
        Creature? dealer,
        CardModel? cardSource,
        ValueProp props,
        int observedDamage,
        IReadOnlyDictionary<uint, Player> playersByCombatId)
    {
        List<ResolvedPowerModifier> resolved = new();
        uint? ownerCombatId = powerOwner.CombatId;
        if (!ownerCombatId.HasValue)
        {
            return resolved;
        }

        decimal probeAmount = Math.Max(1, observedDamage);
        foreach (PowerModel power in powerOwner.Powers)
        {
            decimal multiplier;
            try
            {
                multiplier = power.ModifyDamageMultiplicative(target, probeAmount, props, dealer, cardSource);
            }
            catch
            {
                continue;
            }

            if (multiplier <= 0m || multiplier == 1m)
            {
                continue;
            }

            PowerSourceRecord? source = null;
            PowerSourceKey key = new()
            {
                ReceiverCombatId = ownerCombatId.Value,
                PowerId = power.Id.Entry
            };
            _powerSourceTracker.TryGetValue(key, out source);

            uint? applierCombatId = source?.ApplierCombatId;
            ulong? applierNetId = source?.ApplierNetId;
            string applierName = source?.ApplierName ?? string.Empty;
            if (applierCombatId.HasValue && playersByCombatId.TryGetValue(applierCombatId.Value, out Player? applier))
            {
                applierNetId = applier.NetId;
                applierName = ResolvePlayerDisplayName(applier, applierCombatId.Value);
            }

            resolved.Add(new ResolvedPowerModifier
            {
                OwnerCombatId = ownerCombatId.Value,
                ApplierCombatId = applierCombatId,
                ApplierNetId = applierNetId,
                ApplierName = applierName,
                PowerId = power.Id.Entry,
                Multiplier = (double)multiplier
            });
        }

        return resolved;
    }

    private static void PrunePowerSourceTracker(CombatState state)
    {
        if (_powerSourceTracker.Count == 0)
        {
            return;
        }

        List<PowerSourceKey> staleKeys = new();
        foreach ((PowerSourceKey key, _) in _powerSourceTracker)
        {
            Creature? receiver = state.GetCreature(key.ReceiverCombatId);
            if (receiver == null || !receiver.IsAlive)
            {
                staleKeys.Add(key);
                continue;
            }

            bool powerStillActive = receiver.Powers.Any(power => string.Equals(power.Id.Entry, key.PowerId, StringComparison.Ordinal));
            if (!powerStillActive)
            {
                staleKeys.Add(key);
            }
        }

        foreach (PowerSourceKey key in staleKeys)
        {
            _powerSourceTracker.Remove(key);
        }
    }

    private static List<PlayerContributionSnapshot> BuildContributionBoardSnapshot(IEnumerable<ContributionAccumulator> source)
    {
        List<PlayerContributionSnapshot> rows = source
            .Select(acc =>
            {
                double statusScore = acc.StatusApplications * 3.0
                    + acc.OffensiveDebuffsApplied * 1.8
                    + acc.DefensiveBuffsApplied * 1.2
                    + acc.SupportDamage * 0.12
                    + acc.Mitigation * 0.16;
                double contributionScore = acc.Ndps
                    + acc.SupportDamage
                    + acc.Mitigation
                    + acc.TeamBlock * 0.75
                    + statusScore;

                return new PlayerContributionSnapshot
                {
                    NetId = acc.NetId,
                    Name = acc.Name,
                    Ndps = Math.Round(acc.Ndps, 2),
                    SupportDamage = Math.Round(acc.SupportDamage, 2),
                    Mitigation = Math.Round(acc.Mitigation, 2),
                    TeamBlock = Math.Round(acc.TeamBlock, 2),
                    StatusScore = Math.Round(statusScore, 2),
                    StatusApplications = acc.StatusApplications,
                    OffensiveDebuffsApplied = acc.OffensiveDebuffsApplied,
                    DefensiveBuffsApplied = acc.DefensiveBuffsApplied,
                    ContributionScore = Math.Round(contributionScore, 2)
                };
            })
            .OrderByDescending(static row => row.ContributionScore)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ToList();

        double total = rows.Sum(static row => row.ContributionScore);
        foreach (PlayerContributionSnapshot row in rows)
        {
            row.Ratio = total <= 0 ? 0 : Math.Clamp(row.ContributionScore / total, 0, 1);
        }

        return rows;
    }

    private static CardStatAccumulator GetOrCreateCardStats(StatsAccumulator stats, string cardId)
    {
        if (stats.CardStats.TryGetValue(cardId, out CardStatAccumulator? existing))
        {
            return existing;
        }

        CardStatAccumulator created = new()
        {
            CardId = cardId
        };
        stats.CardStats[cardId] = created;
        return created;
    }

    private static void IncrementCount(IDictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out int current))
        {
            counts[key] = current + 1;
            return;
        }

        counts[key] = 1;
    }

    private static string ResolvePowerCategory(string powerId)
    {
        if (!_powerTypeCache.TryGetValue(powerId, out string? powerType))
        {
            PowerModel? model = ModelDb.AllPowers.FirstOrDefault(p => string.Equals(p.Id.Entry, powerId, StringComparison.Ordinal));
            powerType = model?.Type.ToString() ?? string.Empty;
            _powerTypeCache[powerId] = powerType;
        }

        string lower = powerType.ToLowerInvariant();
        if (lower.Contains("debuff", StringComparison.Ordinal) || lower.Contains("negative", StringComparison.Ordinal))
        {
            return "debuff";
        }

        if (lower.Contains("buff", StringComparison.Ordinal) || lower.Contains("positive", StringComparison.Ordinal))
        {
            return "buff";
        }

        return "other";
    }

    private static CombatMetricsSnapshot BuildMetricsSnapshot(StatsAccumulator stats)
    {
        int totalCardPlays = Math.Max(0, stats.CardsPlayed);
        List<CardMetricSnapshot> topCards = stats.CardStats.Values
            .Select(card =>
            {
                double usageRate = totalCardPlays <= 0 ? 0 : (double)card.Played / totalCardPlays;
                double efficiency = card.EnergySpent <= 0 ? 0 : (double)card.DamageDealt / card.EnergySpent;
                return new CardMetricSnapshot
                {
                    CardId = card.CardId,
                    Played = card.Played,
                    UsageRate = Math.Round(usageRate, 4),
                    DamageDealt = card.DamageDealt,
                    BlockGained = card.BlockGained,
                    OverkillDamage = card.OverkillDamage,
                    EnergySpent = card.EnergySpent,
                    DamagePerEnergy = Math.Round(efficiency, 4)
                };
            })
            .OrderByDescending(static card => card.Played)
            .ThenByDescending(static card => card.DamageDealt)
            .ThenBy(static card => card.CardId, StringComparer.Ordinal)
            .Take(15)
            .ToList();

        List<PowerCountSnapshot> topBuffs = stats.BuffCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(static pair => new PowerCountSnapshot
            {
                PowerId = pair.Key,
                Count = pair.Value
            })
            .ToList();

        List<PowerCountSnapshot> topDebuffs = stats.DebuffCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(static pair => new PowerCountSnapshot
            {
                PowerId = pair.Key,
                Count = pair.Value
            })
            .ToList();

        return new CombatMetricsSnapshot
        {
            DamageDealt = stats.DamageDealt,
            DamageTaken = stats.DamageTaken,
            DamageBlocked = stats.DamageBlocked,
            BlockGained = stats.BlockGained,
            EnergySpent = stats.EnergySpent,
            OverkillDamage = stats.OverkillDamage,
            CardsPlayed = stats.CardsPlayed,
            BuffsApplied = stats.BuffsApplied,
            DebuffsApplied = stats.DebuffsApplied,
            UniqueBuffKinds = stats.BuffCounts.Count,
            UniqueDebuffKinds = stats.DebuffCounts.Count,
            DamagePerEnergy = Math.Round(SafeDivide(stats.DamageDealt, stats.EnergySpent), 4),
            TempoScore = stats.DamageDealt - stats.DamageTaken,
            StatusScore = Math.Round(stats.BuffsApplied * 1.2 + stats.DebuffsApplied * 1.5 + stats.BuffCounts.Count * 0.8 + stats.DebuffCounts.Count * 1.0, 3),
            TopCards = topCards,
            TopBuffs = topBuffs,
            TopDebuffs = topDebuffs
        };
    }

    private static List<CombatLogLineSnapshot> BuildRecentCombatLog(CombatHistorySnapshot combatHistory)
    {
        return combatHistory.RecentEntries
            .TakeLast(MaxDashboardLogEntries)
            .Select(entry =>
            {
                string details = string.Join(
                    ", ",
                    entry.Details.Take(4).Select(static pair => pair.Key + "=" + pair.Value));
                return new CombatLogLineSnapshot
                {
                    Index = entry.Index,
                    EntryType = entry.EntryType,
                    Actor = entry.ActorName,
                    Description = entry.HumanReadable,
                    Details = details
                };
            })
            .ToList();
    }

    private static AnalyticsHighlightsSnapshot BuildAnalyticsHighlights(
        CombatState state,
        Player? localPlayer,
        CombatMetricsSnapshot currentCombat,
        string modeKey)
    {
        int incomingDamage = EstimateIncomingDamage(state);
        int hp = localPlayer?.Creature.CurrentHp ?? 0;
        int block = localPlayer?.Creature.Block ?? 0;
        int unblockedThreat = Math.Max(0, incomingDamage - block);

        string dangerLevel = "low";
        if (unblockedThreat >= hp && hp > 0)
        {
            dangerLevel = "lethal";
        }
        else if (unblockedThreat >= Math.Max(1, hp / 2))
        {
            dangerLevel = "high";
        }
        else if (unblockedThreat > 0)
        {
            dangerLevel = "medium";
        }

        string? mvpCard = currentCombat.TopCards.FirstOrDefault()?.CardId;
        int recentVictories = _battleSegments
            .TakeLast(8)
            .Count(static b => string.Equals(b.Result, "victory", StringComparison.Ordinal));

        return new AnalyticsHighlightsSnapshot
        {
            Mode = modeKey,
            IncomingDamageEstimate = incomingDamage,
            UnblockedThreatEstimate = unblockedThreat,
            DangerLevel = dangerLevel,
            CurrentTempo = currentCombat.TempoScore,
            MvpCard = mvpCard,
            RecentWinCount = recentVictories,
            RecentBattleCount = Math.Min(8, _battleSegments.Count)
        };
    }

    private static int EstimateIncomingDamage(CombatState state)
    {
        int sum = 0;
        foreach (Creature enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster?.NextMove?.Intents == null)
            {
                continue;
            }

            foreach (AbstractIntent intent in enemy.Monster.NextMove.Intents)
            {
                string? label = null;
                try
                {
                    label = intent.GetIntentLabel(state.PlayerCreatures, enemy).GetFormattedText();
                }
                catch
                {
                    label = null;
                }

                sum += ParseFirstInteger(label);
            }
        }

        return sum;
    }

    private static int ParseFirstInteger(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return 0;
        }

        int end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        string token = text[start..end];
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return 0;
    }

    private static DashboardSnapshot BuildDashboardSnapshot(
        CombatState state,
        Player? localPlayer,
        AnalyticsSnapshot analytics,
        RoutePlanSnapshot routePlan,
        CombatHistorySnapshot combatHistory)
    {
        List<string> funInsights = BuildFunInsights(state, analytics, combatHistory, routePlan);
        DashboardSnapshot dashboard = new()
        {
            ModId = ModId,
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = analytics.Mode,
            IsInCombat = analytics.IsInCombat,
            CurrentCombat = analytics.CurrentCombat,
            Total = analytics.Total,
            SingleplayerTotal = analytics.Singleplayer.Total,
            MultiplayerTotal = analytics.Multiplayer.Total,
            RecentBattles = analytics.RecentBattles.Take(10).ToList(),
            RecentCombatLog = analytics.RecentCombatLog,
            RoutePlan = routePlan,
            RouteHistory = routePlan.HistoricalStats,
            Summary = BuildDashboardSummary(analytics, localPlayer),
            Alerts = BuildDashboardAlerts(analytics),
            FunInsights = funInsights
        };

        return dashboard;
    }

    private static string BuildDashboardSummary(AnalyticsSnapshot analytics, Player? localPlayer)
    {
        int hp = localPlayer?.Creature.CurrentHp ?? 0;
        int maxHp = localPlayer?.Creature.MaxHp ?? 0;
        int energy = localPlayer?.PlayerCombatState?.Energy ?? 0;
        CombatMetricsSnapshot current = analytics.CurrentCombat;
        return $"生命 {hp}/{maxHp}，能量 {energy}，伤害 {current.DamageDealt}，格挡 {current.BlockGained}，状态分 {current.StatusScore:F1}，出牌 {current.CardsPlayed}";
    }

    private static List<string> BuildDashboardAlerts(AnalyticsSnapshot analytics)
    {
        List<string> alerts = new();
        if (analytics.Highlights.DangerLevel == "lethal")
        {
            alerts.Add("本回合存在致死风险，优先防守或减伤。");
        }

        if (analytics.CurrentCombat.EnergySpent > 0 && analytics.CurrentCombat.DamagePerEnergy < 1.0)
        {
            alerts.Add("当前能量效率偏低，优先寻找高收益出牌。");
        }

        if (analytics.CurrentCombat.CardsPlayed > 0 && analytics.CurrentCombat.BuffsApplied + analytics.CurrentCombat.DebuffsApplied == 0)
        {
            alerts.Add("本战斗尚未建立增益/减益节奏。");
        }

        return alerts;
    }

    private static List<string> BuildFunInsights(
        CombatState state,
        AnalyticsSnapshot analytics,
        CombatHistorySnapshot combatHistory,
        RoutePlanSnapshot routePlan)
    {
        List<string> insights = new();
        CardMetricSnapshot? topCard = analytics.CurrentCombat.TopCards.FirstOrDefault();
        if (topCard != null && topCard.Played >= 2)
        {
            insights.Add($"本战斗最常用卡是 {topCard.CardId}，使用 {topCard.Played} 次。");
        }

        if (analytics.Total.CardsPlayed > 0)
        {
            insights.Add($"总计能量效率 {analytics.Total.DamagePerEnergy:F2} 伤害/能量。");
        }

        int uniqueEntryTypes = combatHistory.RecentEntries
            .Select(static e => e.EntryType)
            .Distinct(StringComparer.Ordinal)
            .Count();
        insights.Add($"最近战斗日志类型数：{uniqueEntryTypes}。");

        if (state.Players.Count > 1)
        {
            insights.Add("当前处于联机战斗统计通道。");
        }
        else
        {
            insights.Add("当前处于单机战斗统计通道。");
        }

        if (routePlan.HistoricalStats.TotalVisited > 0)
        {
            if (!string.IsNullOrEmpty(routePlan.HistoricalStats.MostVisitedLabel))
            {
                insights.Add($"路线最多节点类型：{routePlan.HistoricalStats.MostVisitedLabel}。");
            }

            if (!string.IsNullOrEmpty(routePlan.HistoricalStats.BestTypeLabel))
            {
                insights.Add($"路线收益最佳类型：{routePlan.HistoricalStats.BestTypeLabel}。");
            }
        }

        return insights;
    }

    private static OverlayPayload BuildOverlayPayload(
        CombatState state,
        IReadOnlyList<CombatHistoryEntry> historyEntries,
        Player? localPlayer,
        AnalyticsSnapshot analytics,
        RoutePlanSnapshot routePlan,
        CombatHistorySnapshot combatHistory)
    {
        string combatText = BuildOverlayCombatText(localPlayer, analytics);
        string totalText = BuildOverlayTotalText(analytics);
        string routeText = BuildOverlayRouteText(routePlan);
        string logsText = BuildOverlayLogsText(combatHistory);
        return new OverlayPayload
        {
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = analytics.Mode,
            IsInCombat = analytics.IsInCombat,
            Summary = BuildDashboardSummary(analytics, localPlayer),
            CombatPanel = combatText,
            TotalPanel = totalText,
            RoutePanel = routeText,
            LogsPanel = logsText,
            CombatBoardScope = analytics.IsInCombat ? "当前战斗" : "最近一场",
            CombatDamageBoard = BuildOverlayContributionBoard(analytics, localPlayer),
            RouteLegendEntries = BuildOverlayRouteLegendEntries(routePlan),
            RouteOptions = BuildOverlayRouteOptions(routePlan),
            SelectedRouteOptionIndex = -1,
            ModifierState = BuildOverlayModifierState(localPlayer),
            Alerts = BuildDashboardAlerts(analytics)
        };
    }

    private static List<OverlayRouteLegendEntry> BuildOverlayRouteLegendEntries(RoutePlanSnapshot routePlan)
    {
        List<OverlayRouteLegendEntry> entries = new();
        foreach (RouteReachableTypeSnapshot type in routePlan.ReachableByType)
        {
            entries.Add(new OverlayRouteLegendEntry
            {
                TypeKey = type.TypeKey,
                Count = type.Count,
                Label = $"{type.Label} {type.Count}"
            });
        }

        return entries;
    }

    private static AnalyticsSnapshot BuildFallbackAnalytics(string modeKey)
    {
        StatsAccumulator singleTotal = _modeTotals.TryGetValue("singleplayer", out StatsAccumulator? s) ? s : new StatsAccumulator();
        StatsAccumulator multiTotal = _modeTotals.TryGetValue("multiplayer", out StatsAccumulator? m) ? m : new StatsAccumulator();

        return new AnalyticsSnapshot
        {
            ModId = ModId,
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = modeKey,
            IsInCombat = false,
            CurrentCombat = new CombatMetricsSnapshot(),
            Total = BuildMetricsSnapshot(_totalStats),
            Singleplayer = new ModeMetricsSnapshot
            {
                Mode = "singleplayer",
                Total = BuildMetricsSnapshot(singleTotal),
                RecentBattles = new List<BattleSegmentSnapshot>()
            },
            Multiplayer = new ModeMetricsSnapshot
            {
                Mode = "multiplayer",
                Total = BuildMetricsSnapshot(multiTotal),
                RecentBattles = new List<BattleSegmentSnapshot>()
            },
            RecentBattles = new List<BattleSegmentSnapshot>(),
            RecentCombatLog = new List<CombatLogLineSnapshot>(),
            Highlights = new AnalyticsHighlightsSnapshot
            {
                Mode = modeKey,
                DangerLevel = "low"
            },
            RouteHistory = new RouteHistoryStatsSnapshot(),
            CurrentContributionBoard = new List<PlayerContributionSnapshot>(),
            TotalContributionBoard = BuildContributionBoardSnapshot(_totalContributions.Values)
        };
    }

    private static List<OverlayRouteOption> BuildOverlayRouteOptions(RoutePlanSnapshot routePlan)
    {
        List<OverlayRouteOption> options = new();
        int index = 0;
        foreach (RouteOptionSnapshot option in routePlan.Options)
        {
            OverlayRouteOption row = new()
            {
                OptionIndex = index,
                Label = $"路线{index + 1}",
                PointTypeLabel = option.PointTypeLabel,
                Score = option.Score,
                RiskTag = option.RiskTag,
                PathTypeKeys = option.PathTypeKeys.ToList(),
                PathCoords = option.PathCoords
                    .Select(static p => new OverlayRouteCoord
                    {
                        Row = p.Row,
                        Col = p.Col
                    })
                    .ToList()
            };
            options.Add(row);
            index++;
        }

        return options;
    }

    private static List<OverlayDamageEntry> BuildOverlayContributionBoard(AnalyticsSnapshot analytics, Player? localPlayer)
    {
        List<PlayerContributionSnapshot> board = analytics.CurrentContributionBoard ?? new List<PlayerContributionSnapshot>();
        ulong localNetId = localPlayer?.NetId ?? 0;
        return board
            .Select(row => new OverlayDamageEntry
            {
                Name = row.Name,
                ContributionScore = row.ContributionScore,
                Ndps = row.Ndps,
                SupportDamage = row.SupportDamage,
                Mitigation = row.Mitigation,
                TeamBlock = row.TeamBlock,
                StatusScore = row.StatusScore,
                Ratio = row.Ratio,
                IsLocalPlayer = localNetId != 0 && row.NetId == localNetId
            })
            .OrderByDescending(static row => row.ContributionScore)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static OverlayModifierState BuildOverlayModifierState(Player? localPlayer)
    {
        OverlayModifierState state = new();
        if (localPlayer == null)
        {
            return state;
        }

        state.PlayerName = ResolvePlayerDisplayName(localPlayer, localPlayer.Creature?.CombatId ?? 0);
        state.CharacterId = localPlayer.Character?.Id.Entry ?? string.Empty;
        state.CurrentHp = localPlayer.Creature?.CurrentHp ?? 0;
        state.MaxHp = localPlayer.Creature?.MaxHp ?? 0;
        state.Gold = localPlayer.Gold;
        state.RelicCount = localPlayer.Relics.Count;
        state.DeckCount = localPlayer.Deck.Cards.Count;
        state.Relics = localPlayer.Relics
            .Select(static relic => new OverlayModifierOwnedRelic
            {
                RelicId = relic.Id.Entry,
                Title = SafeText(() => relic.Title.GetFormattedText(), relic.Id.Entry)
            })
            .OrderBy(static relic => relic.Title, StringComparer.Ordinal)
            .ToList();
        state.DeckCards = localPlayer.Deck.Cards
            .Select((card, index) => new OverlayModifierDeckCardEntry
            {
                DeckIndex = index,
                CardId = card.Id.Entry,
                Title = SafeText(() => card.Title, card.Id.Entry),
                UpgradeLevel = card.CurrentUpgradeLevel,
                IsUpgradable = card.IsUpgradable
            })
            .ToList();
        return state;
    }

    internal static IReadOnlyList<OverlayModifierCatalogEntry> GetModifierCardCatalog()
    {
        lock (ModifierCatalogLock)
        {
            _cardCatalogCache ??= BuildModifierCardCatalog();
            return _cardCatalogCache;
        }
    }

    internal static IReadOnlyList<OverlayModifierCatalogEntry> GetModifierRelicCatalog()
    {
        lock (ModifierCatalogLock)
        {
            _relicCatalogCache ??= BuildModifierRelicCatalog();
            return _relicCatalogCache;
        }
    }

    public static void SetCurrentLocalPlayerHp(int hp)
    {
        _ = ExecuteModifierActionAsync(
            "set_hp",
            async player =>
            {
                int value = Math.Clamp(hp, 0, player.Creature.MaxHp);
                await CreatureCmd.SetCurrentHp(player.Creature, value);
                if (player.Creature.CurrentHp != value)
                {
                    player.Creature.SetCurrentHpInternal(value);
                }
                return $"当前生命已设置为 {value}/{player.Creature.MaxHp}";
            });
    }

    public static void SetCurrentLocalPlayerGold(int gold)
    {
        _ = ExecuteModifierActionAsync(
            "set_gold",
            async player =>
            {
                int value = Math.Max(0, gold);
                await PlayerCmd.SetGold(value, player);
                if (player.Gold != value)
                {
                    player.Gold = value;
                }
                return $"金币已设置为 {value}";
            });
    }

    public static void AddRelicToCurrentRun(string relicId)
    {
        _ = ExecuteModifierActionAsync(
            "add_relic",
            async player =>
            {
                RelicModel? canonical = ModelDb.AllRelics.FirstOrDefault(r =>
                    string.Equals(r.Id.Entry, relicId, StringComparison.OrdinalIgnoreCase));
                if (canonical == null)
                {
                    throw new InvalidOperationException("未找到遗物: " + relicId);
                }

                if (!canonical.IsStackable && player.GetRelicById(canonical.Id) != null)
                {
                    throw new InvalidOperationException("当前已拥有该遗物且不可叠加: " + canonical.Id.Entry);
                }

                RelicModel relic = canonical.ToMutable();
                await RelicCmd.Obtain(relic, player, player.Relics.Count);
                return $"已添加遗物 {SafeText(() => canonical.Title.GetFormattedText(), canonical.Id.Entry)}";
            });
    }

    public static void AddCardToCurrentDeck(string cardId)
    {
        _ = ExecuteModifierActionAsync(
            "add_card",
            async player =>
            {
                RunState runState = ResolveCurrentRunState()
                    ?? player.RunState as RunState
                    ?? throw new InvalidOperationException("当前 RunState 不可用。");
                CardModel? canonical = ModelDb.AllCards.FirstOrDefault(c =>
                    string.Equals(c.Id.Entry, cardId, StringComparison.OrdinalIgnoreCase));
                if (canonical == null)
                {
                    throw new InvalidOperationException("未找到卡牌: " + cardId);
                }

                CardModel card = runState.CreateCard(canonical, player);
                if (!runState.ContainsCard(card))
                {
                    runState.AddCard(card, player);
                }

                await CardPileCmd.Add(card, player.Deck, CardPilePosition.Bottom, player.Character, skipVisuals: true);
                return $"已加入卡牌 {SafeText(() => canonical.Title, canonical.Id.Entry)}";
            });
    }

    public static void UpgradeCurrentDeckCard(int deckIndex)
    {
        _ = ExecuteModifierActionAsync(
            "upgrade_card",
            player =>
            {
                if (deckIndex < 0 || deckIndex >= player.Deck.Cards.Count)
                {
                    throw new InvalidOperationException("卡牌索引超出范围: " + deckIndex.ToString(CultureInfo.InvariantCulture));
                }

                CardModel card = player.Deck.Cards[deckIndex];
                if (!card.IsUpgradable)
                {
                    throw new InvalidOperationException("该卡牌当前不可升级: " + card.Id.Entry);
                }

                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
                return Task.FromResult($"已升级卡牌 {SafeText(() => card.Title, card.Id.Entry)} 到 +{card.CurrentUpgradeLevel}");
            });
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

    private static string BuildOverlayCombatText(Player? localPlayer, AnalyticsSnapshot analytics)
    {
        int hp = localPlayer?.Creature.CurrentHp ?? 0;
        int maxHp = localPlayer?.Creature.MaxHp ?? 0;
        int block = localPlayer?.Creature.Block ?? 0;
        int energy = localPlayer?.PlayerCombatState?.Energy ?? 0;
        CombatMetricsSnapshot m = analytics.CurrentCombat;
        List<string> lines = new()
        {
            $"状态: {(analytics.IsInCombat ? "战斗中" : "非战斗")} | 模式: {ModeLabel(analytics.Mode)}",
            $"生命 {hp}/{maxHp} | 格挡 {block} | 能量 {energy}",
            $"来伤估计 {analytics.Highlights.IncomingDamageEstimate} | 未格挡威胁 {analytics.Highlights.UnblockedThreatEstimate}",
            $"伤害 {m.DamageDealt} | 受伤 {m.DamageTaken} | 挡下 {m.DamageBlocked} | 过量 {m.OverkillDamage}",
            $"打牌 {m.CardsPlayed} | 能量消耗 {m.EnergySpent} | 伤害效率 {m.DamagePerEnergy:F2} / 能量",
            $"增益施加 {m.BuffsApplied} ({m.UniqueBuffKinds} 种) | 减益施加 {m.DebuffsApplied} ({m.UniqueDebuffKinds} 种)",
            $"状态分 {m.StatusScore:F1} | 节奏分 {m.TempoScore}"
        };

        CardMetricSnapshot? top = m.TopCards.FirstOrDefault();
        if (top != null)
        {
            lines.Add($"本战 MVP: {top.CardId} (次数 {top.Played}, DPE {top.DamagePerEnergy:F2})");
        }

        List<string> alerts = BuildDashboardAlerts(analytics);
        if (alerts.Count > 0)
        {
            lines.Add("");
            lines.Add("提醒:");
            lines.AddRange(alerts.Take(3).Select(static alert => "- " + alert));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverlayTotalText(AnalyticsSnapshot analytics)
    {
        CombatMetricsSnapshot total = analytics.Total;
        CombatMetricsSnapshot single = analytics.Singleplayer.Total;
        CombatMetricsSnapshot multi = analytics.Multiplayer.Total;
        List<string> lines = new()
        {
            "总计",
            $"伤害 {total.DamageDealt} | 受伤 {total.DamageTaken} | 挡下 {total.DamageBlocked}",
            $"格挡获得 {total.BlockGained} | 能量消耗 {total.EnergySpent} | 过量伤害 {total.OverkillDamage}",
            $"打牌 {total.CardsPlayed} | 总伤害效率 {total.DamagePerEnergy:F2}",
            $"增益施加 {total.BuffsApplied} ({total.UniqueBuffKinds} 种) | 减益施加 {total.DebuffsApplied} ({total.UniqueDebuffKinds} 种)",
            $"状态分 {total.StatusScore:F1}",
            "",
            "单机累计",
            $"战斗样本 {analytics.Singleplayer.RecentBattles.Count} (近期) | 伤害效率 {single.DamagePerEnergy:F2} | 状态分 {single.StatusScore:F1}",
            "",
            "联机累计",
            $"战斗样本 {analytics.Multiplayer.RecentBattles.Count} (近期) | 伤害效率 {multi.DamagePerEnergy:F2} | 状态分 {multi.StatusScore:F1}"
        };

        if (total.TopCards.Count > 0)
        {
            lines.Add("");
            lines.Add("使用率 Top 5:");
            foreach (CardMetricSnapshot card in total.TopCards.Take(5))
            {
                lines.Add(
                    $"- {card.CardId}: {card.Played} 次, 占比 {(card.UsageRate * 100):F1}%, DPE {card.DamagePerEnergy:F2}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverlayRouteText(RoutePlanSnapshot routePlan)
    {
        List<string> lines = new()
        {
            $"路线策略: {routePlan.Strategy}",
            $"当前层数: {routePlan.CurrentFloor} | 血线比: {(routePlan.HpRatio * 100):F1}%",
            $"可达节点: {routePlan.ReachableNodeCount}"
        };

        if (routePlan.ReachableByType.Count > 0)
        {
            lines.Add("前方全图统计:");
            lines.Add(string.Join(" | ", routePlan.ReachableByType
                .Take(8)
                .Select(static type => $"{type.Label}{type.Count}")));
        }

        RouteHistoryStatsSnapshot history = routePlan.HistoricalStats;
        if (history.TotalVisited > 0)
        {
            lines.Add(
                $"历史统计: 共 {history.TotalVisited} 节点 | 最多 {history.MostVisitedLabel ?? "N/A"} | 最优 {history.BestTypeLabel ?? "N/A"}");
            if (!string.IsNullOrEmpty(history.WorstTypeLabel))
            {
                lines.Add($"最亏类型: {history.WorstTypeLabel} (评分 {history.WorstTypeValueScore:F2})");
            }

            if (history.ByType.Count > 0)
            {
                lines.Add("类型榜:");
                foreach (RouteTypeStatSnapshot type in history.ByType.Take(5))
                {
                    lines.Add(
                        $"- {type.Label}: {type.Visits} 次, 均伤 {type.AvgDamageTaken:F1}, 均回合 {type.AvgTurns:F1}, 评分 {type.AvgValueScore:F2}");
                }
            }

            if (history.RecentPath.Count > 0)
            {
                lines.Add("最近路径: " + string.Join(" -> ", history.RecentPath.TakeLast(8)));
            }
        }
        else
        {
            lines.Add("历史统计: 暂无足够路径数据。");
        }

        if (!routePlan.HasMapContext)
        {
            lines.Add("当前无地图分叉可评估。");
            return string.Join(Environment.NewLine, lines);
        }

        if (routePlan.Suggested != null)
        {
            RouteOptionSnapshot s = routePlan.Suggested;
            lines.Add(
                $"建议: ({s.Coord.Row},{s.Coord.Col}) {s.PointTypeLabel} | 分数 {s.Score:F2} | 风险 {s.RiskTag}");
            if (s.PathPreview.Count > 0)
            {
                lines.Add("预览: " + string.Join(" -> ", s.PathPreview.Take(5)));
            }

            foreach (string reason in s.Reasons.Take(3))
            {
                lines.Add("- " + reason);
            }
        }

        if (routePlan.Options.Count > 1)
        {
            lines.Add("");
            lines.Add("备选:");
            foreach (RouteOptionSnapshot option in routePlan.Options.Skip(1).Take(3))
            {
                lines.Add(
                    $"- ({option.Coord.Row},{option.Coord.Col}) {option.PointTypeLabel} | {option.Score:F2} | {option.RiskTag}");
            }
        }

        if (routePlan.Insights.Count > 0)
        {
            lines.Add("");
            lines.Add("路线建议:");
            lines.AddRange(routePlan.Insights.Take(4).Select(static x => "- " + x));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverlayLogsText(CombatHistorySnapshot combatHistory)
    {
        List<string> lines = new()
        {
            $"战斗日志 (总 {combatHistory.TotalEntries}, 显示最近 16 条)"
        };

        foreach (CombatHistoryEntrySnapshot entry in combatHistory.RecentEntries.TakeLast(16))
        {
            string detail = string.Join(
                ", ",
                entry.Details.Take(2).Select(static pair => pair.Key + "=" + pair.Value));
            if (string.IsNullOrEmpty(detail))
            {
                lines.Add($"#{entry.Index} [{entry.EntryType}] {entry.ActorName}");
            }
            else
            {
                lines.Add($"#{entry.Index} [{entry.EntryType}] {entry.ActorName} | {detail}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static RoutePlanSnapshot BuildRoutePlanSnapshot(CombatState state, Player? localPlayer)
    {
        IRunState runState = state.RunState;
        string modeKey = ResolveModeKey(state);
        double hpRatio = 1;
        if (localPlayer != null && localPlayer.Creature.MaxHp > 0)
        {
            hpRatio = Math.Clamp((double)localPlayer.Creature.CurrentHp / localPlayer.Creature.MaxHp, 0, 1);
        }

        int gold = localPlayer?.Gold ?? 0;
        return BuildRoutePlanSnapshot(runState, localPlayer, modeKey, hpRatio, gold);
    }

    private static RoutePlanSnapshot BuildRoutePlanSnapshot(
        IRunState runState,
        Player? localPlayer,
        string modeKey,
        double hpRatio,
        int gold)
    {
        List<MapPoint> nextChoices = GetNextRouteChoices(runState);
        MapCoord? routeStartCoord = ResolveRouteStartCoord(runState);
        RoutePlanSnapshot snapshot = new()
        {
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Mode = modeKey,
            CurrentFloor = runState.TotalFloor,
            HpRatio = Math.Round(hpRatio, 4),
            Strategy = BuildRouteStrategy(hpRatio, gold),
            HasMapContext = runState.CurrentMapPoint != null || nextChoices.Count > 0,
            HistoricalStats = BuildRouteHistoryStats(runState, localPlayer),
            ReachableByType = BuildReachableRouteTypeCounts(runState),
            Options = new List<RouteOptionSnapshot>()
        };
        snapshot.ReachableNodeCount = snapshot.ReachableByType.Sum(static type => type.Count);
        snapshot.Insights = BuildRouteInsights(snapshot.HistoricalStats, hpRatio, gold);

        if (nextChoices.Count == 0)
        {
            return snapshot;
        }

        IEnumerable<MapPoint> children = nextChoices
            .OrderBy(static c => c.coord.row)
            .ThenBy(static c => c.coord.col);

        foreach (MapPoint child in children)
        {
            foreach (RouteEval eval in EvaluateRoutes(child, hpRatio, gold))
            {
                List<string> pathTypeKeys = (eval.PathTypeKeys ?? new List<string>())
                    .Where(static key => !string.IsNullOrWhiteSpace(key))
                    .ToList();
                if (pathTypeKeys.Count == 0)
                {
                    pathTypeKeys.Add(NormalizeRouteTypeKey(child.PointType.ToString()));
                }

                List<MapCoordSnapshot> pathCoords = BuildRoutePathCoords(runState, routeStartCoord, eval.PathCoords, child.coord);
                List<string> preview = pathTypeKeys
                    .Select(RouteTypeKeyToLabel)
                    .Take(6)
                    .ToList();
                RouteOptionSnapshot option = new()
                {
                    Coord = new MapCoordSnapshot
                    {
                        Col = child.coord.col,
                        Row = child.coord.row
                    },
                    PointType = child.PointType.ToString(),
                    PointTypeLabel = RouteTypeKeyToLabel(NormalizeRouteTypeKey(child.PointType.ToString())),
                    Score = Math.Round(eval.Score, 4),
                    RiskTag = BuildRiskTag(child.PointType.ToString(), hpRatio),
                    PathTypeKeys = pathTypeKeys,
                    PathCoords = pathCoords,
                    PathPreview = preview,
                    Reasons = BuildRouteReasons(child.PointType.ToString(), hpRatio, gold)
                };
                snapshot.Options.Add(option);
            }
        }

        snapshot.Options = snapshot.Options
            .GroupBy(static option => string.Join("->", option.PathCoords.Select(static c => $"{c.Row},{c.Col}")), StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static option => option.Score)
                .ThenBy(static option => option.Coord.Row)
                .ThenBy(static option => option.Coord.Col)
                .First())
            .OrderByDescending(static o => o.Score)
            .ThenBy(static o => o.Coord.Row)
            .ThenBy(static o => o.Coord.Col)
            .Take(256)
            .ToList();
        snapshot.Suggested = snapshot.Options.FirstOrDefault();
        return snapshot;
    }

    private static List<RouteReachableTypeSnapshot> BuildReachableRouteTypeCounts(IRunState runState)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        HashSet<MapPoint> visited = new();
        Stack<MapPoint> stack = new(GetNextRouteChoices(runState));

        while (stack.Count > 0)
        {
            MapPoint point = stack.Pop();
            if (!visited.Add(point))
            {
                continue;
            }

            string typeKey = NormalizeRouteTypeKey(point.PointType.ToString());
            counts.TryGetValue(typeKey, out int current);
            counts[typeKey] = current + 1;

            foreach (MapPoint child in point.Children)
            {
                stack.Push(child);
            }
        }

        string[] preferredOrder = new[]
        {
            "monster",
            "elite",
            "shop",
            "rest",
            "treasure",
            "unknown"
        };

        List<RouteReachableTypeSnapshot> rows = preferredOrder
            .Where(static typeKey => ShouldDisplayRouteReachableType(typeKey))
            .Select(typeKey => new RouteReachableTypeSnapshot
            {
                TypeKey = typeKey,
                Label = RouteTypeKeyToLabel(typeKey),
                Count = counts.TryGetValue(typeKey, out int count) ? count : 0
            })
            .Where(static row => row.Count > 0)
            .ToList();

        foreach ((string key, int count) in counts.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            if (preferredOrder.Contains(key, StringComparer.OrdinalIgnoreCase) || !ShouldDisplayRouteReachableType(key) || count <= 0)
            {
                continue;
            }

            rows.Add(new RouteReachableTypeSnapshot
            {
                TypeKey = key,
                Label = RouteTypeKeyToLabel(key),
                Count = count
            });
        }

        return rows;
    }

    private static bool ShouldDisplayRouteReachableType(string? typeKey)
    {
        if (string.IsNullOrWhiteSpace(typeKey))
        {
            return false;
        }

        return !typeKey.Equals("question", StringComparison.OrdinalIgnoreCase)
            && !typeKey.Equals("boss", StringComparison.OrdinalIgnoreCase)
            && !typeKey.Equals("ancient", StringComparison.OrdinalIgnoreCase);
    }

    private static MapCoord? ResolveRouteStartCoord(IRunState runState)
    {
        if (runState.CurrentMapCoord.HasValue)
        {
            return runState.CurrentMapCoord.Value;
        }

        if (runState.CurrentMapPoint != null)
        {
            return runState.CurrentMapPoint.coord;
        }

        MapPoint? startingMapPoint = runState.Map?.StartingMapPoint;
        if (startingMapPoint != null)
        {
            return startingMapPoint.coord;
        }

        return null;
    }

    private static void TryLogRoutePlanDebug(CombatState state, RunContextSnapshot runContext, RoutePlanSnapshot routePlan)
    {
        DateTime utcNow = DateTime.UtcNow;
        if (utcNow - _lastRouteDebugUtc < RouteDebugInterval)
        {
            return;
        }

        _lastRouteDebugUtc = utcNow;
        string currentCoord = runContext.CurrentMapCoord == null
            ? "null"
            : $"{runContext.CurrentMapCoord.Row},{runContext.CurrentMapCoord.Col}";

        SafeLog(
            "RouteDebug: "
            + "inCombat=" + CombatManager.Instance.IsInProgress
            + " hasMapContext=" + routePlan.HasMapContext
            + " floor=" + routePlan.CurrentFloor.ToString(CultureInfo.InvariantCulture)
            + " currentCoord=" + currentCoord
            + " currentPointType=" + (runContext.CurrentMapPointType ?? "null")
            + " nextChoices=" + runContext.NextMapChoices.Count.ToString(CultureInfo.InvariantCulture)
            + " options=" + routePlan.Options.Count.ToString(CultureInfo.InvariantCulture)
            + " suggested=" + (routePlan.Suggested?.PointTypeLabel ?? "null"));

        if (runContext.NextMapChoices.Count > 0)
        {
            string nextChoices = string.Join(
                " | ",
                runContext.NextMapChoices.Take(6).Select(static choice =>
                    $"{choice.Coord.Row},{choice.Coord.Col}:{choice.PointType}"));
            SafeLog("RouteDebug.NextChoices: " + nextChoices);
        }

        if (routePlan.Options.Count == 0)
        {
            SafeLog("RouteDebug.Options: empty");
            return;
        }

        for (int i = 0; i < Math.Min(routePlan.Options.Count, 6); i++)
        {
            RouteOptionSnapshot option = routePlan.Options[i];
            string pathKeys = option.PathTypeKeys == null || option.PathTypeKeys.Count == 0
                ? "<empty>"
                : string.Join(",", option.PathTypeKeys.Take(10));
            string pathCoords = option.PathCoords == null || option.PathCoords.Count == 0
                ? "<empty>"
                : string.Join(" -> ", option.PathCoords.Take(12).Select(static c => $"{c.Row},{c.Col}"));

            SafeLog(
                "RouteDebug.Option[" + i.ToString(CultureInfo.InvariantCulture) + "]: "
                + "type=" + option.PointType
                + "/" + option.PointTypeLabel
                + " score=" + option.Score.ToString("F3", CultureInfo.InvariantCulture)
                + " risk=" + option.RiskTag
                + " keys=" + pathKeys
                + " coords=" + pathCoords);
        }
    }

    private static List<MapCoordSnapshot> BuildRoutePathCoords(
        IRunState runState,
        MapCoord? currentMapCoord,
        List<MapCoordSnapshot>? evaluatedPathCoords,
        MapCoord childCoord)
    {
        List<MapCoordSnapshot> route = (evaluatedPathCoords ?? new List<MapCoordSnapshot>())
            .Where(static c => c != null)
            .Select(static c => new MapCoordSnapshot
            {
                Col = c.Col,
                Row = c.Row
            })
            .ToList();

        if (route.Count == 0)
        {
            route.Add(new MapCoordSnapshot
            {
                Col = childCoord.col,
                Row = childCoord.row
            });
        }

        if (currentMapCoord != null)
        {
            MapCoord currentMapCoordValue = currentMapCoord.Value;
            if (ShouldIncludeRouteStartPoint(runState, currentMapCoordValue))
            {
                MapCoordSnapshot current = new()
                {
                    Col = currentMapCoordValue.col,
                    Row = currentMapCoordValue.row
                };
                MapCoordSnapshot first = route[0];
                if (first.Col != current.Col || first.Row != current.Row)
                {
                    route.Insert(0, current);
                }
            }
        }

        return route;
    }

    private static bool ShouldIncludeRouteStartPoint(IRunState runState, MapCoord currentMapCoord)
    {
        MapPoint? currentMapPoint = runState.CurrentMapPoint;
        if (currentMapPoint == null)
        {
            return false;
        }

        string typeKey = NormalizeRouteTypeKey(currentMapPoint.PointType.ToString());
        if (string.Equals(typeKey, "unassigned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeKey, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static RouteHistoryStatsSnapshot BuildRouteHistoryStats(IRunState runState, Player? localPlayer)
    {
        List<MapPointHistoryEntry> entries = runState.MapPointHistory
            .SelectMany(static act => act)
            .ToList();

        RouteHistoryStatsSnapshot snapshot = new()
        {
            TotalVisited = entries.Count
        };

        if (entries.Count == 0)
        {
            return snapshot;
        }

        ulong? localNetId = localPlayer?.NetId;
        Dictionary<string, RouteTypeAggregate> buckets = new(StringComparer.Ordinal);
        List<string> recentPath = new();
        foreach (MapPointHistoryEntry entry in entries)
        {
            string typeKey = NormalizeRouteTypeKey(entry.MapPointType.ToString());
            recentPath.Add(typeKey);
            if (!buckets.TryGetValue(typeKey, out RouteTypeAggregate? bucket))
            {
                bucket = new RouteTypeAggregate
                {
                    TypeKey = typeKey
                };
                buckets[typeKey] = bucket;
            }

            bucket.Visits += 1;
            int turnsTaken = entry.Rooms.Sum(static room => Math.Max(0, room.TurnsTaken));
            bucket.TotalTurns += turnsTaken;

            if (localNetId.HasValue)
            {
                try
                {
                    PlayerMapPointHistoryEntry playerStats = entry.GetEntry(localNetId.Value);
                    int damageTaken = Math.Max(0, playerStats.DamageTaken);
                    int hpHealed = Math.Max(0, playerStats.HpHealed);
                    int goldDelta = playerStats.GoldGained - playerStats.GoldSpent - playerStats.GoldLost;
                    int lootCount = playerStats.CardsGained.Count
                        + playerStats.CardChoices.Count
                        + playerStats.RelicChoices.Count * 2
                        + playerStats.PotionChoices.Count;

                    bucket.TotalDamageTaken += damageTaken;
                    bucket.TotalHpHealed += hpHealed;
                    bucket.TotalGoldDelta += goldDelta;
                    bucket.TotalLootCount += lootCount;
                    bucket.TotalValueScore += EvaluateRouteNodeScore(
                        damageTaken,
                        hpHealed,
                        goldDelta,
                        lootCount,
                        turnsTaken);
                }
                catch
                {
                    // Skip missing local player stats for this entry.
                }
            }
        }

        List<RouteTypeStatSnapshot> byType = buckets.Values
            .Select(bucket =>
            {
                int visits = Math.Max(1, bucket.Visits);
                return new RouteTypeStatSnapshot
                {
                    TypeKey = bucket.TypeKey,
                    Label = RouteTypeKeyToLabel(bucket.TypeKey),
                    Visits = bucket.Visits,
                    VisitRate = Math.Round((double)bucket.Visits / entries.Count, 4),
                    AvgTurns = Math.Round(SafeDivide(bucket.TotalTurns, visits), 3),
                    AvgDamageTaken = Math.Round(SafeDivide(bucket.TotalDamageTaken, visits), 3),
                    AvgHpHealed = Math.Round(SafeDivide(bucket.TotalHpHealed, visits), 3),
                    AvgGoldDelta = Math.Round(SafeDivide(bucket.TotalGoldDelta, visits), 3),
                    AvgValueScore = Math.Round(SafeDivide(bucket.TotalValueScore, visits), 3),
                    TotalTurns = bucket.TotalTurns,
                    TotalDamageTaken = bucket.TotalDamageTaken,
                    TotalHpHealed = bucket.TotalHpHealed,
                    TotalGoldDelta = bucket.TotalGoldDelta,
                    TotalLootCount = bucket.TotalLootCount
                };
            })
            .OrderByDescending(static item => item.Visits)
            .ThenBy(static item => item.TypeKey, StringComparer.Ordinal)
            .ToList();

        snapshot.ByType = byType;
        snapshot.RecentPath = recentPath
            .TakeLast(16)
            .Select(RouteTypeKeyToLabel)
            .ToList();

        RouteTypeStatSnapshot? mostVisited = byType
            .OrderByDescending(static item => item.Visits)
            .ThenByDescending(static item => item.AvgValueScore)
            .FirstOrDefault();
        RouteTypeStatSnapshot? bestType = byType
            .OrderByDescending(static item => item.AvgValueScore)
            .ThenByDescending(static item => item.Visits)
            .FirstOrDefault();
        RouteTypeStatSnapshot? worstType = byType
            .OrderBy(static item => item.AvgValueScore)
            .ThenByDescending(static item => item.Visits)
            .FirstOrDefault();

        snapshot.MostVisitedType = mostVisited?.TypeKey;
        snapshot.MostVisitedLabel = mostVisited?.Label;
        snapshot.BestType = bestType?.TypeKey;
        snapshot.BestTypeLabel = bestType?.Label;
        snapshot.WorstType = worstType?.TypeKey;
        snapshot.WorstTypeLabel = worstType?.Label;
        snapshot.BestTypeValueScore = bestType?.AvgValueScore ?? 0;
        snapshot.WorstTypeValueScore = worstType?.AvgValueScore ?? 0;

        return snapshot;
    }

    private static List<string> BuildRouteInsights(RouteHistoryStatsSnapshot stats, double hpRatio, int gold)
    {
        List<string> insights = new();
        if (stats.TotalVisited <= 0)
        {
            insights.Add("尚无路线历史，先完成几层再做偏好判断。");
            return insights;
        }

        if (!string.IsNullOrEmpty(stats.MostVisitedLabel))
        {
            insights.Add($"你目前走得最多的是【{stats.MostVisitedLabel}】。");
        }

        if (!string.IsNullOrEmpty(stats.BestTypeLabel))
        {
            insights.Add($"按历史收益评分，当前最优倾向是【{stats.BestTypeLabel}】。");
        }

        if (!string.IsNullOrEmpty(stats.WorstTypeLabel) &&
            !string.Equals(stats.WorstTypeLabel, stats.BestTypeLabel, StringComparison.Ordinal))
        {
            insights.Add($"按历史收益评分，当前最亏的是【{stats.WorstTypeLabel}】。");
        }

        if (hpRatio < 0.45)
        {
            insights.Add("血线偏低，路线优先休息点/低风险问号。");
        }
        else if (gold >= 180)
        {
            insights.Add("金币充足，商店节点价值明显提升。");
        }

        return insights;
    }

    private static double EvaluateRouteNodeScore(
        int damageTaken,
        int hpHealed,
        int goldDelta,
        int lootCount,
        int turnsTaken)
    {
        return hpHealed * 1.1
            - damageTaken * 1.0
            + goldDelta * 0.035
            + lootCount * 0.45
            - turnsTaken * 0.12;
    }

    private static string NormalizeRouteTypeKey(string pointType)
    {
        string lower = pointType.ToLowerInvariant();
        if (lower.Contains("ancient", StringComparison.Ordinal))
        {
            return "ancient";
        }

        if (lower.Contains("elite", StringComparison.Ordinal))
        {
            return "elite";
        }

        if (lower.Contains("question", StringComparison.Ordinal) ||
            lower.Contains("event", StringComparison.Ordinal))
        {
            return "question";
        }

        if (lower.Contains("combat", StringComparison.Ordinal) ||
            lower.Contains("monster", StringComparison.Ordinal) ||
            lower.Contains("enemy", StringComparison.Ordinal))
        {
            return "monster";
        }

        if (lower.Contains("rest", StringComparison.Ordinal) ||
            lower.Contains("camp", StringComparison.Ordinal))
        {
            return "rest";
        }

        if (lower.Contains("shop", StringComparison.Ordinal) ||
            lower.Contains("merchant", StringComparison.Ordinal))
        {
            return "shop";
        }

        if (lower.Contains("treasure", StringComparison.Ordinal) ||
            lower.Contains("chest", StringComparison.Ordinal))
        {
            return "treasure";
        }

        if (lower.Contains("boss", StringComparison.Ordinal))
        {
            return "boss";
        }

        return pointType.ToLowerInvariant();
    }

    private static string RouteTypeKeyToLabel(string routeTypeKey)
    {
        string key = routeTypeKey.Trim().ToLowerInvariant();
        return key switch
        {
            "elite" => "精英",
            "question" => "问号",
            "monster" => "小怪",
            "rest" => "休息",
            "shop" => "商店",
            "treasure" => "宝箱",
            "boss" => "首领",
            "ancient" => "古遗迹",
            "unknown" => "未知",
            "unassigned" => "未定",
            _ => key
        };
    }

    private static List<RouteEval> EvaluateRoutes(MapPoint point, double hpRatio, int gold, int depth = 0)
    {
        const int MaxRouteDepth = 12;
        string pointType = point.PointType.ToString();
        string typeKey = NormalizeRouteTypeKey(pointType);
        double ownScore = ScoreMapPointType(pointType, hpRatio, gold);
        RouteEval current = new()
        {
            Score = ownScore,
            PathTypes = new List<string> { RouteTypeKeyToLabel(typeKey) },
            PathTypeKeys = new List<string> { typeKey },
            PathCoords = new List<MapCoordSnapshot>
            {
                new()
                {
                    Col = point.coord.col,
                    Row = point.coord.row
                }
            }
        };

        if (point.Children.Count == 0 || depth >= MaxRouteDepth)
        {
            return new List<RouteEval> { current };
        }

        List<RouteEval> routes = new();
        foreach (MapPoint child in point.Children)
        {
            foreach (RouteEval childRoute in EvaluateRoutes(child, hpRatio, gold, depth + 1))
            {
                RouteEval combined = new()
                {
                    Score = ownScore + childRoute.Score * 0.82,
                    PathTypes = new List<string>(current.PathTypes),
                    PathTypeKeys = new List<string>(current.PathTypeKeys),
                    PathCoords = new List<MapCoordSnapshot>(current.PathCoords)
                };
                combined.PathTypes.AddRange(childRoute.PathTypes);
                combined.PathTypeKeys.AddRange(childRoute.PathTypeKeys);
                combined.PathCoords.AddRange(childRoute.PathCoords);
                routes.Add(combined);
            }
        }

        if (routes.Count == 0)
        {
            routes.Add(current);
        }

        return routes;
    }

    private static double ScoreMapPointType(string pointType, double hpRatio, int gold)
    {
        string lower = pointType.ToLowerInvariant();
        if (lower.Contains("elite", StringComparison.Ordinal))
        {
            return hpRatio >= 0.65 ? 2.3 : -1.5;
        }

        if (lower.Contains("rest", StringComparison.Ordinal) || lower.Contains("camp", StringComparison.Ordinal))
        {
            return hpRatio < 0.55 ? 2.4 : 0.7;
        }

        if (lower.Contains("shop", StringComparison.Ordinal))
        {
            return gold >= 150 ? 1.7 : 0.8;
        }

        if (lower.Contains("boss", StringComparison.Ordinal))
        {
            return hpRatio >= 0.6 ? 2.0 : 1.0;
        }

        if (lower.Contains("treasure", StringComparison.Ordinal) || lower.Contains("chest", StringComparison.Ordinal))
        {
            return 1.8;
        }

        if (lower.Contains("event", StringComparison.Ordinal) || lower.Contains("question", StringComparison.Ordinal))
        {
            return 1.2;
        }

        if (lower.Contains("combat", StringComparison.Ordinal) || lower.Contains("monster", StringComparison.Ordinal))
        {
            return hpRatio >= 0.4 ? 1.1 : 0.5;
        }

        return 0.7;
    }

    private static string BuildRouteStrategy(double hpRatio, int gold)
    {
        if (hpRatio < 0.4)
        {
            return "保守生存";
        }

        if (gold >= 200)
        {
            return "经济发育";
        }

        return "均衡推进";
    }

    private static string BuildRiskTag(string pointType, double hpRatio)
    {
        string lower = pointType.ToLowerInvariant();
        if (lower.Contains("elite", StringComparison.Ordinal))
        {
            return hpRatio < 0.6 ? "高风险" : "中风险";
        }

        if (lower.Contains("boss", StringComparison.Ordinal))
        {
            return hpRatio < 0.55 ? "高风险" : "中风险";
        }

        if (lower.Contains("rest", StringComparison.Ordinal) || lower.Contains("camp", StringComparison.Ordinal))
        {
            return "低风险";
        }

        return "中风险";
    }

    private static List<string> BuildRouteReasons(string pointType, double hpRatio, int gold)
    {
        List<string> reasons = new();
        string lower = pointType.ToLowerInvariant();
        if (lower.Contains("elite", StringComparison.Ordinal))
        {
            reasons.Add(hpRatio >= 0.65 ? "血线健康，可争取高收益精英。" : "当前血线偏低，精英风险较高。");
        }
        else if (lower.Contains("rest", StringComparison.Ordinal) || lower.Contains("camp", StringComparison.Ordinal))
        {
            reasons.Add(hpRatio < 0.55 ? "优先回复，提升后续容错。" : "可选择升级或维持节奏。");
        }
        else if (lower.Contains("shop", StringComparison.Ordinal))
        {
            reasons.Add(gold >= 150 ? "金币充足，商店性价比高。" : "金币不足，商店收益一般。");
        }
        else if (lower.Contains("event", StringComparison.Ordinal) || lower.Contains("question", StringComparison.Ordinal))
        {
            reasons.Add("事件点弹性高，适合寻找局外收益。");
        }
        else if (lower.Contains("treasure", StringComparison.Ordinal) || lower.Contains("chest", StringComparison.Ordinal))
        {
            reasons.Add("宝箱节点稳定提供遗物收益。");
        }
        else
        {
            reasons.Add("常规推进节点。");
        }

        return reasons;
    }

    private static double SafeDivide(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }

        return (double)numerator / denominator;
    }

    private static double SafeDivide(double numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }

        return numerator / denominator;
    }

    private static List<OverlayModifierCatalogEntry> BuildModifierCardCatalog()
    {
        return ModelDb.AllCards
            .OrderBy(static card => card.Id.Entry, StringComparer.Ordinal)
            .Select(static card => new OverlayModifierCatalogEntry
            {
                Id = card.Id.Entry,
                Title = SafeText(() => card.Title, card.Id.Entry),
                Subtitle = card.Type + " / " + card.Rarity
            })
            .ToList();
    }

    private static List<OverlayModifierCatalogEntry> BuildModifierRelicCatalog()
    {
        return ModelDb.AllRelics
            .OrderBy(static relic => relic.Id.Entry, StringComparer.Ordinal)
            .Select(static relic => new OverlayModifierCatalogEntry
            {
                Id = relic.Id.Entry,
                Title = SafeText(() => relic.Title.GetFormattedText(), relic.Id.Entry),
                Subtitle = relic.Rarity.ToString()
            })
            .ToList();
    }

    private static async Task ExecuteModifierActionAsync(string action, Func<Player, Task<string>> executor)
    {
        try
        {
            Player? player = ResolveCurrentLocalPlayer();
            if (player == null)
            {
                throw new InvalidOperationException("当前未找到本地玩家。");
            }

            string result = await executor(player);
            SafeLog("[Modifier] " + action + " ok: " + result);
            WriteStatus("modifier_ok", result);
            RefreshAfterModifierAction(action);
        }
        catch (Exception ex)
        {
            SafeLog("[Modifier] " + action + " failed: " + ex);
            WriteStatus("modifier_error", ex.Message);
        }
    }

    private static void RefreshAfterModifierAction(string reason)
    {
        _lastDumpUtc = DateTime.MinValue;
        TryDumpFromTrackedState("modifier_" + reason);
    }

    private static Player? ResolveCurrentLocalPlayer()
    {
        try
        {
            CombatStateTracker? tracker = CombatManager.Instance?.StateTracker;
            if (tracker != null &&
                CombatTrackerStateField != null &&
                CombatTrackerStateField.GetValue(tracker) is CombatState trackedState)
            {
                Player? combatPlayer = ResolveLocalPlayer(trackedState);
                if (combatPlayer != null)
                {
                    return combatPlayer;
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        RunState? runState = ResolveCurrentRunState();
        return runState == null ? null : ResolveLocalPlayer(runState);
    }

    private static Player? ResolveLocalPlayer(CombatState state)
    {
        try
        {
            Player? localPlayer = LocalContext.GetMe(state);
            if (localPlayer != null)
            {
                return localPlayer;
            }
        }
        catch
        {
            // Fallback below.
        }

        try
        {
            if (state.RunState != null)
            {
                Player? runLocalPlayer = ResolveLocalPlayer(state.RunState);
                if (runLocalPlayer != null)
                {
                    Player? matchedCombatPlayer = state.Players.FirstOrDefault(player => player.NetId == runLocalPlayer.NetId);
                    if (matchedCombatPlayer != null)
                    {
                        return matchedCombatPlayer;
                    }
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        return state.Players.FirstOrDefault();
    }

    private static Player? ResolveLocalPlayer(IRunState runState)
    {
        try
        {
            Player? localPlayer = LocalContext.GetMe(runState.Players);
            if (localPlayer != null)
            {
                return localPlayer;
            }
        }
        catch
        {
            // Fallback below.
        }

        return runState.Players.FirstOrDefault();
    }

    private static RunState? ResolveCurrentRunState()
    {
        try
        {
            CombatStateTracker? tracker = CombatManager.Instance?.StateTracker;
            if (tracker != null &&
                CombatTrackerStateField != null &&
                CombatTrackerStateField.GetValue(tracker) is CombatState trackedState &&
                trackedState.RunState is RunState combatRunState)
            {
                return combatRunState;
            }
        }
        catch
        {
            // Fallback below.
        }

        try
        {
            RunManager? runManager = RunManager.Instance;
            if (runManager != null &&
                RunManagerStateField != null &&
                RunManagerStateField.GetValue(runManager) is RunState runState)
            {
                return runState;
            }
        }
        catch
        {
            // Ignore.
        }

        return null;
    }

    private static void WriteStatus(string phase, string message)
    {
        try
        {
            StatusSnapshot payload = new()
            {
                ModId = ModId,
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Phase = phase,
                Message = message
            };
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            lock (FileLock)
            {
                File.WriteAllText(StatusFilePath, json);
            }
        }
        catch
        {
            // No-op.
        }
    }

    private static void SafeLog(string message)
    {
        try
        {
            Log.Info("[Sts2McpProbe] " + message);
        }
        catch
        {
            // Ignore logger failures.
        }

        try
        {
            string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
            lock (FileLock)
            {
                File.AppendAllText(ProbeLogPath, line);
                if (!string.Equals(ProbeLogPath, ModsProbeLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.AppendAllText(ModsProbeLogPath, line);
                }
            }
        }
        catch
        {
            // Ignore file log failures.
        }
    }

    private static string ResolveModsProbeLogPath()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string? gameDir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(gameDir))
                {
                    string modDir = Path.Combine(gameDir, "mods", "Sts2Mcp");
                    Directory.CreateDirectory(modDir);
                    return Path.Combine(modDir, "probe_mod.log");
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        return Path.Combine(WorkDir, "probe_mod.log");
    }

    private sealed class Snapshot
    {
        public string ModId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsInCombat { get; set; }
        public bool DictionaryReady { get; set; }
        public string DictionaryPath { get; set; } = string.Empty;
        public int RoundNumber { get; set; }
        public string CurrentSide { get; set; } = string.Empty;
        public PlayerSnapshot? LocalPlayer { get; set; }
        public List<PlayerSnapshot> Players { get; set; } = new();
        public List<EnemySnapshot> Enemies { get; set; } = new();
        public RunContextSnapshot RunContext { get; set; } = new();
        public ActionSpaceSnapshot ActionSpace { get; set; } = new();
        public CombatHistorySnapshot CombatHistory { get; set; } = new();
        public AnalyticsSnapshot Analytics { get; set; } = new();
        public RoutePlanSnapshot RoutePlan { get; set; } = new();
    }

    private sealed class StatusSnapshot
    {
        public string ModId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    private sealed class PlayerSnapshot
    {
        public ulong NetId { get; set; }
        public string CharacterId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int CurrentHp { get; set; }
        public int MaxHp { get; set; }
        public int Block { get; set; }
        public int Gold { get; set; }
        public int MaxEnergy { get; set; }
        public int? Energy { get; set; }
        public int? Stars { get; set; }
        public List<CardSnapshot> Hand { get; set; } = new();
        public List<CardSnapshot> RunDeck { get; set; } = new();
        public PilesSnapshot? Piles { get; set; }
        public List<RelicSnapshot> Relics { get; set; } = new();
        public List<PotionSnapshot> Potions { get; set; } = new();
        public List<PowerSnapshot> Powers { get; set; } = new();
    }

    private sealed class CardSnapshot
    {
        public uint? CombatCardIndex { get; set; }
        public string IdEntry { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty;
        public int? CurrentEnergyCost { get; set; }
        public int? CurrentStarCost { get; set; }
        public bool? CostsX { get; set; }
        public bool CanPlay { get; set; }
        public bool CanPlayEvaluated { get; set; }
        public string? UnplayableReason { get; set; }
        public string? PreventedByModelId { get; set; }
    }

    private sealed class PilesSnapshot
    {
        public CardPileSnapshot Draw { get; set; } = new();
        public CardPileSnapshot Hand { get; set; } = new();
        public CardPileSnapshot Discard { get; set; } = new();
        public CardPileSnapshot Exhaust { get; set; } = new();
        public CardPileSnapshot Play { get; set; } = new();
    }

    private sealed class CardPileSnapshot
    {
        public string Type { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<CardSnapshot> Cards { get; set; } = new();
    }

    private sealed class EnemySnapshot
    {
        public uint? CombatId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public int CurrentHp { get; set; }
        public int MaxHp { get; set; }
        public int Block { get; set; }
        public bool IsHittable { get; set; }
        public List<IntentSnapshot> Intents { get; set; } = new();
        public List<PowerSnapshot> Powers { get; set; } = new();
    }

    private sealed class IntentSnapshot
    {
        public string IntentType { get; set; } = string.Empty;
        public string? Label { get; set; }
    }

    private sealed class RelicSnapshot
    {
        public string IdEntry { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Rarity { get; set; } = string.Empty;
    }

    private sealed class PotionSnapshot
    {
        public int SlotIndex { get; set; }
        public bool IsEmpty { get; set; }
        public string? IdEntry { get; set; }
        public string? Title { get; set; }
        public string? Rarity { get; set; }
        public string? Usage { get; set; }
        public string? TargetType { get; set; }
    }

    private sealed class PowerSnapshot
    {
        public string IdEntry { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Amount { get; set; }
    }

    private sealed class RunContextSnapshot
    {
        public bool IsRunInProgress { get; set; }
        public bool IsGameOver { get; set; }
        public long? RunTimeSeconds { get; set; }
        public int AscensionLevel { get; set; }
        public int CurrentActIndex { get; set; }
        public string CurrentActId { get; set; } = string.Empty;
        public string CurrentActTitle { get; set; } = string.Empty;
        public int ActFloor { get; set; }
        public int TotalFloor { get; set; }
        public int CurrentRoomCount { get; set; }
        public string? CurrentRoomType { get; set; }
        public string? CurrentRoomModelId { get; set; }
        public string SeedString { get; set; } = string.Empty;
        public uint SeedValue { get; set; }
        public MapCoordSnapshot? CurrentMapCoord { get; set; }
        public string? CurrentMapPointType { get; set; }
        public List<MapPointChoiceSnapshot> NextMapChoices { get; set; } = new();
        public List<string> Modifiers { get; set; } = new();
        public EncounterSnapshot? Encounter { get; set; }
        public RunLocalPlayerSnapshot? LocalPlayer { get; set; }
        public RunHistoryPointSnapshot? CurrentMapPointHistory { get; set; }
    }

    private sealed class EncounterSnapshot
    {
        public string IdEntry { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string RoomType { get; set; } = string.Empty;
        public List<string> Slots { get; set; } = new();
    }

    private sealed class RunLocalPlayerSnapshot
    {
        public ulong NetId { get; set; }
        public string CharacterId { get; set; } = string.Empty;
        public int Gold { get; set; }
        public int DeckCount { get; set; }
        public int RelicCount { get; set; }
        public int PotionCount { get; set; }
    }

    private sealed class MapCoordSnapshot
    {
        public int Col { get; set; }
        public int Row { get; set; }
    }

    private sealed class MapPointChoiceSnapshot
    {
        public MapCoordSnapshot Coord { get; set; } = new();
        public string PointType { get; set; } = string.Empty;
        public List<string> QuestIds { get; set; } = new();
    }

    private sealed class RunHistoryPointSnapshot
    {
        public string MapPointType { get; set; } = string.Empty;
        public List<MapPointRoomSnapshot> Rooms { get; set; } = new();
        public MapPointPlayerStatsSnapshot? LocalPlayerStats { get; set; }
    }

    private sealed class MapPointRoomSnapshot
    {
        public string RoomType { get; set; } = string.Empty;
        public string? ModelId { get; set; }
        public int TurnsTaken { get; set; }
        public List<string> MonsterIds { get; set; } = new();
    }

    private sealed class MapPointPlayerStatsSnapshot
    {
        public ulong PlayerId { get; set; }
        public int CurrentGold { get; set; }
        public int CurrentHp { get; set; }
        public int MaxHp { get; set; }
        public int GoldGained { get; set; }
        public int GoldSpent { get; set; }
        public int DamageTaken { get; set; }
        public int HpHealed { get; set; }
    }

    private sealed class ActionSpaceSnapshot
    {
        public bool IsPlayerTurn { get; set; }
        public bool PlayerActionsDisabled { get; set; }
        public int ActionCount { get; set; }
        public int PlayableActionCount { get; set; }
        public List<string> SupportedActions { get; set; } = new() { "play_card" };
        public List<PlayCardActionOptionSnapshot> PlayCardActions { get; set; } = new();
    }

    private sealed class PlayCardActionOptionSnapshot
    {
        [JsonIgnore]
        public uint SortOrder { get; set; }

        public uint? CombatCardIndex { get; set; }
        public string CardId { get; set; } = string.Empty;
        public string CardTitle { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty;
        public bool RequiresTarget { get; set; }
        public bool CanPlayByRules { get; set; }
        public bool CanPlayNow { get; set; }
        public string UnplayableReason { get; set; } = string.Empty;
        public string? PreventedByModelId { get; set; }
        public List<TargetOptionSnapshot> ValidTargets { get; set; } = new();
        public PlayCardCommandExample ExampleCommand { get; set; } = new();
    }

    private sealed class TargetOptionSnapshot
    {
        public uint? CombatId { get; set; }
        public string ModelId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public bool IsAlive { get; set; }
        public bool IsHittable { get; set; }
    }

    private sealed class PlayCardCommandExample
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "play_card";

        [JsonPropertyName("combat_card_index")]
        public uint? CombatCardIndex { get; set; }

        [JsonPropertyName("target_combat_id")]
        public uint? TargetCombatId { get; set; }
    }

    private sealed class CombatHistorySnapshot
    {
        public int TotalEntries { get; set; }
        public List<CombatHistoryEntrySnapshot> RecentEntries { get; set; } = new();
    }

    private sealed class CombatHistoryEntrySnapshot
    {
        public int Index { get; set; }
        public string EntryType { get; set; } = string.Empty;
        public int RoundNumber { get; set; }
        public string CurrentSide { get; set; } = string.Empty;
        public uint? ActorCombatId { get; set; }
        public string ActorModelId { get; set; } = string.Empty;
        public string ActorName { get; set; } = string.Empty;
        public bool HappenedThisTurn { get; set; }
        public string Description { get; set; } = string.Empty;
        public string HumanReadable { get; set; } = string.Empty;
        public Dictionary<string, string?> Details { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StatsAccumulator
    {
        public int DamageDealt { get; set; }
        public int DamageTaken { get; set; }
        public int DamageBlocked { get; set; }
        public int BlockGained { get; set; }
        public int EnergySpent { get; set; }
        public int OverkillDamage { get; set; }
        public int CardsPlayed { get; set; }
        public int BuffsApplied { get; set; }
        public int DebuffsApplied { get; set; }
        public Dictionary<string, CardStatAccumulator> CardStats { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> BuffCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> DebuffCounts { get; } = new(StringComparer.Ordinal);
    }

    private sealed class CardStatAccumulator
    {
        public string CardId { get; set; } = string.Empty;
        public int Played { get; set; }
        public int DamageDealt { get; set; }
        public int BlockGained { get; set; }
        public int OverkillDamage { get; set; }
        public int EnergySpent { get; set; }
    }

    private sealed class BattleSegmentRuntime
    {
        public string CombatId { get; set; } = string.Empty;
        public string ModeKey { get; set; } = "singleplayer";
        public DateTime StartedUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public string? EncounterId { get; set; }
        public string? EncounterTitle { get; set; }
        public int ActFloor { get; set; }
        public int TotalFloor { get; set; }
        public int LastEnemyAliveCount { get; set; }
        public int LastRoundNumber { get; set; }
        public StatsAccumulator Stats { get; } = new();
    }

    private sealed class AnalyticsSnapshot
    {
        public string ModId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Mode { get; set; } = "singleplayer";
        public bool IsInCombat { get; set; }
        public CombatMetricsSnapshot CurrentCombat { get; set; } = new();
        public CombatMetricsSnapshot Total { get; set; } = new();
        public ModeMetricsSnapshot Singleplayer { get; set; } = new();
        public ModeMetricsSnapshot Multiplayer { get; set; } = new();
        public List<BattleSegmentSnapshot> RecentBattles { get; set; } = new();
        public List<CombatLogLineSnapshot> RecentCombatLog { get; set; } = new();
        public AnalyticsHighlightsSnapshot Highlights { get; set; } = new();
        public RouteHistoryStatsSnapshot RouteHistory { get; set; } = new();
        public List<PlayerContributionSnapshot> CurrentContributionBoard { get; set; } = new();
        public List<PlayerContributionSnapshot> TotalContributionBoard { get; set; } = new();
    }

    private sealed class ModeMetricsSnapshot
    {
        public string Mode { get; set; } = string.Empty;
        public CombatMetricsSnapshot Total { get; set; } = new();
        public List<BattleSegmentSnapshot> RecentBattles { get; set; } = new();
    }

    private sealed class AnalyticsHighlightsSnapshot
    {
        public string Mode { get; set; } = string.Empty;
        public int IncomingDamageEstimate { get; set; }
        public int UnblockedThreatEstimate { get; set; }
        public string DangerLevel { get; set; } = "low";
        public int CurrentTempo { get; set; }
        public string? MvpCard { get; set; }
        public int RecentWinCount { get; set; }
        public int RecentBattleCount { get; set; }
    }

    private sealed class CombatMetricsSnapshot
    {
        public int DamageDealt { get; set; }
        public int DamageTaken { get; set; }
        public int DamageBlocked { get; set; }
        public int BlockGained { get; set; }
        public int EnergySpent { get; set; }
        public int OverkillDamage { get; set; }
        public int CardsPlayed { get; set; }
        public int BuffsApplied { get; set; }
        public int DebuffsApplied { get; set; }
        public int UniqueBuffKinds { get; set; }
        public int UniqueDebuffKinds { get; set; }
        public double DamagePerEnergy { get; set; }
        public int TempoScore { get; set; }
        public double StatusScore { get; set; }
        public List<CardMetricSnapshot> TopCards { get; set; } = new();
        public List<PowerCountSnapshot> TopBuffs { get; set; } = new();
        public List<PowerCountSnapshot> TopDebuffs { get; set; } = new();
    }

    private sealed class PlayerContributionSnapshot
    {
        public ulong NetId { get; set; }
        public string Name { get; set; } = string.Empty;
        public double ContributionScore { get; set; }
        public double Ndps { get; set; }
        public double SupportDamage { get; set; }
        public double Mitigation { get; set; }
        public double TeamBlock { get; set; }
        public double StatusScore { get; set; }
        public int StatusApplications { get; set; }
        public int OffensiveDebuffsApplied { get; set; }
        public int DefensiveBuffsApplied { get; set; }
        public double Ratio { get; set; }
    }

    private sealed class CardMetricSnapshot
    {
        public string CardId { get; set; } = string.Empty;
        public int Played { get; set; }
        public double UsageRate { get; set; }
        public int DamageDealt { get; set; }
        public int BlockGained { get; set; }
        public int OverkillDamage { get; set; }
        public int EnergySpent { get; set; }
        public double DamagePerEnergy { get; set; }
    }

    private sealed class PowerCountSnapshot
    {
        public string PowerId { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class CombatLogLineSnapshot
    {
        public int Index { get; set; }
        public string EntryType { get; set; } = string.Empty;
        public string Actor { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    private sealed class BattleSegmentSnapshot
    {
        public string CombatId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string? EncounterId { get; set; }
        public string? EncounterTitle { get; set; }
        public int ActFloor { get; set; }
        public int TotalFloor { get; set; }
        public int RoundCount { get; set; }
        public string StartedAtUtc { get; set; } = string.Empty;
        public string EndedAtUtc { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public string Result { get; set; } = string.Empty;
        public CombatMetricsSnapshot Metrics { get; set; } = new();
    }

    private sealed class RoutePlanSnapshot
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public string Mode { get; set; } = "singleplayer";
        public int CurrentFloor { get; set; }
        public bool HasMapContext { get; set; }
        public double HpRatio { get; set; }
        public string Strategy { get; set; } = "balanced";
        public int ReachableNodeCount { get; set; }
        public List<RouteReachableTypeSnapshot> ReachableByType { get; set; } = new();
        public RouteHistoryStatsSnapshot HistoricalStats { get; set; } = new();
        public List<string> Insights { get; set; } = new();
        public RouteOptionSnapshot? Suggested { get; set; }
        public List<RouteOptionSnapshot> Options { get; set; } = new();
    }

    private sealed class RouteReachableTypeSnapshot
    {
        public string TypeKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class RouteHistoryStatsSnapshot
    {
        public int TotalVisited { get; set; }
        public string? MostVisitedType { get; set; }
        public string? MostVisitedLabel { get; set; }
        public string? BestType { get; set; }
        public string? BestTypeLabel { get; set; }
        public string? WorstType { get; set; }
        public string? WorstTypeLabel { get; set; }
        public double BestTypeValueScore { get; set; }
        public double WorstTypeValueScore { get; set; }
        public List<RouteTypeStatSnapshot> ByType { get; set; } = new();
        public List<string> RecentPath { get; set; } = new();
    }

    private sealed class RouteTypeStatSnapshot
    {
        public string TypeKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Visits { get; set; }
        public double VisitRate { get; set; }
        public double AvgTurns { get; set; }
        public double AvgDamageTaken { get; set; }
        public double AvgHpHealed { get; set; }
        public double AvgGoldDelta { get; set; }
        public double AvgValueScore { get; set; }
        public int TotalTurns { get; set; }
        public int TotalDamageTaken { get; set; }
        public int TotalHpHealed { get; set; }
        public int TotalGoldDelta { get; set; }
        public int TotalLootCount { get; set; }
    }

    private sealed class RouteTypeAggregate
    {
        public string TypeKey { get; set; } = string.Empty;
        public int Visits { get; set; }
        public int TotalTurns { get; set; }
        public int TotalDamageTaken { get; set; }
        public int TotalHpHealed { get; set; }
        public int TotalGoldDelta { get; set; }
        public int TotalLootCount { get; set; }
        public double TotalValueScore { get; set; }
    }

    private sealed class RouteOptionSnapshot
    {
        public MapCoordSnapshot Coord { get; set; } = new();
        public string PointType { get; set; } = string.Empty;
        public string PointTypeLabel { get; set; } = string.Empty;
        public double Score { get; set; }
        public string RiskTag { get; set; } = string.Empty;
        public List<string> PathTypeKeys { get; set; } = new();
        public List<MapCoordSnapshot> PathCoords { get; set; } = new();
        public List<string> PathPreview { get; set; } = new();
        public List<string> Reasons { get; set; } = new();
    }

    private sealed class DashboardSnapshot
    {
        public string ModId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public bool IsInCombat { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> Alerts { get; set; } = new();
        public List<string> FunInsights { get; set; } = new();
        public CombatMetricsSnapshot CurrentCombat { get; set; } = new();
        public CombatMetricsSnapshot Total { get; set; } = new();
        public CombatMetricsSnapshot SingleplayerTotal { get; set; } = new();
        public CombatMetricsSnapshot MultiplayerTotal { get; set; } = new();
        public RoutePlanSnapshot RoutePlan { get; set; } = new();
        public RouteHistoryStatsSnapshot RouteHistory { get; set; } = new();
        public List<BattleSegmentSnapshot> RecentBattles { get; set; } = new();
        public List<CombatLogLineSnapshot> RecentCombatLog { get; set; } = new();
    }

    private sealed class RouteEval
    {
        public double Score { get; set; }
        public List<string> PathTypes { get; set; } = new();
        public List<string> PathTypeKeys { get; set; } = new();
        public List<MapCoordSnapshot> PathCoords { get; set; } = new();
    }

    private sealed class PowerSourceKey : IEquatable<PowerSourceKey>
    {
        public uint ReceiverCombatId { get; init; }
        public string PowerId { get; init; } = string.Empty;

        public bool Equals(PowerSourceKey? other)
        {
            if (other == null)
            {
                return false;
            }

            return ReceiverCombatId == other.ReceiverCombatId
                && string.Equals(PowerId, other.PowerId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as PowerSourceKey);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ReceiverCombatId, PowerId);
        }
    }

    private sealed class PowerSourceRecord
    {
        public uint? ApplierCombatId { get; set; }
        public ulong? ApplierNetId { get; set; }
        public string ApplierName { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class ContributionAccumulator
    {
        public ulong NetId { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Ndps { get; set; }
        public double SupportDamage { get; set; }
        public double Mitigation { get; set; }
        public double TeamBlock { get; set; }
        public int StatusApplications { get; set; }
        public int OffensiveDebuffsApplied { get; set; }
        public int DefensiveBuffsApplied { get; set; }
        public bool HasTotalsMirror { get; set; }
    }

    private sealed class ResolvedPowerModifier
    {
        public uint OwnerCombatId { get; set; }
        public uint? ApplierCombatId { get; set; }
        public ulong? ApplierNetId { get; set; }
        public string ApplierName { get; set; } = string.Empty;
        public string PowerId { get; set; } = string.Empty;
        public double Multiplier { get; set; }
    }

    private sealed class DictionarySnapshot
    {
        public string ModId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string Locale { get; set; } = string.Empty;
        public Dictionary<string, CardDictionaryEntry> Cards { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, RelicDictionaryEntry> Relics { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, PotionDictionaryEntry> Potions { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, PowerDictionaryEntry> Powers { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, MonsterDictionaryEntry> Monsters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, CharacterDictionaryEntry> Characters { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CardDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty;
        public string Rarity { get; set; } = string.Empty;
    }

    private sealed class RelicDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Rarity { get; set; } = string.Empty;
    }

    private sealed class PotionDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Rarity { get; set; } = string.Empty;
        public string Usage { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty;
    }

    private sealed class PowerDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    private sealed class MonsterDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public int MinInitialHp { get; set; }
        public int MaxInitialHp { get; set; }
    }

    private sealed class CharacterDictionaryEntry
    {
        public string Title { get; set; } = string.Empty;
        public int StartingHp { get; set; }
        public int MaxEnergy { get; set; }
        public List<string> StartingDeck { get; set; } = new();
        public List<string> StartingRelics { get; set; } = new();
        public List<string> StartingPotions { get; set; } = new();
    }

    private sealed class CommandRequest
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("combat_card_index")]
        public uint? CombatCardIndex { get; set; }

        [JsonPropertyName("target_combat_id")]
        public uint? TargetCombatId { get; set; }
    }
}
