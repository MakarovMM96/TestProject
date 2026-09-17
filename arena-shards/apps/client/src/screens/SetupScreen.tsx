import { useState } from 'react';
import { BOT_DIFFICULTIES, HEROES, type BotDifficulty } from '@arena-shards/core';
import type { StartConfig } from '../game/useMatch';

const LOBBY_SIZES = [
  { opponents: 1, label: '1v1' },
  { opponents: 3, label: '4 игрока' },
  { opponents: 7, label: '8 игроков' },
];

const LADDER: BotDifficulty[] = ['peasant', 'militia', 'raider', 'warlord', 'overlord'];

export function SetupScreen({ onStart }: { onStart: (cfg: StartConfig) => void }) {
  const [heroId, setHeroId] = useState(HEROES[0].id);
  const [opponents, setOpponents] = useState(7);
  const [difficulty, setDifficulty] = useState<BotDifficulty | 'mixed'>('raider');

  return (
    <div className="screen setup-screen">
      <header className="app-header">
        <h1>Арена Осколков</h1>
        <p className="subtitle">Автобаттлер против ботов — по мотивам Stronghold Crusader: чем выше сложность, тем умнее и быстрее считает противник.</p>
      </header>

      <section className="card">
        <h2>Размер лобби</h2>
        <div className="chip-row">
          {LOBBY_SIZES.map((opt) => (
            <button
              key={opt.opponents}
              className={`chip ${opponents === opt.opponents ? 'chip-active' : ''}`}
              onClick={() => setOpponents(opt.opponents)}
            >
              {opt.label}
            </button>
          ))}
        </div>
      </section>

      <section className="card">
        <h2>Сложность ботов</h2>
        <div className="difficulty-list">
          {BOT_DIFFICULTIES.map((d) => (
            <button
              key={d.id}
              className={`difficulty-card ${difficulty === d.id ? 'difficulty-active' : ''}`}
              onClick={() => setDifficulty(d.id)}
            >
              <span className="difficulty-name">{d.name}</span>
              <span className="difficulty-tagline">{d.tagline}</span>
              <span className="difficulty-desc">{d.description}</span>
            </button>
          ))}
          <button
            className={`difficulty-card ${difficulty === 'mixed' ? 'difficulty-active' : ''}`}
            onClick={() => setDifficulty('mixed')}
          >
            <span className="difficulty-name">Смешанная</span>
            <span className="difficulty-tagline">От Крестьянина до Владыки</span>
            <span className="difficulty-desc">Каждый бот в лобби — следующая ступень сложности, как отряд лордов в осадной миссии.</span>
          </button>
        </div>
      </section>

      <section className="card">
        <h2>Ваш герой</h2>
        <div className="hero-list">
          {HEROES.map((h) => (
            <button
              key={h.id}
              className={`hero-card ${heroId === h.id ? 'hero-active' : ''}`}
              onClick={() => setHeroId(h.id)}
            >
              <span className="hero-name">{h.name}</span>
              <span className="hero-armor">Броня {h.armor}</span>
              <span className="hero-desc">{h.description}</span>
            </button>
          ))}
        </div>
      </section>

      <button
        className="primary-button start-button"
        onClick={() => onStart({ heroId, opponents, difficulty, difficultyLadder: LADDER })}
      >
        В бой
      </button>
    </div>
  );
}
