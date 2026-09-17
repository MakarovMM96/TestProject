import { CONTRACTS, CREATURES_BY_ID, HEROES_BY_ID, TAVERN_TABLE } from './content';
import { mulberry32, hashSeed, type Rng } from './rng';
import { simulateCombat } from './combat';
import {
  buyPrice,
  createPool,
  drawRuneOffers,
  instantiateCreature,
  pickLobbyTribes,
  refreshShop,
  rerollPrice,
  returnToPool,
  runeRankCost,
  sellPrice,
  shopSizeFor,
  tavernRow,
  tryMerge,
  runeCostAffordable,
} from './tavern';
import type {
  ActionResult,
  BotDifficulty,
  CreatureInstance,
  HeroDef,
  MatchState,
  Phase,
  PlayerAction,
  PlayerState,
  SlotId,
} from './types';

export interface PlayerConfig {
  id: string;
  name: string;
  isBot: boolean;
  botDifficulty?: BotDifficulty;
  heroId: string;
}

function phaseForTurn(turn: number): Phase {
  const m = ((turn - 1) % 9) + 1;
  if (m <= 3) return 'day';
  if (m <= 6) return 'dusk';
  return 'night';
}

function goldForTurn(turn: number): number {
  return Math.min(10, 2 + turn);
}

function timerForTurn(turn: number): number {
  return Math.min(90, 45 + (turn - 1) * 5);
}

function createPlayer(cfg: PlayerConfig): PlayerState {
  const hero: HeroDef = HEROES_BY_ID[cfg.heroId];
  return {
    id: cfg.id,
    name: cfg.name,
    isBot: cfg.isBot,
    botDifficulty: cfg.botDifficulty,
    heroId: cfg.heroId,
    heroPowerUsedThisTurn: false,
    health: 40,
    armor: hero.armor,
    gold: 0,
    shards: 0,
    tavernTier: 1,
    upgradeCost: tavernRow(2).upgradeCost,
    shop: [],
    shopFrozen: false,
    hand: [],
    board: {},
    runes: {},
    runeShop: [],
    ready: false,
    rerollCount: 0,
    actionCount: 0,
  };
}

export function createMatch(seed: number, players: PlayerConfig[]): MatchState {
  const setupRng = mulberry32(seed);
  const tribes = pickLobbyTribes(setupRng);
  const state: MatchState = {
    matchId: `m_${seed}`,
    seed,
    turn: 0,
    phase: 'day',
    tribes,
    pool: createPool(tribes),
    players: players.map(createPlayer),
    pairings: [],
    recruitTimerSec: 45,
    finished: false,
    log: [],
  };
  startTurn(state);
  return state;
}

export function startTurn(state: MatchState): void {
  state.turn += 1;
  state.phase = phaseForTurn(state.turn);
  state.recruitTimerSec = timerForTurn(state.turn);

  for (const player of state.players) {
    if (player.eliminatedAtTurn) continue;
    player.gold = goldForTurn(state.turn);
    player.heroPowerUsedThisTurn = false;
    player.ready = false;
    if (state.turn > 1 && !player.shopFrozen) {
      player.upgradeCost = Math.max(0, player.upgradeCost - 1);
    }
    const rng = mulberry32(hashSeed(state.seed, state.turn, player.id, 'shop'));
    refreshShop(state, player, rng);
    if (state.turn >= 3) {
      const runeRng = mulberry32(hashSeed(state.seed, state.turn, player.id, 'runes'));
      drawRuneOffers(state, player, runeRng);
      player.runeForgeRerolled = false;
    }
    if ([4, 8, 12].includes(state.turn) && !player.contract) {
      const hero = HEROES_BY_ID[player.heroId];
      const extra = hero.power.action === 'extraContractChoice' ? Number(hero.power.params?.extraOptions ?? 0) : 0;
      const contractRng = mulberry32(hashSeed(state.seed, state.turn, player.id, 'contracts'));
      player.contractOffers = contractRng.shuffle(CONTRACTS.map((c) => c.id)).slice(0, 3 + extra);
    }
  }
  state.log.push(`Ход ${state.turn}: фаза ${state.phase}`);
}

