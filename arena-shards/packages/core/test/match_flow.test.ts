import { describe, expect, it } from 'vitest';
import { createMatch } from '../src';
import { resolveCombatPhase, type PlayerConfig } from '../src/match';
import { runAllBotTurns } from '../src/bots';

const HERO_IDS = [
  'irvena_runecarver',
  'grokh_bog_father',
  'saila_spark',
  'morten_ashen',
  'nokta_night_weaver',
  'eldr_rootspeak',
  'tarvis_cartographer',
  'velma_two_rows',
];

const DIFFICULTIES = ['peasant', 'militia', 'raider', 'warlord', 'overlord'] as const;

function lobbyOfBots(seed: number): PlayerConfig[] {
  return Array.from({ length: 8 }, (_, i) => ({
    id: `p${i}`,
    name: `Bot ${i}`,
    isBot: true,
    botDifficulty: DIFFICULTIES[(seed + i) % DIFFICULTIES.length],
    heroId: HERO_IDS[i],
  }));
}

describe('full match with 8 bots', () => {
  it('always converges to a single winner within a bounded number of turns', () => {
    const state = createMatch(99, lobbyOfBots(1));
    let guard = 0;
    while (!state.finished && guard < 60) {
      runAllBotTurns(state);
      resolveCombatPhase(state);
      guard += 1;
    }
    expect(state.finished).toBe(true);
    const winners = state.players.filter((p) => p.place === 1);
    expect(winners).toHaveLength(1);
    const places = state.players.map((p) => p.place).sort((a, b) => (a ?? 0) - (b ?? 0));
    expect(places).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
  });

  it('is deterministic: identical seeds produce identical final placements', () => {
    function run(seed: number) {
      const state = createMatch(seed, lobbyOfBots(2));
      let guard = 0;
      while (!state.finished && guard < 60) {
        runAllBotTurns(state);
        resolveCombatPhase(state);
        guard += 1;
      }
      return state.players.map((p) => ({ id: p.id, place: p.place, health: p.health }));
    }
    const first = run(555);
    const second = run(555);
    expect(first).toEqual(second);
  });

  it('cycles through day, dusk and night every three turns', () => {
    const state = createMatch(11, lobbyOfBots(3));
    const phases: string[] = [state.phase];
    for (let i = 0; i < 11; i++) {
      runAllBotTurns(state);
      resolveCombatPhase(state);
      if (!state.finished) phases.push(state.phase);
    }
    expect(phases.slice(0, 9)).toEqual([
      'day', 'day', 'day',
      'dusk', 'dusk', 'dusk',
      'night', 'night', 'night',
    ]);
  });
});
