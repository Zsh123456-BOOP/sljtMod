import { EnemySnapshot, IntentSnapshot, ProbeState } from "../types.js";

function parseIntFromLabel(text: string | null | undefined): number {
  if (!text) {
    return 0;
  }
  const matched = text.match(/-?\d+/);
  if (!matched) {
    return 0;
  }
  const value = Number.parseInt(matched[0], 10);
  return Number.isFinite(value) ? value : 0;
}

function isAttackIntent(intent: IntentSnapshot): boolean {
  return (intent.IntentType ?? "").toLowerCase().includes("attack");
}

export function estimateEnemyAttackDamage(enemy: EnemySnapshot): number {
  let total = 0;
  for (const intent of enemy.Intents ?? []) {
    if (!isAttackIntent(intent)) {
      continue;
    }
    total += Math.max(0, parseIntFromLabel(intent.Label));
  }
  return total;
}

export function estimateIncomingDamage(state: ProbeState): number {
  return (state.Enemies ?? []).reduce(
    (acc, enemy) => acc + estimateEnemyAttackDamage(enemy),
    0
  );
}

export function makeStateFingerprint(state: ProbeState): string {
  const me = state.LocalPlayer;
  const handSig = (me?.Hand ?? [])
    .map((c) => `${c.IdEntry}:${c.CurrentEnergyCost ?? -1}`)
    .sort()
    .join("|");
  const enemySig = (state.Enemies ?? [])
    .map((e) => `${e.ModelId}:${e.CurrentHp}:${estimateEnemyAttackDamage(e)}`)
    .sort()
    .join("|");
  return [
    state.IsInCombat ? "1" : "0",
    state.CurrentSide ?? "Unknown",
    me?.CharacterId ?? "None",
    String(me?.CurrentHp ?? 0),
    String(me?.Block ?? 0),
    String(me?.Energy ?? 0),
    String(me?.Stars ?? 0),
    handSig,
    enemySig
  ].join(";");
}