function findCreature(player: PlayerState, uid: string): { location: 'hand' | SlotId; creature: CreatureInstance } | null {
  const inHand = player.hand.find((c) => c.uid === uid);
  if (inHand) return { location: 'hand', creature: inHand };
  for (const [slot, c] of Object.entries(player.board)) {
    if (c && c.uid === uid) return { location: slot as SlotId, creature: c };
  }
  return null;
}

function removeCreature(player: PlayerState, uid: string): CreatureInstance | null {
  const found = findCreature(player, uid);
  if (!found) return null;
  if (found.location === 'hand') {
    player.hand = player.hand.filter((c) => c.uid !== uid);
  } else {
    delete player.board[found.location];
  }
  return found.creature;
}

function boardCount(player: PlayerState): number {
  return Object.values(player.board).filter(Boolean).length;
}

export function applyAction(state: MatchState, playerId: string, action: PlayerAction): ActionResult {
  const player = state.players.find((p) => p.id === playerId);
  if (!player) return { state, error: 'unknown_player' };
  if (player.eliminatedAtTurn) return { state, error: 'eliminated' };
  player.actionCount += 1;
  const actionRng = mulberry32(hashSeed(state.seed, state.turn, playerId, 'action', player.actionCount));

  switch (action.type) {
    case 'buy': {
      const item = player.shop[action.shopIndex];
      if (!item) return { state, error: 'empty_slot' };
      if (player.gold < buyPrice()) return { state, error: 'not_enough_gold' };
      if (player.hand.length >= 10) return { state, error: 'hand_full' };
      player.gold -= buyPrice();
      player.shop[action.shopIndex] = null;
      player.hand.push(item);
      runOnBuy(player, item);
      tryMerge(player);
      break;
    }
    case 'sell': {
      const creature = removeCreature(player, action.uid);
      if (!creature) return { state, error: 'not_found' };
      const price = sellPrice();
      player.gold = Math.min(10, player.gold + price.gold);
      player.shards = Math.min(20, player.shards + price.shards);
      returnToPool(state, creature.defId);
      runOnSell(player, creature);
      break;
    }
    case 'play': {
      const idx = player.hand.findIndex((c) => c.uid === action.uid);
      if (idx === -1) return { state, error: 'not_in_hand' };
      if (player.board[action.slot]) return { state, error: 'slot_occupied' };
      if (boardCount(player) >= 7) return { state, error: 'board_full' };
      const [creature] = player.hand.splice(idx, 1);
      player.board[action.slot] = creature;
      runOnPlay(player, creature, action.slot, actionRng);
      tryMerge(player);
      break;
    }
    case 'move': {
      const found = findCreature(player, action.uid);
      if (!found || found.location === 'hand') return { state, error: 'not_on_board' };
      const occupant = player.board[action.slot];
      delete player.board[found.location];
      player.board[action.slot] = found.creature;
      if (occupant) player.board[found.location] = occupant;
      break;
    }
    case 'reroll': {
      const freeDusk = state.phase === 'dusk' && !player.runeForgeRerolled;
      const cost = freeDusk ? 0 : rerollPrice();
      if (player.gold < cost) return { state, error: 'not_enough_gold' };
      player.gold -= cost;
      if (freeDusk) player.runeForgeRerolled = true;
      player.rerollCount += 1;
      const rng = mulberry32(hashSeed(state.seed, state.turn, player.id, 'reroll', player.rerollCount));
      refreshShop(state, player, rng, false);
      break;
    }
    case 'freeze': {
      player.shopFrozen = !player.shopFrozen;
      break;
    }
    case 'upgradeTavern': {
      if (player.tavernTier >= 6) return { state, error: 'max_tier' };
      if (player.gold < player.upgradeCost) return { state, error: 'not_enough_gold' };
      player.gold -= player.upgradeCost;
      player.tavernTier += 1;
      player.upgradeCost = player.tavernTier < 6 ? tavernRow(player.tavernTier + 1).upgradeCost : 0;
      break;
    }
    case 'buyRune': {
      if (state.turn < 3) return { state, error: 'forge_locked' };
      const rune = player.runeShop[action.offerIndex];
      if (!rune) return { state, error: 'no_rune' };
      if (!player.board[action.slot]) return { state, error: 'slot_empty' };
      const rank = ((player.runes[action.slot]?.rank ?? 0) + 1) as 1 | 2 | 3;
      const effectiveRank = Math.min(3, Math.max(1, rank)) as 1 | 2 | 3;
      const hero = HEROES_BY_ID[player.heroId];
      const discount = hero.power.action === 'cheaperRunes' ? Number(hero.power.params?.amount ?? 0) : 0;
      const cost = Math.max(0, runeRankCost(1) - discount);
      if (!runeCostAffordable(player, 1) && player.shards < cost) return { state, error: 'not_enough_shards' };
      if (player.shards < cost) return { state, error: 'not_enough_shards' };
      player.shards -= cost;
      player.runes[action.slot] = { defId: rune.id, rank: 1 };
      break;
    }
    case 'heroPower': {
      const result = applyHeroPower(state, player, action.targetUid);
      if (result.error) return { state, error: result.error };
      break;
    }
    case 'chooseContract': {
      const offers = player.contractOffers;
      if (!offers || !offers[action.index]) return { state, error: 'no_offer' };
      player.contract = {
        defId: offers[action.index],
        progress: 0,
        expiresTurn: state.turn + 3,
        chosenAtTurn: state.turn,
      };
      player.contractOffers = undefined;
      break;
    }
    case 'ready': {
      player.ready = true;
      break;
    }
    default:
      return { state, error: 'unknown_action' };
  }
  return { state };
}

