import { useCallback, useRef, useState } from 'react';
import {
  applyAction,
  createMatch,
  resolveCombatPhase,
  runAllBotTurns,
  type BotDifficulty,
  type MatchState,
  type PlayerAction,
  type PlayerConfig,
  type TurnCombatSummary,
} from '@arena-shards/core';

export const HUMAN_ID = 'human';

const BOT_NAMES = ['Гром', 'Секира', 'Вьюга', 'Клык', 'Пепел', 'Тень', 'Игла'];

const AI_HEROES = [
  'grokh_bog_father',
  'saila_spark',
  'morten_ashen',
  'nokta_night_weaver',
  'eldr_rootspeak',
  'tarvis_cartographer',
  'velma_two_rows',
  'cassius_stargazer',
  'bastian_smith',
];

export interface StartConfig {
  heroId: string;
  opponents: number;
  difficulty: BotDifficulty | 'mixed';
  difficultyLadder: BotDifficulty[];
}

function pickOpponentDifficulty(cfg: StartConfig, index: number): BotDifficulty {
  if (cfg.difficulty !== 'mixed') return cfg.difficulty;
  return cfg.difficultyLadder[index % cfg.difficultyLadder.length];
}

export function useMatch() {
  const stateRef = useRef<MatchState | null>(null);
  const [, setVersion] = useState(0);
  const [lastSummary, setLastSummary] = useState<TurnCombatSummary | null>(null);

  const bump = () => setVersion((v) => v + 1);

  const start = useCallback((cfg: StartConfig) => {
    const seed = (Date.now() ^ Math.floor(Math.random() * 1e9)) >>> 0;
    const players: PlayerConfig[] = [
      { id: HUMAN_ID, name: 'Вы', isBot: false, heroId: cfg.heroId },
      ...Array.from({ length: cfg.opponents }, (_, i) => ({
        id: `bot_${i}`,
        name: BOT_NAMES[i % BOT_NAMES.length],
        isBot: true,
        botDifficulty: pickOpponentDifficulty(cfg, i),
        heroId: AI_HEROES[i % AI_HEROES.length],
      })),
    ];
    stateRef.current = createMatch(seed, players);
    setLastSummary(null);
    bump();
  }, []);

  const dispatch = useCallback((action: PlayerAction) => {
    const state = stateRef.current;
    if (!state) return { error: 'no_match' };
    const result = applyAction(state, HUMAN_ID, action);
    bump();
    return result;
  }, []);

  const endRecruitPhase = useCallback(() => {
    const state = stateRef.current;
    if (!state || state.finished) return;
    const human = state.players.find((p) => p.id === HUMAN_ID);
    if (human && !human.eliminatedAtTurn) {
      applyAction(state, HUMAN_ID, { type: 'ready' });
    }
    runAllBotTurns(state);
    const summaries = resolveCombatPhase(state);
    const mine = summaries.find((s) => s.playerId === HUMAN_ID) ?? null;
    setLastSummary(mine);
    bump();
  }, []);

  return {
    state: stateRef.current,
    lastSummary,
    start,
    dispatch,
    endRecruitPhase,
  };
}
