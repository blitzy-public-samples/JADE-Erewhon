import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The message catalogue shared with Erewhon.Application, imported by the app as '@messages'.
const messagesJson = fileURLToPath(new URL('../src/Erewhon.Application/Messages/messages.en-NZ.json', import.meta.url));

// The Erewhon.Api applicationUrl of Properties/launchSettings.json.
const apiOrigin = 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@messages': messagesJson,
    },
  },
  // Dev server: reads files under ErewhonModern/ and forwards app, REST, SOAP and SignalR requests to the API with
  // their paths unchanged.
  server: {
    fs: {
      allow: ['..'],
    },
    proxy: {
      '/api': { target: apiOrigin },
      '/soap': { target: apiOrigin },
      '/hubs': { target: apiOrigin, ws: true },
    },
  },
  // Production build: written to the folder Erewhon.Api serves as its web root, emptied first.
  build: {
    outDir: '../src/Erewhon.Api/wwwroot',
    emptyOutDir: true,
  },
  // Component tests: src/**/*.test.ts(x) under jsdom, one file and one test at a time, against the fixture bridge
  // that the global setup starts.
  test: {
    environment: 'jsdom',
    setupFiles: ['./vitest.setup.ts'],
    globalSetup: ['./vitest.global-setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    fileParallelism: false,
    sequence: {
      concurrent: false,
    },
    globals: false,
    testTimeout: 30_000,
    hookTimeout: 60_000,
  },
});
