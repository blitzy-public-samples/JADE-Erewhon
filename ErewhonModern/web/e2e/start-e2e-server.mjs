// Prepares the end-to-end database and starts Erewhon.Api at http://127.0.0.1:5090 for the Playwright webServer:
// derives the E2E connection string, resets the database, builds the React app, applies the migrations, seeds the
// database from Erewhon/DataFiles, then runs the API in the foreground until it exits.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const scriptPath = fileURLToPath(import.meta.url);
const scriptDir = path.dirname(scriptPath);
const webDir = path.resolve(scriptDir, '..');
const fixtureHostProject = path.resolve(webDir, '../tests/Erewhon.FixtureHost');
const apiProject = path.resolve(webDir, '../src/Erewhon.Api');
const dataDir = path.resolve(scriptDir, '../../../Erewhon/DataFiles');

const E2E_DATABASE = 'erewhon_test_e2e';
const E2E_URL = 'http://127.0.0.1:5090';

const TESTS_CONNECTION_VARIABLE = 'ConnectionStrings__ErewhonTests';
const API_CONNECTION_VARIABLE = 'ConnectionStrings__Erewhon';
const DATABASE_KEYS = new Set(['database', 'db']);
const STEP_COUNT = 6;
const LOG_PREFIX = '[start-e2e-server]';
const FAILURE_EXIT_CODE = 1;
const SIGNAL_EXIT_BASE = 128;
const STOP_EXIT_CODES = { SIGINT: 130, SIGTERM: 143 };

/** The child process of the step that is running, or null between steps. */
let currentChild = null;
/** Set once SIGINT or SIGTERM has been received. */
let stopping = false;
/** The first stop signal received, or null. */
let stopSignal = null;

/** Writes one progress line to standard output. */
function log(line) {
  process.stdout.write(`${LOG_PREFIX} ${line}\n`);
}

/** Writes one error line to standard error. */
function logError(line) {
  process.stderr.write(`${LOG_PREFIX} ${line}\n`);
}

/**
 * Returns the process exit code for a child's outcome: its exit code when it has one, 128 plus the signal number
 * when it ended by a signal, and 1 when neither is known.
 */
function exitCodeOf({ code, signal }) {
  if (typeof code === 'number') {
    return code;
  }
  const signalNumber = signal ? os.constants.signals[signal] : undefined;
  return typeof signalNumber === 'number' ? SIGNAL_EXIT_BASE + signalNumber : FAILURE_EXIT_CODE;
}

/**
 * Runs command with args in the web folder with env as its environment and the standard streams inherited, and
 * resolves with its exit code and signal once it exits. A command that cannot be started is reported on standard
 * error and resolves as exit code 1.
 */
function run(command, args, env) {
  return new Promise((resolve) => {
    let child;
    let settled = false;

    const finish = (result) => {
      if (settled) {
        return;
      }
      settled = true;
      if (currentChild === child) {
        currentChild = null;
      }
      resolve(result);
    };

    try {
      child = spawn(command, args, { cwd: webDir, env, stdio: 'inherit', shell: false });
    } catch (error) {
      logError(`failed to start ${command}: ${messageOf(error)}`);
      finish({ code: FAILURE_EXIT_CODE, signal: null });
      return;
    }

    currentChild = child;
    child.on('error', (error) => {
      if (child.pid === undefined) {
        logError(`failed to start ${command}: ${error.message}`);
        finish({ code: FAILURE_EXIT_CODE, signal: null });
        return;
      }
      logError(`${command} process error: ${error.message}`);
    });
    child.once('exit', (code, signal) => finish({ code, signal }));
  });
}

/** Returns the message of an error, or the text of any other thrown value. */
function messageOf(error) {
  return error instanceof Error ? error.message : String(error);
}

/** Returns the command and arguments that run `npm run build` without a shell. */
function npmRunBuildCommand() {
  const npmArgs = ['run', 'build'];
  const npmExecPath = process.env.npm_execpath;
  if (npmExecPath && path.basename(npmExecPath) === 'npm-cli.js') {
    return { command: process.execPath, args: [npmExecPath, ...npmArgs] };
  }
  if (process.platform === 'win32') {
    const bundledNpmCli = path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
    if (fs.existsSync(bundledNpmCli)) {
      return { command: process.execPath, args: [bundledNpmCli, ...npmArgs] };
    }
    return { command: 'npm.cmd', args: npmArgs };
  }
  return { command: 'npm', args: npmArgs };
}

/** Splits text on the semicolons that lie outside single- or double-quoted runs. */
function splitOutsideQuotes(text) {
  const segments = [];
  let segmentStart = 0;
  let quote = null;
  for (let index = 0; index < text.length; index += 1) {
    const character = text[index];
    if (quote !== null) {
      if (character === quote) {
        quote = null;
      }
    } else if (character === '"' || character === "'") {
      quote = character;
    } else if (character === ';') {
      segments.push(text.slice(segmentStart, index));
      segmentStart = index + 1;
    }
  }
  segments.push(text.slice(segmentStart));
  return segments;
}

