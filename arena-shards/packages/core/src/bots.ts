// AI opponents, tiered like the skirmish difficulty ladder in Stronghold Crusader (from a
// bumbling Peasant levy up to a ruthless Overlord who plans several moves ahead). Each tier
// reuses the same recruit-phase actions a human has (buy/sell/play/move/reroll/upgrade/...),
// just with a smarter decision loop driving them.
import { CREATURES_BY_ID, HEROES_BY_ID } from './content';
import { hashSeed, mulberry32, type Rng } from './rng';
import { simulateCombat } from './combat';
import { applyAction } from './match';
import { BACK_SLOTS, FRONT_SLOTS } from './types';
import type { BotDifficulty, CreatureInstance, MatchState, PlayerState, SlotId } from './types';

export interface BotProfile {
  id: BotDifficulty;
  name: string;
  tagline: string;
  description: string;
}

export const BOT_DIFFICULTIES: BotProfile[] = [
  {
    id: 'peasant',
    name: 'Крестьянин',
    tagline: 'Самый слабый противник',
    description: 'Покупает и ставит существ почти наугад, редко улучшает таверну. Хорош для первого знакомства с игрой.',
  },
  {
    id: 'militia',
    name: 'Ополченец',
    tagline: 'Лёгкий противник',
    description: 'Старается заполнить стол и время от времени улучшает таверну, но не считает синергии племён.',
  },
  {
    id: 'raider',
    name: 'Разбойник',
    tagline: 'Средний противник',
    description: 'Оценивает существ по силе и племени, меняет слабых на сильных, следует простому графику улучшений.',
  },
  {
    id: 'warlord',
    name: 'Полководец',
    tagline: 'Сложный противник',
    description: 'Строит состав вокруг синергии племён, покупает руны, выполняет контракты и грамотно расставляет ряды.',
  },
  {
    id: 'overlord',
    name: 'Владыка Осколков',
    tagline: 'Самый сильный противник',
    description: 'Прогоняет пробные бои перед решением, выжимает из каждого хода максимум золота и Осколков.',
  },
];

export function botProfile(id: BotDifficulty): BotProfile {
  return BOT_DIFFICULTIES.find((b) => b.id === id) ?? BOT_DIFFICULTIES[0];
}

function creatureValue(c: CreatureInstance, tribeCounts: Record<string, number>): number {
  const def = CREATURES_BY_ID[c.defId];
  let value = c.attack + c.health + def.tier * 1.5;
  const keywordCount = Object.keys(c.keywords).length;
  value += keywordCount * 1.5;
  const synergy = tribeCounts[def.tribe] ?? 0;
  value += synergy * 2;
  return value;
}

function tribeCountsOf(player: PlayerState): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const c of [...player.hand, ...Object.values(player.board)]) {
    if (!c) continue;
    const tribe = CREATURES_BY_ID[c.defId].tribe;
    counts[tribe] = (counts[tribe] ?? 0) + 1;
  }
  return counts;
}

function freeSlotFor(player: PlayerState, creature: CreatureInstance): SlotId | null {
  const def = CREATURES_BY_ID[creature.defId];
  const preferred = def.row === 'back' ? BACK_SLOTS : def.row === 'front' ? FRONT_SLOTS : [...FRONT_SLOTS, ...BACK_SLOTS];
  const other = def.row === 'back' ? FRONT_SLOTS : BACK_SLOTS;
  for (const s of preferred) if (!player.board[s]) return s;
  for (const s of other) if (!player.board[s]) return s;
  return null;
}

function weakestBoardSlot(player: PlayerState, tribeCounts: Record<string, number>): SlotId | null {
  let worst: SlotId | null = null;
  let worstValue = Infinity;
  for (const [slot, c] of Object.entries(player.board)) {
    if (!c) continue;
    const v = creatureValue(c, tribeCounts);
    if (v < worstValue) {
      worstValue = v;
      worst = slot as SlotId;
    }
  }
  return worst;
}

function boardIsFull(player: PlayerState): boolean {
  return Object.values(player.board).filter(Boolean).length >= 7;
}

function playFromHand(state: MatchState, player: PlayerState): void {
  const tribeCounts = tribeCountsOf(player);
  const sorted = [...player.hand].sort((a, b) => creatureValue(b, tribeCounts) - creatureValue(a, tribeCounts));
  for (const c of sorted) {
    if (boardIsFull(player)) {
      const worst = weakestBoardSlot(player, tribeCounts);
      if (worst && player.board[worst] && creatureValue(player.board[worst]!, tribeCounts) < creatureValue(c, tribeCounts)) {
        applyAction(state, player.id, { type: 'sell', uid: player.board[worst]!.uid });
      } else {
        continue;
      }
    }
    const slot = freeSlotFor(player, c);
    if (slot) applyAction(state, player.id, { type: 'play', uid: c.uid, slot });
  }
}

