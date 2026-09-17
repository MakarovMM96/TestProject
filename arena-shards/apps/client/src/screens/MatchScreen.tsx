import { useEffect, useMemo, useState } from 'react';
import {
  BACK_SLOTS,
  CONTRACTS_BY_ID,
  CREATURES_BY_ID,
  FRONT_SLOTS,
  HEROES_BY_ID,
  RUNES_BY_ID,
  botProfile,
  type CreatureInstance,
  type PlayerState,
  type SlotId,
} from '@arena-shards/core';
import { HUMAN_ID, type useMatch } from '../game/useMatch';

const PHASE_LABEL: Record<string, string> = { day: 'День', dusk: 'Сумерки', night: 'Ночь' };
const PHASE_ICON: Record<string, string> = { day: '☀️', dusk: '🌆', night: '🌙' };

function CreatureCard({
  creature,
  onClick,
  selected,
  small,
}: {
  creature: CreatureInstance;
  onClick?: () => void;
  selected?: boolean;
  small?: boolean;
}) {
  const def = CREATURES_BY_ID[creature.defId];
  const keywords = Object.keys(creature.keywords);
  return (
    <button
      className={`creature-card tribe-${def.tribe} ${selected ? 'creature-selected' : ''} ${small ? 'creature-small' : ''} ${creature.ascended ? 'creature-ascended' : ''}`}
      onClick={onClick}
    >
      <span className="creature-name">{def.name}</span>
      <span className="creature-stats">
        {creature.attack}/{creature.health}
      </span>
      {keywords.length > 0 && <span className="creature-keywords">{keywords.join(' · ')}</span>}
      {creature.awakened && <span className="creature-awakened">✦ пробуждён</span>}
    </button>
  );
}

function EmptySlot({ onClick, label }: { onClick?: () => void; label: string }) {
  return (
    <button className="creature-card slot-empty" onClick={onClick}>
      <span className="creature-name">{label}</span>
    </button>
  );
}

