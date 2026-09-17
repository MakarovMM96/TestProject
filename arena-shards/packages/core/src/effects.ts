// Generic trigger/action interpreter for EffectDef, shared by tavern/match (recruit-phase
// triggers) and combat (in-fight triggers). Actions not expressible as a simple stat/keyword
// change get their own case here, per the design doc's own "add a new action + handler" escape
// hatch (section 8).
import { CREATURES_BY_ID } from './content';
import { BACK_SLOTS, FRONT_SLOTS } from './types';
import type {
  CreatureInstance,
  EffectCondition,
  EffectDef,
  Phase,
  Row,
  SlotId,
  Tribe,
} from './types';

export interface BoardView {
  slots: Partial<Record<SlotId, CreatureInstance>>;
}

export interface EffectRuntime {
  phase: Phase;
  own: BoardView;
  enemy: BoardView;
  rng: { next(): number; int(min: number, max: number): number };
  log: (event: Record<string, unknown>) => void;
  // Extra hooks a handler may need, populated per call-site.
  extra?: Record<string, unknown>;
}

export function neighborsOf(board: BoardView, slot: SlotId): SlotId[] {
  const front = FRONT_SLOTS.indexOf(slot as (typeof FRONT_SLOTS)[number]);
  if (front >= 0) {
    const res: SlotId[] = [];
    if (front > 0) res.push(FRONT_SLOTS[front - 1]);
    if (front < FRONT_SLOTS.length - 1) res.push(FRONT_SLOTS[front + 1]);
    return res.filter((s) => board.slots[s]);
  }
  const back = BACK_SLOTS.indexOf(slot as (typeof BACK_SLOTS)[number]);
  if (back >= 0) {
    const res: SlotId[] = [];
    if (back > 0) res.push(BACK_SLOTS[back - 1]);
    if (back < BACK_SLOTS.length - 1) res.push(BACK_SLOTS[back + 1]);
    // Back row is offset by half a cell, so B(i) also neighbors F(i) and F(i+1).
    if (FRONT_SLOTS[back]) res.push(FRONT_SLOTS[back]);
    if (FRONT_SLOTS[back + 1]) res.push(FRONT_SLOTS[back + 1]);
    return res.filter((s) => board.slots[s]);
  }
  return [];
}

export function slotOf(board: BoardView, uid: string): SlotId | null {
  for (const [slot, c] of Object.entries(board.slots)) {
    if (c && c.uid === uid) return slot as SlotId;
  }
  return null;
}

export function findSlot(board: BoardView, creature: CreatureInstance): SlotId | null {
  return slotOf(board, creature.uid);
}

export function conditionMatches(cond: EffectCondition | undefined, ctx: { phase: Phase; row?: Row }): boolean {
  if (!cond) return true;
  if (cond.phase && cond.phase !== ctx.phase) return false;
  if (cond.row && ctx.row && cond.row !== ctx.row) return false;
  return true;
}

function tribeOf(c: CreatureInstance): Tribe {
  return CREATURES_BY_ID[c.defId].tribe;
}

function alive(board: BoardView): CreatureInstance[] {
  return Object.values(board.slots).filter((c): c is CreatureInstance => !!c);
}

function rowOf(board: BoardView, uid: string): Row | null {
  const slot = slotOf(board, uid);
  if (!slot) return null;
  return (FRONT_SLOTS as readonly string[]).includes(slot) ? 'front' : 'back';
}

export function resolveTargets(
  targetKind: string,
  self: CreatureInstance,
  rt: EffectRuntime,
  params: Record<string, number | string>,
): CreatureInstance[] {
  const selfSlot = slotOf(rt.own, self.uid);
  switch (targetKind) {
    case 'self':
      return [self];
    case 'neighbors':
      return selfSlot ? neighborsOf(rt.own, selfSlot).map((s) => rt.own.slots[s]!).filter(Boolean) : [];
    case 'alliesOfTribe':
      return alive(rt.own).filter((c) => tribeOf(c) === params.tribe);
    case 'alliesOfTribeOnRunedSlots':
      return alive(rt.own).filter((c) => tribeOf(c) === params.tribe);
    case 'alliesWithDefId':
      return alive(rt.own).filter((c) => c.defId === params.defId);
    case 'randomAlly': {
      const pool = alive(rt.own);
      return pool.length ? [pool[rt.rng.int(0, pool.length - 1)]] : [];
    }
    case 'randomOtherAlly': {
      const pool = alive(rt.own).filter((c) => c.uid !== self.uid);
      return pool.length ? [pool[rt.rng.int(0, pool.length - 1)]] : [];
    }
    case 'randomAllyOfTribe': {
      const pool = alive(rt.own).filter((c) => tribeOf(c) === params.tribe);
      return pool.length ? [pool[rt.rng.int(0, pool.length - 1)]] : [];
    }
    case 'randomEnemy': {
      const pool = alive(rt.enemy);
      return pool.length ? [pool[rt.rng.int(0, pool.length - 1)]] : [];
    }
    case 'enemyFrontRow':
      return FRONT_SLOTS.map((s) => rt.enemy.slots[s]).filter((c): c is CreatureInstance => !!c);
    case 'enemyBackRow':
      return BACK_SLOTS.map((s) => rt.enemy.slots[s]).filter((c): c is CreatureInstance => !!c);
    default:
      return [];
  }
}

export interface ActionHandlerContext {
  rt: EffectRuntime;
  self: CreatureInstance;
  effect: EffectDef;
  extra?: Record<string, unknown>;
}

export type ActionHandler = (ctx: ActionHandlerContext) => void;

