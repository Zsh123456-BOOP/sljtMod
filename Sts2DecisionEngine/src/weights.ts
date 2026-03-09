import { EngineConfig, WeightMap } from "./types.js";
import { readJsonFile, writeJsonFileAtomic } from "./io.js";
import { Paths } from "./paths.js";

export const DefaultConfig: EngineConfig = {
  intervalMs: 250,
  cooldownMs: 900,
  learningRate: 0.015,
  l2Decay: 0.001,
  weightMin: -5,
  weightMax: 5
};

export const DefaultWeights: WeightMap = {
  bias: 0,
  attack_card: 0.5,
  skill_card: 0.3,
  power_card: 0.1,
  aoe_card: 0.2,
  self_cast: 0.05,
  target_required: -0.05,
  target_enemy: 0.2,
  target_is_attacker: 0.25,
  target_attack_damage: 0.2,
  target_low_hp: 0.3,
  cost_zero: 0.4,
  cost_one: 0.2,
  cost_high: -0.1,
  x_cost: 0.05,
  spend_energy: 0.35,
  spend_star: 0.02,
  stars_available: 0.1,
  low_hp: -0.2,
  incoming_threat: -0.35,
  defense_pressure: 0.45,
  threat_after_block: -0.3,
  lethal_threat: 0.6,
  defensive_candidate: 0.9,
  offensive_candidate: 0.25,
  focus_fire_bonus: 0.5,
  stop_attacker_bonus: 0.4,
  many_enemy_aoe: 0.35,
  energy_available: 0.1,
  can_play_now: 1.0
};

export function loadWeights(): WeightMap {
  const stored = readJsonFile<WeightMap>(Paths.weightsJson) ?? {};
  const merged: WeightMap = { ...DefaultWeights };
  for (const [key, value] of Object.entries(stored)) {
    if (!Number.isFinite(value)) {
      continue;
    }
    merged[key] = Number(value);
  }
  return merged;
}

export function saveWeights(weights: WeightMap): void {
  writeJsonFileAtomic(Paths.weightsJson, weights);
}

export function updateWeightsOnline(
  current: WeightMap,
  features: Record<string, number>,
  reward: number,
  config: EngineConfig
): WeightMap {
  const next: WeightMap = { ...current };
  for (const [key, featureValue] of Object.entries(features)) {
    const oldValue = Number(next[key] ?? 0);
    const decayPart = oldValue * (1 - config.l2Decay);
    const gradientPart = config.learningRate * reward * featureValue;
    const raw = decayPart + gradientPart;
    const clamped = Math.max(config.weightMin, Math.min(config.weightMax, raw));
    next[key] = clamped;
  }
  return next;
}