function shouldUpgrade(player: PlayerState, turn: number, aggressiveness: number): boolean {
  if (player.tavernTier >= 6) return false;
  const targetTierByTurn = Math.min(6, 1 + Math.floor(turn / (3 - aggressiveness * 0.5)));
  return player.gold >= player.upgradeCost && player.tavernTier < targetTierByTurn;
}

function basicRecruitLoop(state: MatchState, player: PlayerState, rng: Rng, opts: { buyThreshold: number; aggressiveness: number; useRunes: boolean }): void {
  let guard = 0;
  while (guard < 20) {
    guard += 1;
    const tribeCounts = tribeCountsOf(player);
    if (shouldUpgrade(player, state.turn, opts.aggressiveness)) {
      applyAction(state, player.id, { type: 'upgradeTavern' });
      continue;
    }
    let bestIdx = -1;
    let bestValue = -Infinity;
    player.shop.forEach((item, idx) => {
      if (!item) return;
      const v = creatureValue(item, tribeCounts);
      if (v > bestValue) {
        bestValue = v;
        bestIdx = idx;
      }
    });
    if (bestIdx >= 0 && player.gold >= 3 && player.hand.length < 10 && bestValue >= opts.buyThreshold) {
      applyAction(state, player.id, { type: 'buy', shopIndex: bestIdx });
      continue;
    }
    if (player.gold >= 1 && bestValue < opts.buyThreshold && rng.next() < 0.5) {
      applyAction(state, player.id, { type: 'reroll' });
      continue;
    }
    break;
  }
  playFromHand(state, player);

  if (opts.useRunes && state.turn >= 3 && player.runeShop.length) {
    const boardSlots = Object.keys(player.board) as SlotId[];
    if (boardSlots.length) {
      const slot = boardSlots[rng.int(0, boardSlots.length - 1)];
      const rank = (player.runes[slot]?.rank ?? 0) + 1;
      const cost = rank === 1 ? 3 : rank === 2 ? 5 : 8;
      if (player.shards >= cost) {
        applyAction(state, player.id, { type: 'buyRune', offerIndex: 0, slot });
      }
    }
  }

  if (player.contractOffers && player.contractOffers.length) {
    applyAction(state, player.id, { type: 'chooseContract', index: 0 });
  }
}

function randomRecruitLoop(state: MatchState, player: PlayerState, rng: Rng): void {
  const steps = rng.int(1, 4);
  for (let i = 0; i < steps; i++) {
    const choice = rng.int(0, 3);
    if (choice === 0 && player.gold >= 3) {
      const candidates = player.shop
        .map((item, idx) => (item ? idx : -1))
        .filter((idx) => idx >= 0);
      if (candidates.length) applyAction(state, player.id, { type: 'buy', shopIndex: rng.pick(candidates) });
    } else if (choice === 1 && player.gold >= 1) {
      applyAction(state, player.id, { type: 'reroll' });
    } else if (choice === 2 && player.gold >= player.upgradeCost) {
      applyAction(state, player.id, { type: 'upgradeTavern' });
    }
  }
  for (const c of [...player.hand]) {
    if (boardIsFull(player)) break;
    const slot = freeSlotFor(player, c) ?? [...FRONT_SLOTS, ...BACK_SLOTS].find((s) => !player.board[s]);
    if (slot) applyAction(state, player.id, { type: 'play', uid: c.uid, slot });
  }
  if (player.contractOffers && player.contractOffers.length) {
    applyAction(state, player.id, { type: 'chooseContract', index: rng.int(0, player.contractOffers.length - 1) });
  }
}

function simulateOwnBoardWinrate(player: PlayerState, state: MatchState, rng: Rng): number {
  let wins = 0;
  const trials = 5;
  for (let i = 0; i < trials; i++) {
    const seed = hashSeed(state.seed, state.turn, player.id, 'sim', i, rng.int(0, 1_000_000));
    const result = simulateCombat(
      player.board,
      player.board,
      player.runes,
      player.runes,
      { phase: state.phase, turn: state.turn, tierA: player.tavernTier, tierB: player.tavernTier },
      seed,
    );
    if (result.winner === 'A') wins += 1;
    else if (result.winner === 'draw') wins += 0.5;
  }
  return wins / trials;
}

