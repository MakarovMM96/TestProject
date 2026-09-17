import { useState } from 'react';
import { useMatch, type StartConfig } from './game/useMatch';
import { SetupScreen } from './screens/SetupScreen';
import { MatchScreen } from './screens/MatchScreen';
import { ResultsScreen } from './screens/ResultsScreen';

export default function App() {
  const match = useMatch();
  const [started, setStarted] = useState(false);

  const handleStart = (cfg: StartConfig) => {
    match.start(cfg);
    setStarted(true);
  };

  const handleRestart = () => {
    setStarted(false);
  };

  if (!started || !match.state) {
    return <SetupScreen onStart={handleStart} />;
  }

  if (match.state.finished) {
    return <ResultsScreen state={match.state} onRestart={handleRestart} />;
  }

  return <MatchScreen match={match} />;
}
