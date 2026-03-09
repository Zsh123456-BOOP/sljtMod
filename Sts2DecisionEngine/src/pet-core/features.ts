import {
  CardSnapshot,
  FeatureMap,
  PlayCardActionOption,
  ProbeState,
  TargetOption
} from "../types.js";
import { estimateEnemyAttackDamage, estimateIncomingDamage } from "./metrics.js";

function normalize(value: number, max: number): number {
  if (max <= 0) {
    return 0;
  }
  return Math.max(0, Math.min(1, value / max));
}

function findCard(state: ProbeState, action: PlayCardActionOption): CardSnapshot | null {
  const hand = state.LocalPlayer?.Hand ?? [];
  if (action.CombatCardIndex != null) {
    const matched = hand.find((h) => h.CombatCardIndex === action.CombatCardIndex);
    if (matched) {
      return matched;
    }
  }
  return hand.find((h) => h.IdEntry === action.CardId) ?? null;
}

export function buildActionFeatures(
  state: ProbeState,
  action: PlayCardActionOption,
  target: TargetOption | null
): FeatureMap {
  const me = state.LocalPlayer;
  const card = findCard(state, action);
  const enemy = target
    ? (state.Enemies ?? []).find((e) => e.CombatId === target.CombatId) ?? null
    : null;

  const energy = Math.max(0, Number(me?.Energy ?? 0));
  const stars = Math.max(0, Number(me?.Stars ?? 0));
  const hp = Math.max(1, Number(me?.CurrentHp ?? 1));
  const maxHp = Math.max(1, Number(me?.MaxHp ?? 1));
  const block = Math.max(0, Number(me?.Block ?? 0));
  const hpRatio = hp / maxHp;

  const incoming = estimateIncomingDamage(state);
  const threatAfterBlock = Math.max(0, incoming - block);
  const lethalThreat = incoming >= hp + block;
  const enemiesAlive = Math.max(0, (state.Enemies ?? []).length);

  const cardType = card?.Type ?? "Unknown";
  const targetType = action.TargetType ?? "None";
  const energyCost = Math.max(0, Number(card?.CurrentEnergyCost ?? 0));
  const starCost = Math.max(0, Number(card?.CurrentStarCost ?? 0));
  const isXCost = Boolean(card?.CostsX);

  const isAttack = cardType === "Attack" ? 1 : 0;
  const isSkill = cardType === "Skill" ? 1 : 0;
  const isPower = cardType === "Power" ? 1 : 0;
  const isAoe = targetType === "AllEnemies" ? 1 : 0;
  const isSelfCast =
    targetType === "Self" || targetType === "None" || targetType === "AllAllies"
      ? 1
      : 0;
  const isDefensiveCandidate = isSkill * isSelfCast;

  const targetEnemy = target && target.Side === "Enemy" ? 1 : 0;
  const targetAttackDamage = enemy ? estimateEnemyAttackDamage(enemy) : 0;
  const targetLowHp = enemy ? 1 - normalize(enemy.CurrentHp, 80) : 0;
  const targetIsAttacker = targetAttackDamage > 0 ? 1 : 0;

  return {
    bias: 1,
    can_play_now: action.CanPlayNow ? 1 : 0,
    attack_card: isAttack,
    skill_card: isSkill,
    power_card: isPower,
    aoe_card: isAoe,
    self_cast: isSelfCast,
    target_required: action.RequiresTarget ? 1 : 0,
    target_enemy: targetEnemy,
    target_is_attacker: targetIsAttacker,
    target_attack_damage: normalize(targetAttackDamage, 20),
    target_low_hp: targetLowHp,
    low_hp: hpRatio < 0.45 ? 1 : 0,
    lethal_threat: lethalThreat ? 1 : 0,
    incoming_threat: normalize(incoming, 40),
    threat_after_block: normalize(threatAfterBlock, 35),
    defense_pressure: incoming > block ? 1 : 0,
    defensive_candidate: isDefensiveCandidate,
    offensive_candidate: isAttack * (targetEnemy || isAoe),
    focus_fire_bonus: isAttack * targetLowHp,
    stop_attacker_bonus: isAttack * targetIsAttacker * normalize(incoming, 30),
    many_enemy_aoe: isAoe * (enemiesAlive >= 3 ? 1 : 0),
    energy_available: normalize(energy, 3),
    stars_available: stars > 0 ? 1 : 0,
    cost_zero: energyCost === 0 ? 1 : 0,
    cost_one: energyCost === 1 ? 1 : 0,
    cost_high: energyCost >= 2 ? 1 : 0,
    spend_energy: normalize(energyCost, 3),
    spend_star: normalize(starCost, 3),
    x_cost: isXCost ? 1 : 0
  };
}