function runOnPlay(player: PlayerState, creature: CreatureInstance, slot: SlotId, rng: Rng): void {
  const def = CREATURES_BY_ID[creature.defId];
  for (const eff of def.effects) {
    if (eff.trigger !== 'onPlay') continue;
    if (eff.action === 'gainShards') {
      player.shards = Math.min(20, player.shards + Number(eff.params.amount ?? 0));
    }
    if (eff.action === 'consumeAllyForStats') {
      const others = Object.entries(player.board).filter(([s, c]) => s !== slot && c) as [SlotId, CreatureInstance][];
      if (others.length) {
        const [vslot, victim] = others[rng.int(0, others.length - 1)];
        creature.attack += victim.attack;
        creature.health += victim.health;
        creature.maxHealth += victim.health;
        delete player.board[vslot];
      }
    }
  }
}

function runOnSell(player: PlayerState, creature: CreatureInstance): void {
  const def = CREATURES_BY_ID[creature.defId];
  for (const eff of def.effects) {
    if (eff.trigger !== 'onSell') continue;
    if (eff.action === 'buffStats' && eff.target === 'alliesOfTribe') {
      for (const c of Object.values(player.board)) {
        if (c && CREATURES_BY_ID[c.defId].tribe === eff.params.tribe) {
          c.attack += Number(eff.params.attack ?? 0);
        }
      }
    }
  }
  if (player.contract?.defId === 'trader') player.contract.progress += 1;
}

function runOnBuy(_player: PlayerState, _creature: CreatureInstance): void {
  // No creatures currently define onBuy effects; reserved for future content.
}

