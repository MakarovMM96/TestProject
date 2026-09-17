import { botProfile, type MatchState } from '@arena-shards/core';
import { HUMAN_ID } from '../game/useMatch';

export function ResultsScreen({ state, onRestart }: { state: MatchState; onRestart: () => void }) {
  const sorted = [...state.players].sort((a, b) => (a.place ?? 99) - (b.place ?? 99));
  const humanPlace = sorted.find((p) => p.id === HUMAN_ID)?.place;

  return (
    <div className="screen results-screen">
      <header className="app-header">
        <h1>Матч завершён</h1>
        <p className="subtitle">
          {humanPlace === 1 ? 'Вы захватили звезду! Победа.' : `Вы заняли ${humanPlace}-е место.`}
        </p>
      </header>

      <section className="card">
        <h2>Итоговая таблица</h2>
        <ol className="results-list">
          {sorted.map((p) => (
            <li key={p.id} className={`results-row ${p.id === HUMAN_ID ? 'results-row-human' : ''}`}>
              <span className="results-place">#{p.place}</span>
              <span className="results-name">{p.name}</span>
              {p.isBot && p.botDifficulty && (
                <span className="results-tag">{botProfile(p.botDifficulty).name}</span>
              )}
            </li>
          ))}
        </ol>
      </section>

      <button className="primary-button start-button" onClick={onRestart}>
        Играть снова
      </button>
    </div>
  );
}
