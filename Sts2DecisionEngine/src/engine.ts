import { appendNdjson, readJsonFile, writeJsonFileAtomic } from "./io.js";
import { Paths, fileExists } from "./paths.js";
import { buildActionReasons, rankActions } from "./policy.js";
import { computeTransitionReward } from "./reward.js";
import {
  AdviceAction,
  AdviceSnapshot,
  CommandPayload,
  ProbeState,
  RankedAction,
  WeightMap
} from "./types.js";
import {
  DefaultConfig,
  DefaultWeights,
  loadWeights,
  saveWeights,
  updateWeightsOnline
} from "./weights.js";
import { estimateIncomingDamage, makeStateFingerprint } from "./features.js";

interface PendingDecision {
  stateBefore: ProbeState;
  selected: RankedAction;
  command: CommandPayload;
  createdAtUnixMs: number;
}

function resolveMode(): "advise" | "autoplay" {
  const raw = (process.env.STS2_ENGINE_MODE ?? "advise").toLowerCase();
  return raw === "autoplay" ? "autoplay" : "advise";
}

function buildSummary(state: ProbeState, ranked: RankedAction[]): string {
  const me = state.LocalPlayer;
  if (!me) {
    return "本局没有本地玩家上下文。";
  }
  const incoming = estimateIncomingDamage(state);
  const top = ranked[0];
  if (!top) {
    return `当前无可执行出牌动作。血量 ${me.CurrentHp}/${me.MaxHp}，能量 ${me.Energy ?? 0}，预估来伤 ${incoming}。`;
  }
  return `建议优先打出 ${top.action.CardTitle}（${top.action.CardId}）。血量 ${me.CurrentHp}/${me.MaxHp}，能量 ${me.Energy ?? 0}，格挡 ${me.Block}，预估来伤 ${incoming}。`;
}

function buildAdviceActions(ranked: RankedAction[], limit = 3): AdviceAction[] {
  return ranked.slice(0, limit).map((row, index) => ({
    rank: index + 1,
    cardId: row.action.CardId,
    cardTitle: row.action.CardTitle,
    targetCombatId: row.command.target_combat_id,
    targetName: row.target?.Name,
    score: Number(row.score.toFixed(4)),
    reasons: buildActionReasons(row),
    command: row.command
  }));
}

function writeAdvice(
  mode: "advise" | "autoplay",
  state: ProbeState,
  ranked: RankedAction[]
): void {
  const me = state.LocalPlayer;
  if (!me) {
    return;
  }
  const advice: AdviceSnapshot = {
    timestampUtc: new Date().toISOString(),
    mode,
    summary: buildSummary(state, ranked),
    topActions: buildAdviceActions(ranked, 3),
    context: {
      playerHp: me.CurrentHp,
      playerMaxHp: me.MaxHp,
      block: me.Block,
      energy: Number(me.Energy ?? 0),
      stars: Number(me.Stars ?? 0),
      incomingDamageEstimate: estimateIncomingDamage(state),
      enemiesAlive: state.Enemies.length
    }
  };
  writeJsonFileAtomic(Paths.adviceJson, advice);
}

function maybeWriteCommand(command: CommandPayload): boolean {
  if (fileExists(Paths.commandJson)) {
    return false;
  }
  writeJsonFileAtomic(Paths.commandJson, command);
  return true;
}

function loadState(): ProbeState | null {
  return readJsonFile<ProbeState>(Paths.stateJson);
}

function loadEngineWeights(): WeightMap {
  if (!fileExists(Paths.weightsJson)) {
    saveWeights(DefaultWeights);
    return { ...DefaultWeights };
  }
  return loadWeights();
}

export function startEngine(): void {
  const mode = resolveMode();
  const config = DefaultConfig;
  let weights = loadEngineWeights();
  let lastTimestamp = "";
  let pending: PendingDecision | null = null;
  let cooldownUntil = 0;

  console.log(`[Sts2DecisionEngine] mode=${mode}`);
  console.log(`[Sts2DecisionEngine] probeDir=${Paths.probeDir}`);

  setInterval(() => {
    const state = loadState();
    if (!state || !state.TimestampUtc || state.TimestampUtc === lastTimestamp) {
      return;
    }

    if (pending) {
      const transition = computeTransitionReward(pending.stateBefore, state);
      weights = updateWeightsOnline(
        weights,
        pending.selected.features,
        transition.reward,
        config
      );
      saveWeights(weights);
      appendNdjson(Paths.trainingLog, {
        timestampUtc: new Date().toISOString(),
        from: pending.stateBefore.TimestampUtc,
        to: state.TimestampUtc,
        cardId: pending.selected.action.CardId,
        targetCombatId: pending.command.target_combat_id,
        reward: transition.reward,
        rewardComponents: transition.components
      });
      pending = null;
    }

    const ranked = rankActions(state, weights);
    const fingerprint = makeStateFingerprint(state);
    writeAdvice(mode, state, ranked);

    appendNdjson(Paths.decisionsLog, {
      timestampUtc: new Date().toISOString(),
      stateTimestampUtc: state.TimestampUtc,
      stateFingerprint: fingerprint,
      inCombat: state.IsInCombat,
      side: state.CurrentSide,
      actionCount: state.ActionSpace?.ActionCount ?? 0,
      playableActionCount: state.ActionSpace?.PlayableActionCount ?? 0,
      top1: ranked[0]
        ? {
            cardId: ranked[0].action.CardId,
            cardTitle: ranked[0].action.CardTitle,
            targetCombatId: ranked[0].command.target_combat_id,
            score: ranked[0].score
          }
        : null,
      top3: ranked.slice(0, 3).map((r) => ({
        cardId: r.action.CardId,
        cardTitle: r.action.CardTitle,
        targetCombatId: r.command.target_combat_id,
        score: r.score
      }))
    });

    const shouldAct =
      mode === "autoplay" &&
      state.IsInCombat &&
      state.CurrentSide === "Player" &&
      Date.now() >= cooldownUntil &&
      ranked.length > 0;

    if (shouldAct) {
      const best = ranked[0];
      const command = best.command;
      if (maybeWriteCommand(command)) {
        pending = {
          stateBefore: state,
          selected: best,
          command,
          createdAtUnixMs: Date.now()
        };
        cooldownUntil = Date.now() + config.cooldownMs;
      }
    }

    lastTimestamp = state.TimestampUtc;
  }, config.intervalMs);

  process.on("SIGINT", () => {
    console.log("\n[Sts2DecisionEngine] stop");
    process.exit(0);
  });
}
