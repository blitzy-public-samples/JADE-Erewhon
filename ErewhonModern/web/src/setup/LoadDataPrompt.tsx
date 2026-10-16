// The load-data prompt shown in place of the logon screen while no company exists: an OK/Cancel message box whose OK
// loads the seed data of the server's configured data directory through POST /api/app/setup/load-data.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../api/client';
import type { LoadDataResponse } from '../api/types';
import { MessageBox, errorOutcomeOptions } from '../components/MessageBox';
import type { MessageBoxOptions, MessageBoxResult } from '../components/MessageBox';
import { msg } from '../messages/messages';

/** Properties of {@link LoadDataPrompt}. */
export interface LoadDataPromptProps {
  /** Called once the load request has answered 200, whatever its `loaded` value, or 409. */
  onLoaded: () => void;
  /**
   * Called when the prompt is cancelled, when the load request fails without an error outcome, and when the message
   * box of a failed load's error outcome is closed.
   */
  onCancelled: () => void;
}

/** Step of the prompt: the OK/Cancel box, the running load request, or the box of a failed load. */
type PromptState = { step: 'prompt' } | { step: 'loading' } | { step: 'error'; options: MessageBoxOptions };

/** How the load request ended: answered 200 or 409, failed with an error outcome, or failed otherwise. */
type LoadOutcome = { kind: 'loaded' } | { kind: 'errorOutcome'; options: MessageBoxOptions } | { kind: 'failed' };

const LOAD_DATA_PATH = '/api/app/setup/load-data';

const HTTP_CONFLICT = 409;

/** Returns the outcome of a load request that rejected with `err`. */
function failureOutcome(err: unknown): LoadOutcome {
  if (err instanceof ApiError && err.status === HTTP_CONFLICT) {
    return { kind: 'loaded' };
  }
  if (err instanceof ErrorOutcomeError) {
    return { kind: 'errorOutcome', options: errorOutcomeOptions(err.outcome) };
  }
  return { kind: 'failed' };
}

/**
 * Prompt shown while no company exists: a message box titled `$PleaseLoadDataFiles` with the message
 * `$DataHasNotBeenLoaded` unchanged, the information icon and the buttons OK and Cancel, OK focused.
 *
 * OK sends `POST /api/app/setup/load-data` with no body, one request at a time, and leaves no box on screen while the
 * request runs. A 200 response, whatever its `loaded` value, or a 409 calls `onLoaded`. A 500 error outcome shows its
 * OK-only message box, and closing that box calls `onCancelled`; any other failure calls `onCancelled` and shows
 * nothing. Cancel and Escape call `onCancelled`. Once the prompt has unmounted, a request still running changes no
 * state and calls neither callback.
 */
export function LoadDataPrompt(props: LoadDataPromptProps): ReactElement {
  const { onLoaded, onCancelled } = props;
  const [state, setState] = useState<PromptState>({ step: 'prompt' });
  const mountedRef = useRef(false);
  const busyRef = useRef(false);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const startLoad = useCallback(async (): Promise<void> => {
    if (busyRef.current) {
      return;
    }
    busyRef.current = true;
    setState({ step: 'loading' });

    let outcome: LoadOutcome;
    try {
      await api.post<LoadDataResponse>(LOAD_DATA_PATH);
      outcome = { kind: 'loaded' };
    } catch (err: unknown) {
      outcome = failureOutcome(err);
    } finally {
      busyRef.current = false;
    }

    if (!mountedRef.current) {
      return;
    }

    switch (outcome.kind) {
      case 'loaded':
        onLoaded();
        break;
      case 'errorOutcome':
        setState({ step: 'error', options: outcome.options });
        break;
      case 'failed':
        onCancelled();
        break;
    }
  }, [onLoaded, onCancelled]);

  const handlePromptClose = useCallback(
    (result: MessageBoxResult): void => {
      if (busyRef.current) {
        return;
      }
      if (result === 'ok') {
        void startLoad();
      } else {
        onCancelled();
      }
    },
    [startLoad, onCancelled],
  );

  const handleErrorClose = useCallback((): void => {
    onCancelled();
  }, [onCancelled]);

  return (
    <section aria-busy={state.step === 'loading'}>
      {state.step === 'prompt' && (
        <MessageBox
          title={msg('PleaseLoadDataFiles')}
          message={msg('DataHasNotBeenLoaded')}
          icon="information"
          buttons="okCancel"
          onClose={handlePromptClose}
        />
      )}
      {state.step === 'error' && <MessageBox {...state.options} onClose={handleErrorClose} />}
    </section>
  );
}