function applyHeroPower(state: MatchState, player: PlayerState, targetUid?: string): { error?: string } {
  if (player.heroPowerUsedThisTurn) return { error: 'already_used' };
  const hero = HEROES_BY_ID[player.heroId];
  const cost = hero.power.cost ?? 0;
  if (hero.power.kind === 'active') {
    if (player.gold < cost) return { error: 'not_enough_gold' };
    player.gold -= cost;
  }
  switch (hero.power.action) {
    case 'summonSlimeToHand':
      if (player.hand.length < 10) player.hand.push(instantiateCreature('swamp_slime_token'));
      break;
    case 'grantRangedTemp': {
      const found = targetUid && findCreature(player, targetUid);
      if (found && found.location !== 'hand') {
        (found.creature.keywords as Record<string, boolean>).ranged = true;
      }
      break;
    }
    case 'addAwakenCounter': {
      const found = targetUid && findCreature(player, targetUid);
      if (found) {
        found.creature.survivedCombats += 1;
        const def = CREATURES_BY_ID[found.creature.defId];
        if (def.awaken && found.creature.survivedCombats >= def.awaken.after) {
          found.creature.awakened = true;
        }
      }
      break;
    }
    case 'gainShards':
      player.shards = Math.min(20, player.shards + Number(hero.power.params?.amount ?? 0));
      break;
    default:
      break;
  }
  player.heroPowerUsedThisTurn = true;
  return {};
}

function alivePlayers(state: MatchState): PlayerState[] {
  return state.players.filter((p) => !p.eliminatedAtTurn);
}

export function makePairings(state: MatchState, rng: Rng): [string, string][] {
  const alive = rng.shuffle(alivePlayers(state).map((p) => p.id));
  const pairs: [string, string][] = [];
  const used = new Set<string>();
  for (const id of alive) {
    if (used.has(id)) continue;
    const player = state.players.find((p) => p.id === id)!;
    let bestMatch: string | null = null;
    for (const otherId of alive) {
      if (otherId === id || used.has(otherId)) continue;
      if (player.lastOpponentId === otherId) continue;
      bestMatch = otherId;
      break;
    }
    if (!bestMatch) {
      bestMatch = alive.find((otherId) => otherId !== id && !used.has(otherId)) ?? null;
    }
    if (bestMatch) {
      used.add(id);
      used.add(bestMatch);
      pairs.push([id, bestMatch]);
    }
  }
  return pairs;
}

export interface TurnCombatSummary {
  playerId: string;
  opponentId: string | null;
  winner: 'A' | 'B' | 'draw';
  damage: number;
  events: unknown[];
}

export function resolveCombatPhase(state: MatchState): TurnCombatSummary[] {
  const rng = mulberry32(hashSeed(state.seed, state.turn, 'pairings'));
  const pairs = makePairings(state, rng);
  const paired = new Set(pairs.flat());
  const alive = alivePlayers(state);
  const summaries: TurnCombatSummary[] = [];

  for (const player of alive) {
    if (paired.has(player.id)) continue;
    // Odd one out fights the ghost of the last eliminated player.
    if (!state.ghostBoard) continue;
    const seed = hashSeed(state.seed, state.turn, player.id, 'ghost');
    const result = simulateCombat(
      player.board,
      state.ghostBoard.board,
      player.runes,
      state.ghostBoard.runes,
      { phase: state.phase, turn: state.turn, tierA: player.tavernTier, tierB: 1 },
      seed,
    );
    writeBackSurvivors(player, result.survivorsA);
    if (result.winner === 'A') player.shards = Math.min(20, player.shards + 1);
    summaries.push({ playerId: player.id, opponentId: null, winner: result.winner, damage: 0, events: result.events });
  }

  for (const [idA, idB] of pairs) {
    const a = state.players.find((p) => p.id === idA)!;
    const b = state.players.find((p) => p.id === idB)!;
    const seed = hashSeed(state.seed, state.turn, idA, idB);
    const result = simulateCombat(
      a.board,
      b.board,
      a.runes,
      b.runes,
      { phase: state.phase, turn: state.turn, tierA: a.tavernTier, tierB: b.tavernTier },
      seed,
    );
    writeBackSurvivors(a, result.survivorsA);
    writeBackSurvivors(b, result.survivorsB);
    a.lastOpponentId = idB;
    b.lastOpponentId = idA;

    if (result.winner === 'draw') {
      a.shards = Math.min(20, a.shards + 1);
      b.shards = Math.min(20, b.shards + 1);
    } else {
      const winner = result.winner === 'A' ? a : b;
      const loser = result.winner === 'A' ? b : a;
      winner.shards = Math.min(20, winner.shards + 1);
      applyDamageToLoser(loser, result.damage);
      updateContractOnCombat(winner, true, state.phase);
      updateContractOnCombat(loser, false, state.phase);
      if (result.winner === 'A' && Object.values(a.board).filter(Boolean).length === Object.keys(a.board).length) {
        // no-op placeholder for potential "clean win" nuance
      }
      checkCleanWin(winner, result.winner === 'A' ? result.survivorsA.length : result.survivorsB.length, result.winner === 'A' ? a : b);
    }

    summaries.push({ playerId: idA, opponentId: idB, winner: result.winner, damage: result.damage, events: result.events });
  }

  for (const player of alive) {
    if (player.health <= 0 && !player.eliminatedAtTurn) {
      player.eliminatedAtTurn = state.turn;
      state.ghostBoard = { board: player.board, runes: player.runes };
      const remaining = alivePlayers(state).length;
      player.place = remaining + 1;
    }
  }

  const stillAlive = alivePlayers(state);
  if (stillAlive.length <= 1) {
    state.finished = true;
    if (stillAlive.length === 1) stillAlive[0].place = 1;
  } else {
    startTurn(state);
  }

  return summaries;
}

