// Registers the jest-dom matchers and their types on Vitest's expect.
import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Unmounts every rendered tree after each test.
afterEach(() => {
  cleanup();
});

declare module 'vitest' {
  export interface ProvidedContext {
    /** Erewhon.FixtureHost base URL, with no trailing slash. */
    fixtureBaseUrl: string;
  }
}