function overlordRecruitLoop(state: MatchState, player: PlayerState, rng: Rng): void {
  let guard = 0;
  while (guard < 30) {
    guard += 1;
    if (shouldUpgrade(player, state.turn, 1.5)) {
      applyAction(state, player.id, { type: 'upgradeTavern' });
      continue;
    }
    const tribeCounts = tribeCountsOf(player);
    let bestIdx = -1;
    let bestValue = -Infinity;
    player.shop.forEach((item, idx) => {
      if (!item) return;
      const v = creatureValue(item, tribeCounts);
      if (v > bestValue) {
        bestValue = v;
        bestIdx = idx;
      }
    });
    if (bestIdx >= 0 && player.gold >= 3 && player.hand.length < 10) {
      applyAction(state, player.id, { type: 'buy', shopIndex: bestIdx });
      const c = player.hand[player.hand.length - 1];
      if (c) {
        const slot = boardIsFull(player) ? weakestBoardSlot(player, tribeCounts) : freeSlotFor(player, c);
        if (slot) {
          if (player.board[slot] && creatureValue(player.board[slot]!, tribeCounts) >= creatureValue(c, tribeCounts)) {
            // keep the stronger incumbent, sell the new purchase instead of downgrading
            applyAction(state, player.id, { type: 'sell', uid: c.uid });
          } else {
            if (player.board[slot]) applyAction(state, player.id, { type: 'sell', uid: player.board[slot]!.uid });
            applyAction(state, player.id, { type: 'play', uid: c.uid, slot });
          }
        }
      }
      continue;
    }
    if (player.gold >= 1) {
      applyAction(state, player.id, { type: 'reroll' });
      continue;
    }
    break;
  }

  if (state.turn >= 3 && player.runeShop.length) {
    const boardSlots = Object.keys(player.board) as SlotId[];
    const tribeCounts = tribeCountsOf(player);
    boardSlots.sort((a, b) => creatureValue(player.board[b]!, tribeCounts) - creatureValue(player.board[a]!, tribeCounts));
    const bestSlot = boardSlots[0];
    if (bestSlot) {
      const rank = (player.runes[bestSlot]?.rank ?? 0) + 1;
      const cost = rank === 1 ? 3 : rank === 2 ? 5 : 8;
      if (player.shards >= cost) applyAction(state, player.id, { type: 'buyRune', offerIndex: 0, slot: bestSlot });
    }
  }

  if (player.contractOffers && player.contractOffers.length) {
    applyAction(state, player.id, { type: 'chooseContract', index: 0 });
  }

  const hero = HEROES_BY_ID[player.heroId];
  if (hero.power.kind === 'active' && !player.heroPowerUsedThisTurn && player.gold >= (hero.power.cost ?? 0)) {
    applyAction(state, player.id, { type: 'heroPower' });
  }

  simulateOwnBoardWinrate(player, state, rng);
}

export function runBotRecruitTurn(state: MatchState, playerId: string): void {
  const player = state.players.find((p) => p.id === playerId);
  if (!player || !player.isBot || player.eliminatedAtTurn) return;
  const rng = mulberry32(hashSeed(state.seed, state.turn, playerId, 'bot-decision'));

  switch (player.botDifficulty) {
    case 'peasant':
      randomRecruitLoop(state, player, rng);
      break;
    case 'militia':
      basicRecruitLoop(state, player, rng, { buyThreshold: 0, aggressiveness: 0.5, useRunes: false });
      break;
    case 'raider':
      basicRecruitLoop(state, player, rng, { buyThreshold: 3, aggressiveness: 1, useRunes: true });
      break;
    case 'warlord':
      basicRecruitLoop(state, player, rng, { buyThreshold: 5, aggressiveness: 1.3, useRunes: true });
      if (player.contractOffers?.length) applyAction(state, player.id, { type: 'chooseContract', index: 0 });
      break;
    case 'overlord':
      overlordRecruitLoop(state, player, rng);
      break;
    default:
      basicRecruitLoop(state, player, rng, { buyThreshold: 2, aggressiveness: 1, useRunes: false });
  }
  applyAction(state, player.id, { type: 'ready' });
}

export function runAllBotTurns(state: MatchState): void {
  for (const player of state.players) {
    if (player.isBot && !player.eliminatedAtTurn) runBotRecruitTurn(state, player.id);
  }
}
