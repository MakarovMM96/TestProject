import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'com.arenashards.app',
  appName: 'Арена Осколков',
  webDir: 'dist',
  backgroundColor: '#0f0b1e',
  server: {
    androidScheme: 'https',
  },
};

export default config;