export function MatchScreen({ match }: { match: ReturnType<typeof useMatch> }) {
  const state = match.state!;
  const human = state.players.find((p) => p.id === HUMAN_ID) as PlayerState;
  const [selectedHand, setSelectedHand] = useState<string | null>(null);
  const [selectedBoard, setSelectedBoard] = useState<string | null>(null);
  const [selectedRune, setSelectedRune] = useState<number | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(state.recruitTimerSec);

  useEffect(() => {
    setSecondsLeft(state.recruitTimerSec);
    setSelectedHand(null);
    setSelectedBoard(null);
    setSelectedRune(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.turn]);

  useEffect(() => {
    if (human.eliminatedAtTurn) return;
    const id = setInterval(() => {
      setSecondsLeft((s) => {
        if (s <= 1) {
          clearInterval(id);
          match.endRecruitPhase();
          return 0;
        }
        return s - 1;
      });
    }, 1000);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.turn]);

  const opponents = state.players.filter((p) => p.id !== HUMAN_ID);
  const heroDef = HEROES_BY_ID[human.heroId];

  const clearSelection = () => {
    setSelectedHand(null);
    setSelectedBoard(null);
    setSelectedRune(null);
  };

  const onHandClick = (uid: string) => {
    if (selectedRune !== null) return;
    setSelectedBoard(null);
    setSelectedHand((cur) => (cur === uid ? null : uid));
  };

  const onBoardClick = (slot: SlotId) => {
    const occupant = human.board[slot];
    if (selectedRune !== null) {
      if (occupant) {
        match.dispatch({ type: 'buyRune', offerIndex: selectedRune, slot });
      }
      clearSelection();
      return;
    }
    if (selectedHand) {
      match.dispatch({ type: 'play', uid: selectedHand, slot });
      clearSelection();
      return;
    }
    if (selectedBoard) {
      if (selectedBoard === occupant?.uid) {
        clearSelection();
        return;
      }
      match.dispatch({ type: 'move', uid: selectedBoard, slot });
      clearSelection();
      return;
    }
    if (occupant) setSelectedBoard(occupant.uid);
  };

  const sellSelected = () => {
    const uid = selectedHand ?? selectedBoard;
    if (uid) match.dispatch({ type: 'sell', uid });
    clearSelection();
  };

  const canForge = state.turn >= 3;
  const contractOffers = human.contractOffers;

  const lastSummary = match.lastSummary;
  const opponentName = lastSummary?.opponentId
    ? state.players.find((p) => p.id === lastSummary.opponentId)?.name
    : lastSummary
      ? 'Призрак'
      : null;

  return (
    <div className="screen match-screen">
      <header className="match-topbar">
        <div className="match-phase">
          <span>{PHASE_ICON[state.phase]}</span>
          <span>{PHASE_LABEL[state.phase]}</span>
        </div>
        <div className="match-turn">Ход {state.turn}</div>
        <div className={`match-timer ${secondsLeft <= 10 ? 'match-timer-urgent' : ''}`}>⏱ {secondsLeft}с</div>
      </header>

      {lastSummary && (
        <div className="last-result-banner">
          {lastSummary.winner === 'draw'
            ? `Ничья с ${opponentName ?? 'соперником'}: оба +1 Осколок`
            : lastSummary.playerId === HUMAN_ID
              ? `Вы победили ${opponentName ?? 'соперника'}!`
              : `Вы проиграли ${opponentName ?? 'сопернику'}: -${lastSummary.damage} здоровья`}
        </div>
      )}

      <section className="stat-bar">
        <span className="stat">❤️ {human.health}</span>
        <span className="stat">🛡 {human.armor}</span>
        <span className="stat">🪙 {human.gold}</span>
        <span className="stat">💠 {human.shards}</span>
        <span className="stat">🏰 Ур.{human.tavernTier}</span>
      </section>

      <section className="opponents-strip">
        {opponents.map((p) => (
          <div key={p.id} className={`opponent-chip ${p.eliminatedAtTurn ? 'opponent-dead' : ''}`}>
            <span className="opponent-name">{p.name}</span>
            <span className="opponent-hp">{p.eliminatedAtTurn ? `#${p.place}` : `❤️${p.health}`}</span>
            {p.botDifficulty && <span className="opponent-diff">{botProfile(p.botDifficulty).name}</span>}
          </div>
        ))}
      </section>

      {contractOffers && contractOffers.length > 0 && (
        <section className="card contract-card">
          <h2>Выберите контракт</h2>
          <div className="contract-offers">
            {contractOffers.map((id, idx) => {
              const c = CONTRACTS_BY_ID[id];
              return (
                <button key={id} className="contract-offer" onClick={() => match.dispatch({ type: 'chooseContract', index: idx })}>
                  <span className="contract-name">{c.name}</span>
                </button>
              );
            })}
          </div>
        </section>
      )}
      {human.contract && !contractOffers && (
        <section className="card contract-active">
          Контракт: {CONTRACTS_BY_ID[human.contract.defId].name} (до хода {human.contract.expiresTurn})
        </section>
      )}

      <section className="card">
        <h2>Таверна</h2>
        <div className="shop-row">
          {human.shop.map((item, idx) =>
            item ? (
              <CreatureCard key={item.uid} creature={item} onClick={() => match.dispatch({ type: 'buy', shopIndex: idx })} />
            ) : (
              <EmptySlot key={`empty_${idx}`} label="—" />
            ),
          )}
        </div>
        <div className="shop-actions">
          <button className="secondary-button" onClick={() => match.dispatch({ type: 'reroll' })}>
            🔄 Обновить (1🪙)
          </button>
          <button
            className={`secondary-button ${human.shopFrozen ? 'secondary-button-active' : ''}`}
            onClick={() => match.dispatch({ type: 'freeze' })}
          >
            ❄️ Заморозить
          </button>
          <button className="secondary-button" onClick={() => match.dispatch({ type: 'upgradeTavern' })}>
            ⬆️ Улучшить ({human.upgradeCost}🪙)
          </button>
        </div>
      </section>

      <section className="card">
        <h2>Стол</h2>
        <p className="row-label">Задний ряд</p>
        <div className="board-row">
          {BACK_SLOTS.map((slot) => (
            <div className="board-cell" key={slot}>
              {human.board[slot] ? (
                <CreatureCard
                  creature={human.board[slot]!}
                  selected={selectedBoard === human.board[slot]!.uid}
                  onClick={() => onBoardClick(slot)}
                />
              ) : (
                <EmptySlot label={runeLabel(human, slot)} onClick={() => onBoardClick(slot)} />
              )}
              {human.runes[slot] && <span className="rune-badge">{runeLabel(human, slot)}</span>}
            </div>
          ))}
        </div>
        <p className="row-label">Передний ряд</p>
        <div className="board-row">
          {FRONT_SLOTS.map((slot) => (
            <div className="board-cell" key={slot}>
              {human.board[slot] ? (
                <CreatureCard
                  creature={human.board[slot]!}
                  selected={selectedBoard === human.board[slot]!.uid}
                  onClick={() => onBoardClick(slot)}
                />
              ) : (
                <EmptySlot label={runeLabel(human, slot)} onClick={() => onBoardClick(slot)} />
              )}
              {human.runes[slot] && <span className="rune-badge">{runeLabel(human, slot)}</span>}
            </div>
          ))}
        </div>
        <p className="hint">Нажмите существо, затем клетку — переставить. Выберите руну ниже, затем клетку — установить.</p>
      </section>

      <section className="card">
        <h2>Рука</h2>
        <div className="hand-row">
          {human.hand.length === 0 && <p className="hint">Пусто</p>}
          {human.hand.map((c) => (
            <CreatureCard key={c.uid} creature={c} selected={selectedHand === c.uid} onClick={() => onHandClick(c.uid)} />
          ))}
        </div>
        {(selectedHand || selectedBoard) && (
          <button className="secondary-button sell-button" onClick={sellSelected}>
            💰 Продать выбранное (+1🪙 +1💠)
          </button>
        )}
      </section>

      {canForge && (
        <section className="card">
          <h2>Кузница</h2>
          <div className="rune-row">
            {human.runeShop.map((rune, idx) =>
              rune ? (
                <button
                  key={`${rune.id}_${idx}`}
                  className={`rune-card ${selectedRune === idx ? 'rune-selected' : ''}`}
                  onClick={() => setSelectedRune((cur) => (cur === idx ? null : idx))}
                >
                  <span className="rune-name">Руна {rune.name}</span>
                  <span className="rune-desc">{rune.description}</span>
                </button>
              ) : null,
            )}
          </div>
          <p className="hint">Выберите руну, затем существо на столе, чтобы установить или улучшить.</p>
        </section>
      )}

      <section className="card hero-power-card">
        <h2>{heroDef.name}</h2>
        <p className="hint">{heroDef.description}</p>
        {heroDef.power.kind === 'active' && (
          <button
            className="secondary-button"
            disabled={human.heroPowerUsedThisTurn || human.gold < (heroDef.power.cost ?? 0)}
            onClick={() => match.dispatch({ type: 'heroPower', targetUid: selectedBoard ?? undefined })}
          >
            Сила героя ({heroDef.power.cost ?? 0}🪙)
          </button>
        )}
      </section>

      <button className="primary-button ready-button" onClick={() => match.endRecruitPhase()}>
        Готов ✅
      </button>
    </div>
  );
}

function runeLabel(player: PlayerState, slot: SlotId): string {
  const rune = player.runes[slot];
  if (!rune) return '+';
  return `${RUNES_BY_ID[rune.defId].name} ${rune.rank}`;
}
