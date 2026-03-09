export interface ProbeState {
  ModId: string;
  TimestampUtc: string;
  IsInCombat: boolean;
  CurrentSide: string;
  LocalPlayer?: PlayerSnapshot | null;
  Enemies: EnemySnapshot[];
  ActionSpace?: ActionSpaceSnapshot;
  RunContext?: RunContextSnapshot;
}

export interface PlayerSnapshot {
  NetId: number;
  CharacterId: string;
  Name: string;
  CurrentHp: number;
  MaxHp: number;
  Block: number;
  Gold: number;
  MaxEnergy: number;
  Energy?: number | null;
  Stars?: number | null;
  Hand: CardSnapshot[];
}

export interface CardSnapshot {
  CombatCardIndex?: number | null;
  IdEntry: string;
  Title: string;
  Type: string;
  TargetType: string;
  CurrentEnergyCost?: number | null;
  CurrentStarCost?: number | null;
  CostsX?: boolean | null;
  CanPlay: boolean;
  CanPlayEvaluated: boolean;
  UnplayableReason?: string | null;
  PreventedByModelId?: string | null;
}

export interface EnemySnapshot {
  CombatId?: number | null;
  Name: string;
  ModelId: string;
  CurrentHp: number;
  MaxHp: number;
  Block: number;
  IsHittable: boolean;
  Intents: IntentSnapshot[];
}

export interface IntentSnapshot {
  IntentType: string;
  Label?: string | null;
}

export interface ActionSpaceSnapshot {
  IsPlayerTurn: boolean;
  PlayerActionsDisabled: boolean;
  ActionCount: number;
  PlayableActionCount: number;
  SupportedActions: string[];
  PlayCardActions: PlayCardActionOption[];
}

export interface PlayCardActionOption {
  CombatCardIndex?: number | null;
  CardId: string;
  CardTitle: string;
  TargetType: string;
  RequiresTarget: boolean;
  CanPlayByRules: boolean;
  CanPlayNow: boolean;
  UnplayableReason: string;
  PreventedByModelId?: string | null;
  ValidTargets: TargetOption[];
  ExampleCommand: CommandPayload;
}

export interface TargetOption {
  CombatId?: number | null;
  ModelId: string;
  Name: string;
  Side: string;
  IsAlive: boolean;
  IsHittable: boolean;
}

export interface CommandPayload {
  action: "play_card";
  combat_card_index?: number | null;
  target_combat_id?: number | null;
}

export interface RunContextSnapshot {
  CurrentActId?: string;
  CurrentActTitle?: string;
  ActFloor?: number;
  TotalFloor?: number;
  CurrentRoomType?: string;
  CurrentRoomModelId?: string | null;
}

export type FeatureMap = Record<string, number>;
export type WeightMap = Record<string, number>;

export interface RankedAction {
  action: PlayCardActionOption;
  command: CommandPayload;
  target?: TargetOption | null;
  score: number;
  features: FeatureMap;
  contribution: Record<string, number>;
  notes: string[];
}

export interface AdviceSnapshot {
  timestampUtc: string;
  mode: "advise" | "autoplay";
  summary: string;
  topActions: AdviceAction[];
  context: AdviceContext;
}

export interface AdviceAction {
  rank: number;
  cardId: string;
  cardTitle: string;
  targetCombatId?: number | null;
  targetName?: string;
  score: number;
  reasons: string[];
  command: CommandPayload;
}

export interface AdviceContext {
  playerHp: number;
  playerMaxHp: number;
  block: number;
  energy: number;
  stars: number;
  incomingDamageEstimate: number;
  enemiesAlive: number;
}

export interface TransitionReward {
  reward: number;
  components: Record<string, number>;
}

export interface EngineConfig {
  intervalMs: number;
  cooldownMs: number;
  learningRate: number;
  l2Decay: number;
  weightMin: number;
  weightMax: number;
}