function grantKeyword(target: CreatureInstance, keyword: string, value: number | boolean): void {
  (target.keywords as Record<string, number | boolean>)[keyword] = value;
}

function buffStats(target: CreatureInstance, attack = 0, health = 0): void {
  target.attack += attack;
  target.health += health;
  if (health > 0) target.maxHealth += health;
}

export const actionHandlers: Record<string, ActionHandler> = {
  grantKeyword: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    const kw = String(effect.params.keyword);
    const value = effect.params.value ?? true;
    for (const t of targets) grantKeyword(t, kw, value as number | boolean);
    rt.log({ type: 'buff', targets: targets.map((t) => t.uid), keyword: kw });
  },
  buffStats: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    const atk = Number(effect.params.attack ?? 0);
    const hp = Number(effect.params.health ?? 0);
    for (const t of targets) buffStats(t, atk, hp);
    rt.log({ type: 'buff', targets: targets.map((t) => t.uid), attack: atk, health: hp });
  },
  multiplyKeyword: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    const kw = String(effect.params.keyword) as 'shell';
    const mult = Number(effect.params.multiplier ?? 1);
    for (const t of targets) {
      const cur = Number(t.keywords[kw] ?? 0);
      if (cur) (t.keywords as Record<string, number>)[kw] = cur * mult;
    }
  },
  dealDamage: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    const requires = effect.params.requiresKeyword as string | undefined;
    const amount = Number(effect.params.amount ?? 0);
    for (const t of targets) {
      if (requires && !t.keywords[requires as keyof typeof t.keywords]) continue;
      applyRawDamage(t, amount);
      rt.log({ type: 'damage', targetUid: t.uid, amount, source: 'effect' });
    }
  },
  gainShards: ({ rt, effect }) => {
    rt.log({ type: 'gainShards', amount: Number(effect.params.amount ?? 0) });
  },
  increaseShopSize: () => {
    /* handled declaratively by tavern.shopSizeFor via hasAuraCreature */
  },
  boostNeighborRunes: () => {
    /* applied where runes are read (see match.ts rune value lookups) */
  },
  buffNextBuy: ({ rt, effect }) => {
    rt.log({ type: 'buffNextBuy', attack: effect.params.attack, health: effect.params.health });
  },
  consumeAllyForStats: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    if (!targets.length) return;
    const victim = targets[0];
    self.attack += victim.attack;
    self.health += victim.health;
    self.maxHealth += victim.health;
    const slot = slotOf(rt.own, victim.uid);
    if (slot) delete rt.own.slots[slot];
    rt.log({ type: 'buff', targets: [self.uid], attack: victim.attack, health: victim.health, consumed: victim.uid });
  },
  reviveSelf: ({ rt, self, effect, extra }) => {
    if (extra?.oncePerCombatUsed) return;
    self.health = Number(effect.params.health ?? 1);
    if (extra) extra.revived = true;
    rt.log({ type: 'buff', targets: [self.uid], health: self.health, revived: true });
  },
  dealDamageEqualToDeadAttack: ({ rt, self, effect, extra }) => {
    const amount = Number(extra?.deadAttack ?? 0);
    if (amount <= 0) return;
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    for (const t of targets) {
      applyRawDamage(t, amount);
      rt.log({ type: 'damage', targetUid: t.uid, amount, source: 'ashlord' });
    }
  },
  buffRandomAllyOfTribe: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    const isNight = rt.phase === 'night';
    const atk = Number((isNight ? effect.params.nightAttack : effect.params.attack) ?? 0);
    const hp = Number((isNight ? effect.params.nightHealth : effect.params.health) ?? 0);
    for (const t of targets) buffStats(t, atk, hp);
  },
  copyTargetAttackIfHigher: ({ self, extra }) => {
    const targetAttack = Number(extra?.currentTargetAttack ?? 0);
    if (targetAttack > self.attack) self.attack = targetAttack;
  },
  swapStats: ({ rt, self, effect }) => {
    const targets = resolveTargets(effect.target, self, rt, effect.params);
    for (const t of targets) {
      const a = t.attack;
      t.attack = t.health;
      t.health = a;
    }
  },
  forcePhase: () => {
    /* read where phase-dependent conditions are evaluated for this creature's owner */
  },
  extendLinkAcrossRows: () => {
    /* read by neighborsOf callers via linkExtended flag set in combat.ts */
  },
  multiplyLinkEffects: () => {
    /* read by link aura application in combat.ts */
  },
  reduceChargeThreshold: () => {
    /* read where charge thresholds are evaluated in combat.ts */
  },
  summon: ({ rt, self, effect }) => {
    // Handled in combat.ts / match.ts (needs slot allocation + instantiation helpers
    // that would create a circular import here).
    rt.log({ type: 'summonRequest', creature: effect.params.creature, count: effect.params.count ?? 1, source: self.uid });
  },
  summonLastDead: ({ rt, self, effect }) => {
    rt.log({ type: 'summonLastDeadRequest', tribe: effect.params.tribe, count: effect.params.count ?? 1, health: effect.params.health, source: self.uid });
  },
};

export function applyRawDamage(target: CreatureInstance, amount: number): number {
  let remaining = amount;
  const shell = Number(target.keywords.shell ?? 0);
  if (shell > 0) {
    const absorbed = Math.min(shell, remaining);
    (target.keywords as Record<string, number>).shell = shell - absorbed;
    remaining -= absorbed;
    if (target.keywords.shell === 0) delete target.keywords.shell;
  }
  if (remaining > 0) target.health -= remaining;
  return remaining;
}