function writeBackSurvivors(player: PlayerState, survivors: CreatureInstance[]): void {
  const survivorIds = new Set(survivors.map((s) => s.uid));
  for (const [slot, c] of Object.entries(player.board)) {
    if (!c) continue;
    if (survivorIds.has(c.uid)) {
      c.survivedCombats += 1;
      const def = CREATURES_BY_ID[c.defId];
      if (def.awaken && !c.awakened && c.survivedCombats >= def.awaken.after) {
        c.awakened = true;
      }
    } else {
      delete player.board[slot as SlotId];
    }
  }
}

function applyDamageToLoser(loser: PlayerState, damage: number): void {
  let remaining = damage;
  if (loser.armor > 0) {
    const absorbed = Math.min(loser.armor, remaining);
    loser.armor -= absorbed;
    remaining -= absorbed;
  }
  loser.health = Math.max(0, loser.health - remaining);
}

function updateContractOnCombat(player: PlayerState, won: boolean, phase: Phase): void {
  if (!player.contract) return;
  const c = player.contract;
  if (c.defId === 'clean_win' && won) {
    // approximated: any win without checking own losses counts, refined in checkCleanWin
  }
  if (c.defId === 'night_hunt') {
    if (won && phase === 'night') c.progress += 1;
    else if (!won) c.progress = 0;
  }
  if (player.contract && player.contract.expiresTurn < 0) {
    // unreachable, keeps type narrowed
  }
}

function checkCleanWin(winner: PlayerState, survivorCount: number, boardOwner: PlayerState): void {
  if (winner.contract?.defId === 'clean_win') {
    const boardSize = Object.values(boardOwner.board).filter(Boolean).length;
    if (survivorCount >= boardSize && boardSize > 0) {
      winner.contract.progress = 1;
    }
  }
  if (winner.contract?.defId === 'collector') {
    const tribeCounts: Record<string, number> = {};
    for (const c of Object.values(winner.board)) {
      if (!c) continue;
      const tribe = CREATURES_BY_ID[c.defId].tribe;
      tribeCounts[tribe] = (tribeCounts[tribe] ?? 0) + 1;
    }
    if (Object.values(tribeCounts).some((n) => n >= 4)) winner.contract.progress = 1;
  }
}

export function contractCompleted(player: PlayerState): boolean {
  return !!player.contract && player.contract.progress >= 1;
}