/**
 * Returns source with its database set to erewhon_test_e2e. The segments between semicolons outside quotes are
 * trimmed and empty ones dropped; the first segment whose key is `Database` or `DB`, in any letter case, becomes
 * `Database=erewhon_test_e2e` in place and later ones are removed, or `Database=erewhon_test_e2e` is appended when
 * there is none. Every other segment keeps its text and order.
 *
 * @param {string} source A PostgreSQL connection string in key=value form.
 * @returns {string} The connection string of the E2E database.
 */
export function deriveE2eConnectionString(source) {
  const databaseSegment = `Database=${E2E_DATABASE}`;
  const derived = [];
  let databaseSet = false;
  for (const rawSegment of splitOutsideQuotes(String(source))) {
    const segment = rawSegment.trim();
    if (segment.length === 0) {
      continue;
    }
    const separatorIndex = segment.indexOf('=');
    const key = (separatorIndex === -1 ? segment : segment.slice(0, separatorIndex)).trim().toLowerCase();
    if (DATABASE_KEYS.has(key)) {
      if (!databaseSet) {
        derived.push(databaseSegment);
        databaseSet = true;
      }
      continue;
    }
    derived.push(segment);
  }
  if (!databaseSet) {
    derived.push(databaseSegment);
  }
  return derived.join(';');
}

/** Returns the exit code for a stop by the first stop signal received. */
function stopExitCode() {
  return STOP_EXIT_CODES[stopSignal] ?? FAILURE_EXIT_CODE;
}

/** Forwards SIGINT and SIGTERM to the running child, or exits at once when no child is running. */
function registerStopSignalHandlers() {
  const onStopSignal = (signal) => {
    stopping = true;
    stopSignal ??= signal;
    if (currentChild === null) {
      process.exit(STOP_EXIT_CODES[signal]);
      return;
    }
    try {
      currentChild.kill(signal);
    } catch (error) {
      logError(`failed to forward ${signal}: ${messageOf(error)}`);
    }
  };
  process.on('SIGINT', onStopSignal);
  process.on('SIGTERM', onStopSignal);
}

/** Exits when a stop signal has been received; otherwise writes the progress line of the step. */
function beginStep(number, description) {
  if (stopping) {
    process.exit(stopExitCode());
  }
  log(`step ${number}/${STEP_COUNT}: ${description}`);
}

/** Exits with the step's exit code when it did not end with 0 or a stop signal arrived while it ran. */
function endStep(result) {
  if (stopping || result.code !== 0) {
    process.exit(exitCodeOf(result));
  }
}

/** Runs the six steps in order and exits with the API's exit code. */
async function main() {
  registerStopSignalHandlers();

  // Step 1: derive the E2E connection string from ConnectionStrings__ErewhonTests
  beginStep(1, `derive the connection string of database ${E2E_DATABASE} from ${TESTS_CONNECTION_VARIABLE}`);
  const testsConnectionString = process.env[TESTS_CONNECTION_VARIABLE];
  if (typeof testsConnectionString !== 'string' || testsConnectionString.trim().length === 0) {
    logError(
      `${TESTS_CONNECTION_VARIABLE} is not set; set it to a PostgreSQL connection string for a role that may create databases`,
    );
    process.exit(FAILURE_EXIT_CODE);
  }
  const apiEnv = { ...process.env, [API_CONNECTION_VARIABLE]: deriveE2eConnectionString(testsConnectionString) };

  // Step 2: drop and recreate the E2E database empty
  beginStep(2, `reset database ${E2E_DATABASE}`);
  endStep(
    await run('dotnet', ['run', '--project', fixtureHostProject, '--', 'reset-database', E2E_DATABASE], process.env),
  );

  // Step 3: build the React app into the API's wwwroot
  beginStep(3, 'build the React app (npm run build)');
  const npmBuild = npmRunBuildCommand();
  endStep(await run(npmBuild.command, npmBuild.args, process.env));

  // Step 4: apply EF Core migrations to the E2E database
  beginStep(4, `apply migrations to database ${E2E_DATABASE}`);
  endStep(await run('dotnet', ['run', '--project', apiProject, '--', 'migrate'], apiEnv));

  // Step 5: load the seed data files into the E2E database
  beginStep(5, `seed database ${E2E_DATABASE} from ${dataDir}`);
  if (!fs.existsSync(dataDir)) {
    logError(`data directory not found: ${dataDir}`);
    process.exit(FAILURE_EXIT_CODE);
  }
  endStep(await run('dotnet', ['run', '--project', apiProject, '--', 'seed', dataDir], apiEnv));

  // Step 6: run the API in the foreground until it exits
  beginStep(6, `start Erewhon.Api at ${E2E_URL}`);
  const serverResult = await run('dotnet', ['run', '--project', apiProject, '--no-build', '--urls', E2E_URL], apiEnv);
  process.exit(exitCodeOf(serverResult));
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    await main();
  } catch (error) {
    logError(`unexpected failure: ${messageOf(error)}`);
    process.exit(FAILURE_EXIT_CODE);
  }
}
