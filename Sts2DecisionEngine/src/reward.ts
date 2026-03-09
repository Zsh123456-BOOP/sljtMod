import { ProbeState, TransitionReward } from "./types.js";

function sumEnemyHp(state: ProbeState): number {
  return (state.Enemies ?? []).reduce((acc, enemy) => acc + enemy.CurrentHp, 0);
}

export function computeTransitionReward(
  previous: ProbeState,
  next: ProbeState
): TransitionReward {
  const prevMe = previous.LocalPlayer;
  const nextMe = next.LocalPlayer;
  if (!prevMe || !nextMe) {
    return { reward: 0, components: {} };
  }

  const enemyHpDelta = sumEnemyHp(previous) - sumEnemyHp(next);
  const hpLoss = Math.max(0, prevMe.CurrentHp - nextMe.CurrentHp);
  const blockGain = Math.max(0, nextMe.Block - prevMe.Block);
  const energySpent = Math.max(0, (prevMe.Energy ?? 0) - (nextMe.Energy ?? 0));

  let combatEndBonus = 0;
  if (previous.IsInCombat && !next.IsInCombat) {
    combatEndBonus = nextMe.CurrentHp > 0 ? 15 : -20;
  }

  const components: Record<string, number> = {
    enemy_hp_delta: enemyHpDelta * 0.6,
    hp_loss: hpLoss * -1.4,
    block_gain: blockGain * 0.12,
    energy_spent: energySpent * 0.08,
    combat_end_bonus: combatEndBonus
  };

  const rewardRaw = Object.values(components).reduce((a, b) => a + b, 0);
  const reward = Math.max(-50, Math.min(50, rewardRaw));
  return { reward, components };
}
