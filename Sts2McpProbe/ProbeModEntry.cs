using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

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
    private const int MaxHistoryEntries = 80;
    private static DateTime _lastDumpUtc = DateTime.MinValue;
    private static DateTime _lastCommandCheckUtc = DateTime.MinValue;
    private static DateTime _lastDictionaryAttemptUtc = DateTime.MinValue;
    private static bool _hooksInstalled;
    private static bool _dictionaryDumped;

    private static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sts2McpProbe");
    private static readonly string StateFilePath = Path.Combine(WorkDir, "state.json");
    private static readonly string StatusFilePath = Path.Combine(WorkDir, "status.json");
    private static readonly string DictionaryFilePath = Path.Combine(WorkDir, "dictionary.json");
    private static readonly string CommandFilePath = Path.Combine(WorkDir, "command.json");
    private static readonly string ProbeLogPath = Path.Combine(WorkDir, "probe.log");

    public static void Initialize()
    {
        Directory.CreateDirectory(WorkDir);
        SafeLog("Initialize() invoked.");
        WriteStatus("initialized", "Mod initialized successfully.");
        InstallHooks();
        TryDumpDictionary(force: true);
    }

    private static void InstallHooks()
    {
        if (_hooksInstalled)
        {
            return;
        }

        _hooksInstalled = true;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        SafeLog("Hooks installed.");
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
            Player? localPlayerEntity = LocalContext.GetMe(state);
            PlayerSnapshot? localPlayer = localPlayerEntity == null ? null : BuildPlayerSnapshot(localPlayerEntity);
            List<EnemySnapshot> enemies = state.Enemies.Select(enemy => BuildEnemySnapshot(enemy, state)).ToList();
            List<PlayerSnapshot> allPlayers = state.Players.Select(BuildPlayerSnapshot).ToList();
            RunContextSnapshot runContext = BuildRunContextSnapshot(state, localPlayerEntity);
            ActionSpaceSnapshot actionSpace = BuildActionSpaceSnapshot(state, localPlayerEntity);
            CombatHistorySnapshot combatHistory = BuildCombatHistorySnapshot(state);

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
                CombatHistory = combatHistory
            };

            string json = JsonSerializer.Serialize(payload, JsonOptions);
            lock (FileLock)
            {
                File.WriteAllText(StateFilePath, json);
            }
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

        if (runState.CurrentMapPoint != null)
        {
            snapshot.NextMapChoices = runState.CurrentMapPoint.Children
                .OrderBy(static p => p.coord.row)
                .ThenBy(static p => p.coord.col)
                .Select(BuildMapPointChoiceSnapshot)
                .ToList();
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

    private static CombatHistorySnapshot BuildCombatHistorySnapshot(CombatState state)
    {
        List<CombatHistoryEntry> entries;
        try
        {
            entries = CombatManager.Instance.History.Entries.ToList();
        }
        catch
        {
            entries = new List<CombatHistoryEntry>();
        }

        int totalEntries = entries.Count;
        int start = Math.Max(0, totalEntries - MaxHistoryEntries);
        List<CombatHistoryEntrySnapshot> recentEntries = new(totalEntries - start);
        for (int i = start; i < totalEntries; i++)
        {
            CombatHistoryEntry entry = entries[i];
            recentEntries.Add(BuildCombatHistoryEntrySnapshot(entry, state, i));
        }

        return new CombatHistorySnapshot
        {
            TotalEntries = totalEntries,
            RecentEntries = recentEntries
        };
    }

    private static CombatHistoryEntrySnapshot BuildCombatHistoryEntrySnapshot(CombatHistoryEntry entry, CombatState state, int index)
    {
        CombatHistoryEntrySnapshot snapshot = new()
        {
            Index = index,
            EntryType = entry.GetType().Name,
            RoundNumber = entry.RoundNumber,
            CurrentSide = entry.CurrentSide.ToString(),
            ActorCombatId = entry.Actor.CombatId,
            ActorModelId = entry.Actor.ModelId.Entry,
            ActorName = entry.Actor.Name,
            HappenedThisTurn = entry.HappenedThisTurn(state),
            Description = entry.Description,
            HumanReadable = entry.HumanReadableString
        };

        Dictionary<string, string?> details = snapshot.Details;
        switch (entry)
        {
            case CardPlayStartedEntry cardPlayStarted:
                AddDetail(details, "card_id", cardPlayStarted.CardPlay.Card.Id.Entry);
                if (NetCombatCardDb.Instance.TryGetCardId(cardPlayStarted.CardPlay.Card, out uint cardId1))
                {
                    AddDetail(details, "combat_card_index", cardId1);
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

        return snapshot;
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
            }
        }
        catch
        {
            // Ignore file log failures.
        }
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
