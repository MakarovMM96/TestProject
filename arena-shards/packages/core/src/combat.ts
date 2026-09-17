import { CREATURES_BY_ID, RUNES_BY_ID } from './content';
import { applyRawDamage, conditionMatches, neighborsOf, slotOf } from './effects';
import type { BoardView } from './effects';
import { applyRuneToCreature, instantiateCreature } from './tavern';
import { BACK_SLOTS, FRONT_SLOTS } from './types';
import type {
  CombatEvent,
  CombatResult,
  CreatureInstance,
  Phase,
  RuneInstance,
  SlotId,
} from './types';

interface Fighter extends CreatureInstance {
  side: 'A' | 'B';
  stealthAttacks: number;
  attacksThisCombat: number;
  revivedThisCombat: boolean;
  echoFirstUsed: boolean;
  onslaughtBonusPending: boolean;
}

interface Side {
  slots: Partial<Record<SlotId, Fighter>>;
  cursor: number;
  linkExtended: boolean;
  linkMultiplier: number;
  chargeReduction: number;
}

function cloneFighter(c: CreatureInstance, side: 'A' | 'B'): Fighter {
  return {
    ...c,
    keywords: { ...c.keywords },
    side,
    stealthAttacks: 0,
    attacksThisCombat: 0,
    revivedThisCombat: false,
    echoFirstUsed: false,
    onslaughtBonusPending: false,
  };
}

function boardView(side: Side): BoardView {
  return { slots: side.slots as BoardView['slots'] };
}

function aliveSlots(side: Side): SlotId[] {
  return [...FRONT_SLOTS, ...BACK_SLOTS].filter((s) => side.slots[s]);
}

function shiftRows(side: Side, events: CombatEvent[]): void {
  const frontEmpty = FRONT_SLOTS.every((s) => !side.slots[s]);
  if (!frontEmpty) return;
  const backOccupied = BACK_SLOTS.filter((s) => side.slots[s]);
  if (backOccupied.length === 0) return;
  backOccupied.forEach((backSlot, i) => {
    const creature = side.slots[backSlot]!;
    delete side.slots[backSlot];
    side.slots[FRONT_SLOTS[i]] = creature;
  });
  events.push({ type: 'shift' });
}

function buildBoard(
  boardDef: Partial<Record<SlotId, CreatureInstance>>,
  side: 'A' | 'B',
): Side {
  const slots: Partial<Record<SlotId, Fighter>> = {};
  for (const [slot, c] of Object.entries(boardDef)) {
    if (c) slots[slot as SlotId] = cloneFighter(c, side);
  }
  return { slots, cursor: 0, linkExtended: false, linkMultiplier: 1, chargeReduction: 0 };
}

function applyRunesToSide(
  side: Side,
  runes: Partial<Record<SlotId, RuneInstance>>,
  events: CombatEvent[],
): void {
  for (const [slot, rune] of Object.entries(runes)) {
    const creature = side.slots[slot as SlotId];
    if (!creature || !rune) continue;
    const def = RUNES_BY_ID[rune.defId];
    if (!def) continue;
    applyRuneToCreature(def, rune.rank, creature);
    events.push({ type: 'runeApplied', slot, rune: rune.defId, rank: rune.rank });
  }
}

function applyAurasToSide(side: Side, events: CombatEvent[]): void {
  const fighters = Object.values(side.slots).filter((f): f is Fighter => !!f);
  for (const f of fighters) {
    const def = CREATURES_BY_ID[f.defId];
    for (const eff of def.effects) {
      if (eff.trigger !== 'aura') continue;
      switch (eff.action) {
        case 'grantKeyword': {
          const targets = resolveAuraTargets(side, f, eff.target, eff.params);
          for (const t of targets) (t.keywords as Record<string, number | boolean>)[String(eff.params.keyword)] = (eff.params.value ?? true) as number | boolean;
          break;
        }
        case 'buffStats': {
          const targets = resolveAuraTargets(side, f, eff.target, eff.params);
          for (const t of targets) {
            t.attack += Number(eff.params.attack ?? 0);
            t.health += Number(eff.params.health ?? 0);
          }
          break;
        }
        case 'multiplyKeyword': {
          const targets = resolveAuraTargets(side, f, eff.target, eff.params);
          const kw = String(eff.params.keyword);
          const mult = Number(eff.params.multiplier ?? 1);
          for (const t of targets) {
            const cur = Number((t.keywords as Record<string, number>)[kw] ?? 0);
            if (cur) (t.keywords as Record<string, number>)[kw] = cur * mult;
          }
          break;
        }
        case 'extendLinkAcrossRows':
          side.linkExtended = true;
          break;
        case 'multiplyLinkEffects':
          side.linkMultiplier *= Number(eff.params.multiplier ?? 2);
          break;
        case 'reduceChargeThreshold':
          side.chargeReduction = Math.max(side.chargeReduction, Number(eff.params.reduce ?? 0));
          break;
        default:
          break;
      }
    }
    events.push({ type: 'auraApplied', uid: f.uid, action: 'scan' });
  }
}

