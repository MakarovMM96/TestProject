import { describe, expect, it } from 'vitest';
import { createMatch, tavernRow } from '../src';
import { applyAction, resolveCombatPhase, type PlayerConfig } from '../src/match';

function players(n: number): PlayerConfig[] {
  const heroes = ['irvena_runecarver', 'grokh_bog_father', 'saila_spark', 'morten_ashen'];
  return Array.from({ length: n }, (_, i) => ({
    id: `p${i}`,
    name: `Player ${i}`,
    isBot: i !== 0,
    botDifficulty: 'raider' as const,
    heroId: heroes[i % heroes.length],
  }));
}

describe('economy', () => {
  it('gold follows 3 + 1/turn capped at 10', () => {
    const state = createMatch(1, players(2));
    const expected = [3, 4, 5, 6, 7, 8, 9, 10, 10, 10];
    expect(state.players[0].gold).toBe(expected[0]);
    for (let t = 2; t <= 10; t++) {
      resolveCombatPhase(state);
      expect(state.players[0].gold).toBe(expected[t - 1]);
    }
  });

  it('tavern upgrade costs match the design table', () => {
    expect(tavernRow(2).upgradeCost).toBe(5);
    expect(tavernRow(3).upgradeCost).toBe(7);
    expect(tavernRow(6).upgradeCost).toBe(11);
  });

  it('a shown creature is already reserved from the pool, and selling returns it', () => {
    // The pool is decremented the moment a creature is drawn into a shop slot (it's reserved
    // for that player), so buying it doesn't change the pool further — only selling does.
    const state = createMatch(42, players(2));
    const player = state.players[0];
    const idx = player.shop.findIndex((c) => c !== null);
    expect(idx).toBeGreaterThanOrEqual(0);
    const shopItem = player.shop[idx]!;
    const before = state.pool[shopItem.defId];
    player.gold = 10;

    const buyResult = applyAction(state, player.id, { type: 'buy', shopIndex: idx });
    expect(buyResult.error).toBeUndefined();
    expect(state.pool[shopItem.defId]).toBe(before);

    const uid = player.hand[0].uid;
    applyAction(state, player.id, { type: 'sell', uid });
    expect(state.pool[shopItem.defId]).toBe(before + 1);
  });

  it('upgrading the tavern advances the tier and charges gold', () => {
    const state = createMatch(7, players(2));
    const player = state.players[0];
    expect(player.tavernTier).toBe(1);
    player.gold = 20;
    const cost = player.upgradeCost;
    const result = applyAction(state, player.id, { type: 'upgradeTavern' });
    expect(result.error).toBeUndefined();
    expect(player.tavernTier).toBe(2);
    expect(player.gold).toBe(20 - cost);
  });

  it('rejects upgrading without enough gold', () => {
    const state = createMatch(7, players(2));
    const player = state.players[0];
    player.gold = 0;
    const result = applyAction(state, player.id, { type: 'upgradeTavern' });
    expect(result.error).toBe('not_enough_gold');
    expect(player.tavernTier).toBe(1);
  });
});
