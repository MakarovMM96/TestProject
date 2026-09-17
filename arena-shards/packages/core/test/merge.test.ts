import { describe, expect, it } from 'vitest';
import { createMatch, instantiateCreature } from '../src';
import { applyAction, type PlayerConfig } from '../src/match';

function onePlayer(): PlayerConfig[] {
  return [
    { id: 'p0', name: 'Hero', isBot: false, heroId: 'irvena_runecarver' },
    { id: 'p1', name: 'Bot', isBot: true, botDifficulty: 'peasant', heroId: 'grokh_bog_father' },
  ];
}

describe('merging three copies', () => {
  it('creates an ascended creature with doubled stats and grants shards', () => {
    const state = createMatch(3, onePlayer());
    const player = state.players[0];
    player.hand = [
      instantiateCreature('crystal_shard_beetle'),
      instantiateCreature('crystal_shard_beetle'),
    ];
    player.board = {};
    const shardsBefore = player.shards;

    // Playing the third copy from hand should trigger the merge inside applyAction.
    const third = instantiateCreature('crystal_shard_beetle');
    player.hand.push(third);
    const result = applyAction(state, player.id, { type: 'play', uid: third.uid, slot: 'F0' });
    expect(result.error).toBeUndefined();

    const boardCreatures = Object.values(player.board).filter(Boolean);
    expect(boardCreatures).toHaveLength(1);
    const ascended = boardCreatures[0]!;
    expect(ascended.ascended).toBe(true);
    expect(ascended.attack).toBe(2); // base 1 * 2
    expect(ascended.health).toBe(6); // base 3 * 2
    expect(player.shards).toBe(shardsBefore + 2);
  });
});
