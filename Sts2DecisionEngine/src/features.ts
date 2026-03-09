import { PlayCardActionOption, ProbeState } from "./types.js";
import {
  buildActionFeatures as buildActionFeaturesCore
} from "./pet-core/features.js";
import {
  estimateEnemyAttackDamage,
  estimateIncomingDamage,
  makeStateFingerprint
} from "./pet-core/metrics.js";

export { estimateEnemyAttackDamage, estimateIncomingDamage, makeStateFingerprint };

export function buildActionFeatures(state: ProbeState, action: PlayCardActionOption) {
  return buildActionFeaturesCore(state, action, null);
}