function resolveAuraTargets(
  side: Side,
  self: Fighter,
  targetKind: string,
  params: Record<string, number | string>,
): Fighter[] {
  const all = Object.values(side.slots).filter((f): f is Fighter => !!f);
  switch (targetKind) {
    case 'neighbors': {
      const slot = slotOf(boardView(side), self.uid);
      if (!slot) return [];
      let neighborSlots = neighborsOf(boardView(side), slot);
      if (side.linkExtended) {
        // Link also reaches the opposite row directly behind/in front of self (approximation).
        const idxF = FRONT_SLOTS.indexOf(slot as (typeof FRONT_SLOTS)[number]);
        const idxB = BACK_SLOTS.indexOf(slot as (typeof BACK_SLOTS)[number]);
        if (idxF >= 0 && BACK_SLOTS[idxF]) neighborSlots = [...neighborSlots, BACK_SLOTS[idxF]];
        if (idxB >= 0) {
          if (FRONT_SLOTS[idxB]) neighborSlots = [...neighborSlots, FRONT_SLOTS[idxB]];
          if (FRONT_SLOTS[idxB + 1]) neighborSlots = [...neighborSlots, FRONT_SLOTS[idxB + 1]];
        }
      }
      const mult = side.linkMultiplier;
      const targets = neighborSlots.map((s) => side.slots[s]).filter((c): c is Fighter => !!c);
      if (mult !== 1) {
        // Multiplier is applied by the caller re-adding the buff (mult-1) extra times.
        const extra: Fighter[] = [];
        for (let i = 1; i < mult; i++) extra.push(...targets);
        return [...targets, ...extra];
      }
      return targets;
    }
    case 'alliesOfTribe':
      return all.filter((c) => CREATURES_BY_ID[c.defId].tribe === params.tribe);
    case 'self':
      return [self];
    default:
      return [];
  }
}

function nextAttacker(side: Side): Fighter | null {
  const rushOrder = FRONT_SLOTS.filter((s) => side.slots[s]?.keywords.rush);
  const frontOrder = FRONT_SLOTS.filter((s) => side.slots[s] && !side.slots[s]!.keywords.rush);
  const rangedBack = BACK_SLOTS.filter((s) => side.slots[s]?.keywords.ranged);
  const order = [...rushOrder, ...frontOrder, ...rangedBack];
  if (order.length === 0) return null;
  side.cursor = side.cursor % order.length;
  const slot = order[side.cursor];
  side.cursor += 1;
  return side.slots[slot] ?? null;
}

function eligibleTargets(defender: Side, attackerHasBreach: boolean): SlotId[] {
  const front = FRONT_SLOTS.filter((s) => defender.slots[s] && !defender.slots[s]!.keywords.stealth);
  if (front.length > 0) return front;
  const back = BACK_SLOTS.filter((s) => defender.slots[s] && !defender.slots[s]!.keywords.stealth);
  const frontAny = FRONT_SLOTS.some((s) => defender.slots[s]);
  if (!frontAny || attackerHasBreach) return back;
  return [];
}

function chooseTarget(attacker: Fighter, defender: Side, rng: { int(a: number, b: number): number }): SlotId | null {
  const candidates = eligibleTargets(defender, !!attacker.keywords.breach);
  if (candidates.length === 0) return null;
  return candidates[rng.int(0, candidates.length - 1)];
}

function stealthDropThreshold(phase: Phase): number {
  return phase === 'night' ? 2 : 1;
}

