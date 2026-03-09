import {
  CommandPayload,
  PlayCardActionOption,
  ProbeState,
  RankedAction,
  TargetOption,
  WeightMap
} from "../types.js";
import { buildActionFeatures } from "./features.js";
import { estimateIncomingDamage } from "./metrics.js";

function dotProduct(features: Record<string, number>, weights: WeightMap): number {
  let score = 0;
  for (const [key, value] of Object.entries(features)) {
    score += (weights[key] ?? 0) * value;
  }
  return score;
}

function formatContributionKey(key: string): string {
  const mapping: Record<string, string> = {
    defensive_candidate: "防守动作",
    focus_fire_bonus: "集火低血",
    stop_attacker_bonus: "压制攻击者",
    incoming_threat: "来伤压力",
    threat_after_block: "破防风险",
    many_enemy_aoe: "AOE 场景",
    spend_energy: "能量利用"
  };
  return mapping[key] ?? key;
}

function enumerateCandidates(action: PlayCardActionOption): {
  command: CommandPayload;
  target: TargetOption | null;
}[] {
  if (action.RequiresTarget) {
    return (action.ValidTargets ?? []).map((target) => ({
      command: {
        action: "play_card",
        combat_card_index: action.CombatCardIndex,
        target_combat_id: target.CombatId
      },
      target
    }));
  }
  return [
    {
      command: {
        action: "play_card",
        combat_card_index: action.CombatCardIndex,
        target_combat_id: null
      },
      target: null
    }
  ];
}

function compareByScoreDesc(a: RankedAction, b: RankedAction): number {
  if (b.score !== a.score) {
    return b.score - a.score;
  }
  const aCard = a.action.CardId ?? "";
  const bCard = b.action.CardId ?? "";
  if (aCard !== bCard) {
    return aCard.localeCompare(bCard);
  }
  return Number(a.command.target_combat_id ?? -1) - Number(b.command.target_combat_id ?? -1);
}

function hasDefensivePlayableOption(
  state: ProbeState,
  actions: PlayCardActionOption[]
): boolean {
  const hand = state.LocalPlayer?.Hand ?? [];
  return actions.some((a) => {
    if (!a.CanPlayNow) {
      return false;
    }
    const card = hand.find((h) => h.CombatCardIndex === a.CombatCardIndex);
    if (!card) {
      return false;
    }
    const defensiveType = card.Type === "Skill" || card.Type === "Power";
    const selfTarget =
      a.TargetType === "Self" || a.TargetType === "None" || a.TargetType === "AllAllies";
    return defensiveType && selfTarget;
  });
}

export function rankActions(state: ProbeState, weights: WeightMap): RankedAction[] {
  const actionSpace = state.ActionSpace;
  if (!actionSpace || !state.LocalPlayer) {
    return [];
  }
  const incoming = estimateIncomingDamage(state);
  const hp = state.LocalPlayer.CurrentHp;
  const block = state.LocalPlayer.Block;
  const lethalThreat = incoming >= hp + block;
  const hasDefensiveOption = hasDefensivePlayableOption(
    state,
    actionSpace.PlayCardActions
  );

  const ranked: RankedAction[] = [];
  for (const action of actionSpace.PlayCardActions) {
    if (!action.CanPlayNow) {
      continue;
    }
    for (const candidate of enumerateCandidates(action)) {
      const features = buildActionFeatures(state, action, candidate.target);
      const contribution: Record<string, number> = {};
      for (const [featureKey, featureValue] of Object.entries(features)) {
        contribution[featureKey] = (weights[featureKey] ?? 0) * featureValue;
      }

      let score = dotProduct(features, weights);
      const notes: string[] = [];
      const card = state.LocalPlayer.Hand.find(
        (h) => h.CombatCardIndex === action.CombatCardIndex
      );
      const cardType = card?.Type ?? "Unknown";
      const isAttack = cardType === "Attack";
      const isDefensive = cardType === "Skill" || cardType === "Power";

      if (lethalThreat && hasDefensiveOption && isAttack) {
        score -= 1.8;
        notes.push("高致死威胁下惩罚纯进攻");
      }
      if (lethalThreat && isDefensive) {
        score += 0.9;
        notes.push("高致死威胁下鼓励防守");
      }

      if (action.RequiresTarget && !candidate.target) {
        score -= 10;
        notes.push("缺少目标");
      }

      ranked.push({
        action,
        command: candidate.command,
        target: candidate.target,
        score,
        features,
        contribution,
        notes
      });
    }
  }

  ranked.sort(compareByScoreDesc);
  return ranked;
}

export function buildActionReasons(ranked: RankedAction): string[] {
  const topContrib = Object.entries(ranked.contribution)
    .sort((a, b) => Math.abs(b[1]) - Math.abs(a[1]))
    .slice(0, 4);
  const reasons = topContrib.map(
    ([featureKey, value]) =>
      `${formatContributionKey(featureKey)}=${value.toFixed(3)}`
  );
  reasons.push(...ranked.notes);
  return reasons;
}
