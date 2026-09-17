import { ALL_TRIBES, CREATURES, CREATURES_BY_ID, RUNES, TAVERN_TABLE } from './content';
import type { Rng } from './rng';
import type { CreatureInstance, MatchState, PlayerState, RuneDef, Tribe } from './types';

let uidCounter = 0;
export function nextUid(prefix: string): string {
  uidCounter += 1;
  return `${prefix}_${uidCounter}`;
}

export function resetUidCounter(): void {
  uidCounter = 0;
}

export function tavernRow(tier: number) {
  return TAVERN_TABLE[Math.min(Math.max(tier, 1), 6) - 1];
}

export function createPool(tribes: Tribe[]): Record<string, number> {
  const pool: Record<string, number> = {};
  for (const c of CREATURES) {
    if (c.token) continue;
    if (c.tribe !== 'neutral' && !tribes.includes(c.tribe)) continue;
    pool[c.id] = tavernRow(c.tier).copiesInPool;
  }
  return pool;
}

function eligibleCreatureIds(state: MatchState, player: PlayerState): string[] {
  const ids: string[] = [];
  for (const c of CREATURES) {
    if (c.token) continue;
    if (c.tier > player.tavernTier) continue;
    if (c.tribe !== 'neutral' && !state.tribes.includes(c.tribe)) continue;
    ids.push(c.id);
  }
  return ids;
}

export function instantiateCreature(defId: string): CreatureInstance {
  const def = CREATURES_BY_ID[defId];
  if (!def) throw new Error(`Unknown creature ${defId}`);
  return {
    uid: nextUid('c'),
    defId,
    ascended: false,
    attack: def.attack,
    health: def.health,
    maxHealth: def.health,
    keywords: { ...def.keywords },
    survivedCombats: 0,
    awakened: false,
    chargeCounter: 0,
  };
}

function drawFromPool(state: MatchState, ids: string[], rng: Rng): string | null {
  const weighted: string[] = [];
  for (const id of ids) {
    const count = state.pool[id] ?? 0;
    for (let i = 0; i < count; i++) weighted.push(id);
  }
  if (weighted.length === 0) return null;
  const chosen = rng.pick(weighted);
  state.pool[chosen] = (state.pool[chosen] ?? 0) - 1;
  return chosen;
}

export function returnToPool(state: MatchState, defId: string): void {
  const def = CREATURES_BY_ID[defId];
  if (!def || def.token) return;
  state.pool[defId] = (state.pool[defId] ?? 0) + 1;
}

export function shopSizeFor(state: MatchState, player: PlayerState): number {
  let size = tavernRow(player.tavernTier).shopSize;
  if (state.phase === 'day' && hasCreatureOnBoard(player, ['grove', 'crystal'])) size += 1;
  if (hasAuraCreature(player, 'increaseShopSize')) size += 1;
  return size;
}

function hasCreatureOnBoard(player: PlayerState, tribes: Tribe[]): boolean {
  return Object.values(player.board).some((c) => c && tribes.includes(CREATURES_BY_ID[c.defId].tribe));
}

function hasAuraCreature(player: PlayerState, action: string): boolean {
  return Object.values(player.board).some((c) => {
    if (!c) return false;
    const def = CREATURES_BY_ID[c.defId];
    return def.effects.some((e) => e.trigger === 'aura' && e.action === action);
  });
}

export function refreshShop(state: MatchState, player: PlayerState, rng: Rng, keepFrozen = true): void {
  const kept: (CreatureInstance | null)[] = [];
  if (keepFrozen && player.shopFrozen) {
    for (const slot of player.shop) kept.push(slot);
  } else {
    for (const slot of player.shop) {
      if (slot) returnToPool(state, slot.defId);
    }
  }
  const size = shopSizeFor(state, player);
  const ids = eligibleCreatureIds(state, player);
  const newShop: (CreatureInstance | null)[] = kept.slice(0, size);
  while (newShop.length < size) {
    const defId = drawFromPool(state, ids, rng);
    newShop.push(defId ? instantiateCreature(defId) : null);
  }
  player.shop = newShop;
  player.shopFrozen = false;
}

export function updateUpgradeCost(player: PlayerState, discounted: boolean): void {
  if (discounted) {
    player.upgradeCost = Math.max(0, player.upgradeCost - 1);
  }
}

export function sellPrice(): { gold: number; shards: number } {
  return { gold: 1, shards: 1 };
}

export function buyPrice(): number {
  return 3;
}

export function rerollPrice(): number {
  return 1;
}

export function drawRuneOffers(state: MatchState, player: PlayerState, rng: Rng): void {
  player.runeShop = rng.shuffle(RUNES).slice(0, 3);
}

export function runeRankCost(rank: 1 | 2 | 3): number {
  return rank === 1 ? 3 : rank === 2 ? 5 : 8;
}

export function tryMerge(player: PlayerState): void {
  const groups = new Map<string, { uid: string; slot: 'hand' | string }[]>();
  player.hand.forEach((c) => {
    if (c.ascended) return;
    const arr = groups.get(c.defId) ?? [];
    arr.push({ uid: c.uid, slot: 'hand' });
    groups.set(c.defId, arr);
  });
  for (const [slot, c] of Object.entries(player.board)) {
    if (!c || c.ascended) continue;
    const arr = groups.get(c.defId) ?? [];
    arr.push({ uid: c.uid, slot });
    groups.set(c.defId, arr);
  }

  for (const [defId, entries] of groups) {
    if (entries.length < 3) continue;
    const boardEntry = entries.find((e) => e.slot !== 'hand');
    const three = entries.slice(0, 3);
    const targetSlot = boardEntry?.slot ?? 'hand';
    for (const entry of three) {
      if (entry.slot === 'hand') {
        player.hand = player.hand.filter((h) => h.uid !== entry.uid);
      } else {
        delete player.board[entry.slot as keyof typeof player.board];
      }
    }
    const def = CREATURES_BY_ID[defId];
    const ascended: CreatureInstance = {
      uid: nextUid('c'),
      defId,
      ascended: true,
      attack: def.attack * 2,
      health: def.health * 2,
      maxHealth: def.health * 2,
      keywords: { ...def.keywords },
      survivedCombats: 0,
      awakened: false,
      chargeCounter: 0,
    };
    if (targetSlot === 'hand') {
      player.hand.push(ascended);
    } else {
      (player.board as Record<string, CreatureInstance>)[targetSlot] = ascended;
    }
    grantStarGift(player);
  }
}

function grantStarGift(player: PlayerState): void {
  player.shards = Math.min(20, player.shards + 2);
}

export function allTribesList(): Tribe[] {
  return [...ALL_TRIBES];
}

export function pickLobbyTribes(rng: Rng): Tribe[] {
  return rng.shuffle(ALL_TRIBES).slice(0, 5) as Tribe[];
}

export function runeCostAffordable(player: PlayerState, rank: 1 | 2 | 3): boolean {
  return player.shards >= runeRankCost(rank);
}

export function applyRuneToCreature(rune: RuneDef, rank: 1 | 2 | 3, creature: CreatureInstance): void {
  const effect = rune.ranks[rank - 1];
  if (effect.attack) creature.attack += effect.attack;
  if (effect.health) {
    creature.health += effect.health;
    creature.maxHealth += effect.health;
  }
  if (effect.shell) {
    creature.keywords.shell = (Number(creature.keywords.shell) || 0) + effect.shell;
  }
  if (effect.keywords) {
    creature.keywords = { ...creature.keywords, ...effect.keywords };
  }
}
