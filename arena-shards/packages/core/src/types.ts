export type Tribe =
  | 'crystal'
  | 'swamp'
  | 'spark'
  | 'ash'
  | 'night'
  | 'grove'
  | 'neutral';

export type Keyword =
  | 'shell'
  | 'ranged'
  | 'breach'
  | 'stealth'
  | 'rush'
  | 'toxin'
  | 'link'
  | 'charge';

export type Trigger =
  | 'onPlay'
  | 'onDeath'
  | 'onSell'
  | 'onBuy'
  | 'onCombatStart'
  | 'onAttack'
  | 'onAllyAttack'
  | 'onAllyDeath'
  | 'onShellBroken'
  | 'onRecruitEnd'
  | 'aura';

export type Phase = 'day' | 'dusk' | 'night';

export type Row = 'front' | 'back';

export type SlotId = 'F0' | 'F1' | 'F2' | 'F3' | 'B0' | 'B1' | 'B2';

export const FRONT_SLOTS: SlotId[] = ['F0', 'F1', 'F2', 'F3'];
export const BACK_SLOTS: SlotId[] = ['B0', 'B1', 'B2'];

export interface EffectCondition {
  phase?: Phase;
  tribe?: Tribe;
  row?: Row;
  everyNth?: number;
}

export interface EffectDef {
  trigger: Trigger;
  condition?: EffectCondition;
  action: string;
  target: string;
  params: Record<string, number | string>;
}

export interface AwakenDef {
  after: number;
  effects: EffectDef[];
}

export interface CreatureDef {
  id: string;
  name: string;
  tribe: Tribe;
  tier: 1 | 2 | 3 | 4 | 5 | 6;
  attack: number;
  health: number;
  row: Row | 'any';
  keywords: Partial<Record<Keyword, number | true>>;
  effects: EffectDef[];
  awaken?: AwakenDef;
  token?: boolean;
}

export type RuneId =
  | 'resilience'
  | 'blade'
  | 'aegis'
  | 'echo'
  | 'breach'
  | 'ambush'
  | 'growth'
  | 'onslaught';

export interface RuneRankEffect {
  attack?: number;
  health?: number;
  shell?: number;
  keywords?: Partial<Record<Keyword, number | true>>;
  echoDoubleFirst?: boolean;
  echoDoubleAll?: boolean;
  permanentGrowth?: { attack: number; health: number };
  onslaughtDoubleFirstRound?: boolean;
  breachDoubleAttack?: boolean;
}

export interface RuneDef {
  id: RuneId;
  name: string;
  description: string;
  ranks: [RuneRankEffect, RuneRankEffect, RuneRankEffect];
  backRowOnly?: boolean;
}

export interface RuneInstance {
  defId: RuneId;
  rank: 1 | 2 | 3;
}

export interface HeroPowerDef {
  kind: 'active' | 'passive';
  cost?: number;
  action: string;
  params?: Record<string, number | string>;
}

export interface HeroDef {
  id: string;
  name: string;
  armor: number;
  description: string;
  power: HeroPowerDef;
}

export interface ContractDef {
  id: string;
  name: string;
  condition: string;
  params?: Record<string, number | string>;
  durationTurns: number;
  reward: string;
  rewardParams?: Record<string, number | string>;
}

export interface CreatureInstance {
  uid: string;
  defId: string;
  ascended: boolean;
  attack: number;
  health: number;
  maxHealth: number;
  keywords: Partial<Record<Keyword, number | true>>;
  survivedCombats: number;
  awakened: boolean;
  chargeCounter?: number;
}

export interface ContractState {
  defId: string;
  progress: number;
  expiresTurn: number;
  chosenAtTurn: number;
}

export type BotDifficulty =
  | 'peasant'
  | 'militia'
  | 'raider'
  | 'warlord'
  | 'overlord';

export interface PlayerState {
  id: string;
  name: string;
  isBot: boolean;
  botDifficulty?: BotDifficulty;
  heroId: string;
  heroPowerUsedThisTurn: boolean;
  health: number;
  armor: number;
  gold: number;
  shards: number;
  tavernTier: number;
  upgradeCost: number;
  shop: (CreatureInstance | null)[];
  shopFrozen: boolean;
  hand: CreatureInstance[];
  board: Partial<Record<SlotId, CreatureInstance>>;
  runes: Partial<Record<SlotId, RuneInstance>>;
  runeShop: (RuneDef | null)[];
  contract?: ContractState;
  contractOffers?: string[];
  place?: number;
  eliminatedAtTurn?: number;
  lastOpponentId?: string;
  ready?: boolean;
  runeForgeRerolled?: boolean;
  rerollCount: number;
  actionCount: number;
}

export interface MatchState {
  matchId: string;
  seed: number;
  turn: number;
  phase: Phase;
  tribes: Tribe[];
  pool: Record<string, number>;
  players: PlayerState[];
  pairings: [string, string][];
  ghostBoard?: { board: Partial<Record<SlotId, CreatureInstance>>; runes: Partial<Record<SlotId, RuneInstance>> };
  recruitTimerSec: number;
  finished: boolean;
  log: string[];
}

export type CombatEventType =
  | 'runeApplied'
  | 'auraApplied'
  | 'attack'
  | 'damage'
  | 'shellAbsorb'
  | 'toxinKill'
  | 'death'
  | 'summon'
  | 'buff'
  | 'shift'
  | 'keywordRemoved'
  | 'combatEnd';

export interface CombatEvent {
  type: CombatEventType;
  [key: string]: unknown;
}

export interface CombatResult {
  winner: 'A' | 'B' | 'draw';
  damage: number;
  events: CombatEvent[];
  survivorsA: CreatureInstance[];
  survivorsB: CreatureInstance[];
}

export type PlayerAction =
  | { type: 'buy'; shopIndex: number }
  | { type: 'sell'; uid: string }
  | { type: 'play'; uid: string; slot: SlotId }
  | { type: 'move'; uid: string; slot: SlotId }
  | { type: 'reroll' }
  | { type: 'freeze' }
  | { type: 'upgradeTavern' }
  | { type: 'buyRune'; offerIndex: number; slot: SlotId }
  | { type: 'heroPower'; targetUid?: string }
  | { type: 'chooseContract'; index: number }
  | { type: 'ready' };

export interface ActionResult {
  state: MatchState;
  error?: string;
}
