import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import type { TestProject } from 'vitest/node';

const webRoot = path.dirname(fileURLToPath(import.meta.url));
const fixtureHostProject = path.resolve(webRoot, '../tests/Erewhon.FixtureHost');

const connectionStringVariable = 'ConnectionStrings__ErewhonTests';
const databaseName = 'erewhon_test_vitest';
const baseUrlPattern = /^https?:\/\/\S+$/;

const baseUrlTimeoutMs = 300_000;
const progressIntervalMs = 30_000;
const stdinCloseExitTimeoutMs = 30_000;
const sigtermExitTimeoutMs = 10_000;

/** Reports whether the bridge process was started and has neither exited nor been ended by a signal. */
function isRunning(child: ChildProcess): boolean {
  return child.pid !== undefined && child.exitCode === null && child.signalCode === null;
}

/** Resolves true once the bridge process has exited, or false when it is still running after timeoutMs. */
function waitForExit(child: ChildProcess, timeoutMs: number): Promise<boolean> {
  if (!isRunning(child)) {
    return Promise.resolve(true);
  }
  return new Promise<boolean>((resolve) => {
    let timer: NodeJS.Timeout | undefined;
    const onExit = (): void => {
      clearTimeout(timer);
      resolve(true);
    };
    timer = setTimeout(() => {
      child.off('exit', onExit);
      resolve(!isRunning(child));
    }, timeoutMs);
    child.once('exit', onExit);
  });
}

/**
 * Stops the bridge process: closes its standard input and waits up to 30 s, then sends SIGTERM and waits up to
 * 10 s, then sends SIGKILL. Returns at once when the process is not running.
 */
async function stopBridge(child: ChildProcess): Promise<void> {
  if (!isRunning(child)) {
    return;
  }
  child.stdin?.end();
  if (await waitForExit(child, stdinCloseExitTimeoutMs)) {
    return;
  }
  child.kill('SIGTERM');
  if (await waitForExit(child, sigtermExitTimeoutMs)) {
    return;
  }
  child.kill('SIGKILL');
}

/** Describes how the bridge process ended, from its exit code or terminating signal. */
function describeExit(code: number | null, signal: NodeJS.Signals | null): string {
  return code !== null ? `exit code ${code}` : `signal ${signal ?? 'unknown'}`;
}

/**
 * Waits for the base URL line on the bridge's standard output and resolves with it, trimmed. Every other line, before
 * and after the URL, is echoed to standard error. A progress line goes to standard error every 30 s while waiting.
 * Rejects when the process cannot be started, exits before writing a URL, or writes none within 300 s.
 */
function waitForBaseUrl(child: ChildProcess): Promise<string> {
  const stdout = child.stdout;
  if (stdout === null) {
    return Promise.reject(new Error('Erewhon.FixtureHost was started without a standard output pipe'));
  }
  const lines = createInterface({ input: stdout });

  return new Promise<string>((resolve, reject) => {
    const startedAt = Date.now();
    let settled = false;

    const progress = setInterval(() => {
      const elapsedSeconds = Math.round((Date.now() - startedAt) / 1000);
      process.stderr.write(`Waiting for Erewhon.FixtureHost to write its base URL (${elapsedSeconds} s elapsed)\n`);
    }, progressIntervalMs);

    const settle = (outcome: { url: string } | { error: Error }): void => {
      if (settled) {
        return;
      }
      settled = true;
      clearInterval(progress);
      clearTimeout(timeout);
      child.off('exit', onExit);
      if ('url' in outcome) {
        resolve(outcome.url);
      } else {
        reject(outcome.error);
      }
    };

    const timeout = setTimeout(() => {
      settle({ error: new Error(`Erewhon.FixtureHost did not write its base URL within ${baseUrlTimeoutMs} ms`) });
    }, baseUrlTimeoutMs);

    const onExit = (code: number | null, signal: NodeJS.Signals | null): void => {
      settle({
        error: new Error(
          `Erewhon.FixtureHost ended with ${describeExit(code, signal)} before writing its base URL`,
        ),
      });
    };
    child.once('exit', onExit);

    child.on('error', (error: Error) => {
      if (settled) {
        process.stderr.write(`Erewhon.FixtureHost process error: ${error.message}\n`);
        return;
      }
      settle({ error: new Error(`Erewhon.FixtureHost could not be started: ${error.message}`, { cause: error }) });
    });

    lines.on('line', (line: string) => {
      const trimmed = line.trim();
      if (!settled && baseUrlPattern.test(trimmed)) {
        settle({ url: trimmed });
        return;
      }
      process.stderr.write(`${line}\n`);
    });
  });
}

/**
 * Starts `dotnet run --project ../tests/Erewhon.FixtureHost -- serve --database erewhon_test_vitest` with the
 * inherited environment, waits for the base URL it writes to standard output, and provides that URL, without a
 * trailing slash, as `fixtureBaseUrl`. The returned teardown stops the bridge process.
 *
 * @throws Error when `ConnectionStrings__ErewhonTests` is missing or empty, or when the bridge fails to report its
 * base URL; the bridge process is stopped before the error is thrown.
 */
export default async function setup(project: TestProject): Promise<() => Promise<void>> {
  if (!process.env[connectionStringVariable]) {
    throw new Error(`${connectionStringVariable} must be set to run the React tests`);
  }

  const child = spawn('dotnet', ['run', '--project', fixtureHostProject, '--', 'serve', '--database', databaseName], {
    cwd: webRoot,
    env: process.env,
    stdio: ['pipe', 'pipe', 'inherit'],
  });

  child.stdin?.on('error', (error: Error) => {
    process.stderr.write(`Erewhon.FixtureHost standard input error: ${error.message}\n`);
  });

  let baseUrl: string;
  try {
    baseUrl = await waitForBaseUrl(child);
  } catch (error) {
    await stopBridge(child);
    throw error;
  }

  project.provide('fixtureBaseUrl', baseUrl.replace(/\/+$/, ''));

  return async (): Promise<void> => {
    await stopBridge(child);
  };
}