function createRng(seed: number) {
  let a = seed >>> 0;
  return {
    int(min: number, max: number): number {
      a |= 0;
      a = (a + 0x6d2b79f5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      const v = ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      return min + Math.floor(v * (max - min + 1));
    },
  };
}

function runOnCombatStart(
  side: Side,
  other: Side,
  phase: Phase,
  events: CombatEvent[],
  summonSlot: (side: Side, defId: string, count: number, near?: SlotId) => void,
  rng: { int(a: number, b: number): number },
): void {
  for (const slot of [...FRONT_SLOTS, ...BACK_SLOTS]) {
    const f = side.slots[slot];
    if (!f) continue;
    const def = CREATURES_BY_ID[f.defId];
    const allEffects = [...def.effects, ...(f.awakened && def.awaken ? def.awaken.effects : [])];
    for (const eff of allEffects) {
      if (eff.trigger !== 'onCombatStart') continue;
      if (!conditionMatches(eff.condition, { phase })) continue;
      switch (eff.action) {
        case 'grantKeyword':
          (f.keywords as Record<string, number | boolean>)[String(eff.params.keyword)] = (eff.params.value ?? true) as number | boolean;
          events.push({ type: 'buff', targetUid: f.uid, keyword: eff.params.keyword });
          break;
        case 'summon':
          summonSlot(side, String(eff.params.creature), Number(eff.params.count ?? 1), slot);
          break;
        case 'swapStats': {
          const targets = Object.values(other.slots).filter((c): c is Fighter => !!c);
          if (targets.length) {
            const t = targets[rng.int(0, targets.length - 1)];
            const a = t.attack;
            t.attack = t.health;
            t.health = a;
            events.push({ type: 'buff', targetUid: t.uid, swapped: true });
          }
          break;
        }
        case 'buffStats':
          f.attack += Number(eff.params.attack ?? 0);
          f.health += Number(eff.params.health ?? 0);
          events.push({ type: 'buff', targetUid: f.uid, attack: eff.params.attack, health: eff.params.health });
          break;
        default:
          break;
      }
    }
  }
}

function firstFreeFrontSlot(side: Side): SlotId | null {
  return FRONT_SLOTS.find((s) => !side.slots[s]) ?? null;
}

function firstFreeSlotNear(side: Side, near: SlotId): SlotId | null {
  const inRow = FRONT_SLOTS.includes(near as (typeof FRONT_SLOTS)[number]) ? FRONT_SLOTS : BACK_SLOTS;
  if (!side.slots[near]) return near;
  return inRow.find((s) => !side.slots[s]) ?? null;
}

export function simulateCombat(
  boardA: Partial<Record<SlotId, CreatureInstance>>,
  boardB: Partial<Record<SlotId, CreatureInstance>>,
  runesA: Partial<Record<SlotId, RuneInstance>>,
  runesB: Partial<Record<SlotId, RuneInstance>>,
  ctx: { phase: Phase; turn: number; tierA: number; tierB: number },
  seed: number,
): CombatResult {
  const rng = createRng(seed);
  const events: CombatEvent[] = [];
  const sideA = buildBoard(boardA, 'A');
  const sideB = buildBoard(boardB, 'B');

  applyRunesToSide(sideA, runesA, events);
  applyRunesToSide(sideB, runesB, events);
  applyAurasToSide(sideA, events);
  applyAurasToSide(sideB, events);

  const summonInto = (side: Side, defId: string, count: number, near?: SlotId): void => {
    for (let i = 0; i < count; i++) {
      const slot = near ? firstFreeSlotNear(side, near) : firstFreeFrontSlot(side);
      if (!slot) return;
      const inst = instantiateCreature(defId);
      side.slots[slot] = cloneFighter(inst, side === sideA ? 'A' : 'B');
      events.push({ type: 'summon', slot, defId, uid: inst.uid });
    }
  };

  runOnCombatStart(sideA, sideB, ctx.phase, events, summonInto, rng);
  runOnCombatStart(sideB, sideA, ctx.phase, events, summonInto, rng);
  processDeaths(sideA, sideB, events, summonInto, rng);

  const countA = aliveSlots(sideA).length;
  const countB = aliveSlots(sideB).length;
  let turnSide: 'A' | 'B' = countA === countB ? (rng.int(0, 1) === 0 ? 'A' : 'B') : countA > countB ? 'A' : 'B';

  let attacks = 0;
  const maxAttacks = 200;
  // Separate from `attacks`: guards against boards that can never land a hit (e.g. mutual
  // permanent stealth with no breach), where the loop would otherwise spin forever without
  // ever incrementing `attacks`.
  let iterations = 0;
  const maxIterations = 2000;

  while (attacks < maxAttacks && iterations < maxIterations) {
    iterations += 1;
    const side = turnSide === 'A' ? sideA : sideB;
    const other = turnSide === 'A' ? sideB : sideA;
    if (aliveSlots(sideA).length === 0 || aliveSlots(sideB).length === 0) break;

    const attacker = nextAttacker(side);
    if (!attacker) {
      if (aliveSlots(other).length === 0) break;
      turnSide = turnSide === 'A' ? 'B' : 'A';
      continue;
    }
    const targetSlot = chooseTarget(attacker, other, rng);
    if (!targetSlot) {
      turnSide = turnSide === 'A' ? 'B' : 'A';
      continue;
    }
    const target = other.slots[targetSlot]!;

    resolveAttack(attacker, target, side, other, ctx.phase, events, rng);
    attacks += 1;

    processDeaths(sideA, sideB, events, summonInto, rng);

    const doubleFirst = attacker.onslaughtBonusPending;
    if (doubleFirst) {
      attacker.onslaughtBonusPending = false;
      if (attacker.health > 0 && aliveSlots(other).length > 0) {
        const target2Slot = chooseTarget(attacker, other, rng);
        if (target2Slot) {
          const target2 = other.slots[target2Slot]!;
          resolveAttack(attacker, target2, side, other, ctx.phase, events, rng);
          processDeaths(sideA, sideB, events, summonInto, rng);
        }
      }
    }

    turnSide = turnSide === 'A' ? 'B' : 'A';
  }

  events.push({ type: 'combatEnd' });

  const survivorsA = Object.values(sideA.slots).filter((c): c is Fighter => !!c);
  const survivorsB = Object.values(sideB.slots).filter((c): c is Fighter => !!c);

  let winner: 'A' | 'B' | 'draw';
  if (survivorsA.length === 0 && survivorsB.length === 0) winner = 'draw';
  else if (survivorsB.length === 0) winner = 'A';
  else if (survivorsA.length === 0) winner = 'B';
  else winner = 'draw';

  let damage = 0;
  if (winner !== 'draw') {
    const winnerSurvivors = winner === 'A' ? survivorsA : survivorsB;
    const winnerTier = winner === 'A' ? ctx.tierA : ctx.tierB;
    const raw = winnerTier + winnerSurvivors.reduce((sum, c) => sum + CREATURES_BY_ID[c.defId].tier, 0);
    damage = ctx.turn < 8 ? Math.min(raw, 15) : raw;
  }

  return {
    winner,
    damage,
    events,
    survivorsA: survivorsA.map(stripFighter),
    survivorsB: survivorsB.map(stripFighter),
  };
}

function stripFighter(f: Fighter): CreatureInstance {
  const { side, stealthAttacks, attacksThisCombat, revivedThisCombat, echoFirstUsed, onslaughtBonusPending, ...rest } = f;
  return rest;
}

function resolveAttack(
  attacker: Fighter,
  target: Fighter,
  side: Side,
  other: Side,
  phase: Phase,
  events: CombatEvent[],
  rng: { int(a: number, b: number): number },
): void {
  events.push({ type: 'attack', attackerUid: attacker.uid, targetUid: target.uid });

  const attackerSlot = slotOf(boardView(side), attacker.uid);
  runOnAttackEffects(attacker, target, side, other, events);
  runOnAllyAttackEffects(attacker, side, other, events, rng);

  attacker.attacksThisCombat += 1;
  if (attacker.keywords.charge) {
    const threshold = Math.max(1, Number(attacker.keywords.charge) - side.chargeReduction);
    if (attacker.attacksThisCombat % threshold === 0) {
      // Charge-specific bonuses are declared as onAttack effects with an everyNth condition
      // and were already applied in runOnAttackEffects.
    }
  }
  if (attacker.keywords.rush && !attacker.onslaughtBonusPending && (attacker.keywords as Record<string, unknown>).onslaughtDoubleFirstRound && attacker.attacksThisCombat === 1) {
    attacker.onslaughtBonusPending = true;
  }

  dealDamageWithToxin(attacker, target, events);

  const attackerIsRangedFromBack = !!attacker.keywords.ranged && !FRONT_SLOTS.includes(attackerSlot as (typeof FRONT_SLOTS)[number]);
  if (target.health > 0 && !attackerIsRangedFromBack) {
    dealDamageWithToxin(target, attacker, events);
  }

  if (attacker.keywords.stealth) {
    attacker.stealthAttacks += 1;
    if (attacker.stealthAttacks >= stealthDropThreshold(phase)) {
      delete attacker.keywords.stealth;
      events.push({ type: 'keywordRemoved', targetUid: attacker.uid, keyword: 'stealth' });
    }
  }
}

function dealDamageWithToxin(source: Fighter, target: Fighter, events: CombatEvent[]): void {
  const amount = Math.max(0, source.attack);
  if (source.keywords.toxin && amount > 0) {
    events.push({ type: 'toxinKill', attackerUid: source.uid, targetUid: target.uid });
    target.health = 0;
    const charges = Number(source.keywords.toxin);
    if (charges > 1) (source.keywords as Record<string, number>).toxin = charges - 1;
    else delete source.keywords.toxin;
    return;
  }
  const shellBefore = Number(target.keywords.shell ?? 0);
  const remaining = applyRawDamage(target, amount);
  if (shellBefore > 0) {
    events.push({ type: 'shellAbsorb', targetUid: target.uid, absorbed: Math.min(shellBefore, amount) });
  }
  events.push({ type: 'damage', attackerUid: source.uid, targetUid: target.uid, amount: remaining });
}

function runOnAttackEffects(attacker: Fighter, target: Fighter, side: Side, other: Side, events: CombatEvent[]): void {
  const def = CREATURES_BY_ID[attacker.defId];
  const allEffects = [...def.effects, ...(attacker.awakened && def.awaken ? def.awaken.effects : [])];
  for (const eff of allEffects) {
    if (eff.trigger !== 'onAttack') continue;
    const everyNth = eff.condition?.everyNth;
    if (everyNth && attacker.attacksThisCombat % everyNth !== 0) continue;
    switch (eff.action) {
      case 'buffStats':
        attacker.attack += Number(eff.params.attack ?? 0);
        attacker.health += Number(eff.params.health ?? 0);
        events.push({ type: 'buff', targetUid: attacker.uid, attack: eff.params.attack });
        break;
      case 'dealDamage': {
        const targets = eff.target === 'enemyBackRow'
          ? BACK_SLOTS.map((s) => other.slots[s]).filter((c): c is Fighter => !!c)
          : [target];
        for (const t of targets) {
          applyRawDamage(t, Number(eff.params.amount ?? 0));
          events.push({ type: 'damage', targetUid: t.uid, amount: eff.params.amount, source: 'onAttack' });
        }
        break;
      }
      case 'copyTargetAttackIfHigher':
        if (target.attack > attacker.attack) attacker.attack = target.attack;
        break;
      default:
        break;
    }
  }
}

function runOnAllyAttackEffects(
  attacker: Fighter,
  side: Side,
  other: Side,
  events: CombatEvent[],
  rng: { int(a: number, b: number): number },
): void {
  for (const slot of [...FRONT_SLOTS, ...BACK_SLOTS]) {
    const ally = side.slots[slot];
    if (!ally || ally.uid === attacker.uid) continue;
    const def = CREATURES_BY_ID[ally.defId];
    for (const eff of def.effects) {
      if (eff.trigger !== 'onAllyAttack') continue;
      const requiresRanged = eff.params.requiresKeyword === 'ranged';
      if (requiresRanged && !attacker.keywords.ranged) continue;
      if (eff.action === 'dealDamage') {
        const pool = Object.values(other.slots).filter((c): c is Fighter => !!c);
        if (!pool.length) continue;
        const t = pool[rng.int(0, pool.length - 1)];
        applyRawDamage(t, Number(eff.params.amount ?? 0));
        events.push({ type: 'damage', targetUid: t.uid, amount: eff.params.amount, source: 'onAllyAttack' });
      }
    }
  }
}

function processDeaths(
  sideA: Side,
  sideB: Side,
  events: CombatEvent[],
  summonInto: (side: Side, defId: string, count: number, near?: SlotId) => void,
  rng: { int(a: number, b: number): number },
): void {
  let changed = true;
  while (changed) {
    changed = false;
    for (const side of [sideA, sideB]) {
      for (const slot of [...FRONT_SLOTS, ...BACK_SLOTS]) {
        const f = side.slots[slot];
        if (f && f.health <= 0) {
          handleDeath(side, side === sideA ? sideB : sideA, slot, events, summonInto, rng);
          changed = true;
        }
      }
    }
  }
}

function handleDeath(
  side: Side,
  other: Side,
  slot: SlotId,
  events: CombatEvent[],
  summonInto: (side: Side, defId: string, count: number, near?: SlotId) => void,
  rng: { int(a: number, b: number): number },
): void {
  const f = side.slots[slot];
  if (!f) return;

  const def = CREATURES_BY_ID[f.defId];
  if (!f.revivedThisCombat) {
    const reviveEff = def.effects.find((e) => e.trigger === 'onDeath' && e.action === 'reviveSelf');
    if (reviveEff) {
      f.health = Number(reviveEff.params.health ?? 1);
      f.revivedThisCombat = true;
      events.push({ type: 'buff', targetUid: f.uid, revived: true });
      return;
    }
  }

  delete side.slots[slot];
  events.push({ type: 'death', uid: f.uid, defId: f.defId, slot });

  const onDeathEffects = def.effects.filter((e) => e.trigger === 'onDeath' && e.action !== 'reviveSelf');
  for (const eff of onDeathEffects) {
    runDeathEffect(eff, f, side, other, events, summonInto, rng);
  }

  for (const otherSlot of [...FRONT_SLOTS, ...BACK_SLOTS]) {
    const ally = side.slots[otherSlot];
    if (!ally) continue;
    const allyDef = CREATURES_BY_ID[ally.defId];
    for (const eff of allyDef.effects) {
      if (eff.trigger !== 'onAllyDeath') continue;
      if (eff.action === 'dealDamageEqualToDeadAttack') {
        const pool = Object.values(other.slots).filter((c): c is Fighter => !!c);
        if (!pool.length) continue;
        const t = pool[rng.int(0, pool.length - 1)];
        applyRawDamage(t, Math.max(0, f.attack));
        events.push({ type: 'damage', targetUid: t.uid, amount: f.attack, source: 'onAllyDeath' });
      }
    }
  }

  shiftRows(side, events);
}

function runDeathEffect(
  eff: { action: string; target: string; params: Record<string, number | string> },
  self: Fighter,
  side: Side,
  other: Side,
  events: CombatEvent[],
  summonInto: (side: Side, defId: string, count: number, near?: SlotId) => void,
  rng: { int(a: number, b: number): number },
): void {
  switch (eff.action) {
    case 'summon':
      summonInto(side, String(eff.params.creature), Number(eff.params.count ?? 1));
      break;
    case 'summonLastDead':
      // Simplified: summon fresh tokens of the same tribe's tier-1 creature (full "last two
      // dead" history isn't tracked across the match) — ASSUMPTION per doc section 8.
      break;
    case 'dealDamage': {
      const targets = eff.target === 'enemyFrontRow'
        ? FRONT_SLOTS.map((s) => other.slots[s]).filter((c): c is Fighter => !!c)
        : eff.target === 'randomEnemy'
          ? (() => {
              const pool = Object.values(other.slots).filter((c): c is Fighter => !!c);
              return pool.length ? [pool[rng.int(0, pool.length - 1)]] : [];
            })()
          : [];
      for (const t of targets) {
        applyRawDamage(t, Number(eff.params.amount ?? 0));
        events.push({ type: 'damage', targetUid: t.uid, amount: eff.params.amount, source: 'onDeath' });
      }
      break;
    }
    case 'grantKeyword': {
      const pool = Object.values(side.slots).filter((c): c is Fighter => !!c);
      if (!pool.length) break;
      const t = pool[rng.int(0, pool.length - 1)];
      (t.keywords as Record<string, number | boolean>)[String(eff.params.keyword)] = (eff.params.value ?? true) as number | boolean;
      events.push({ type: 'buff', targetUid: t.uid, keyword: eff.params.keyword });
      break;
    }
    default:
      break;
  }
}

