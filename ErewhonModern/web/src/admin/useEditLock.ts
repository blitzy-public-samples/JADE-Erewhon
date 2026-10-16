// The soft edit lock of the admin edit dialogs over /api/app/edit-locks/{type}/{id}: the prompt shown when the
// opened row is already locked, the lock taken on the first change, the outstanding-changes prompt on close, and the
// unlock after a save, on close and on unmount.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { ApiError, api } from '../api/client';
import type { EditLockType, OperationResult, SaveRequestResponse, SoftLockStatus } from '../api/types';
import { displayErrorsOptions } from '../components/ErrorList';
import { useMessageBox } from '../components/MessageBox';
import { msg } from '../messages/messages';

const EDIT_LOCKS_PATH = '/api/app/edit-locks';

const SAVE_REQUEST_SEGMENT = '/save-request';

/** Lock request this hook sent and has not yet followed with an unlock. */
interface HeldLock {
  /** Path of the row's edit lock, `/api/app/edit-locks/{type}/{id}`. */
  readonly path: string;
  /** The POST that sets the lock. */
  readonly request: Promise<OperationResult>;
}

/** State and actions of the soft edit lock of one edit dialog, returned by {@link useEditLock}. */
export interface EditLockHandle {
  /** The message box at the head of the hook's queue, or null; the dialog renders it once. */
  element: ReactNode;
  /** True once a change was made since the dialog opened or since the last successful save. */
  isDirty: boolean;
  /** Result of the last save that failed; null before any failure and after a successful save. */
  lastResult: OperationResult | null;
  /**
   * Marks the dialog changed. The first call after open, after a successful save or after {@link release} sets the
   * row's edit lock to the caller's session when the dialog edits an existing row.
   */
  markChanged(): void;
  /**
   * Runs `doSave` and resolves true when its result succeeded, after clearing {@link lastResult}, {@link isDirty} and
   * the row's edit lock as {@link release} does. A failed result is kept in {@link lastResult} and its full error text
   * shown in the `$ErrorDuringSave` box; a thrown error outcome is shown in its own box, and a thrown 401 shows
   * nothing. Each of these resolves false, once any box is closed, and keeps the lock and {@link isDirty}; any other
   * thrown error rejects.
   */
  save(doSave: () => Promise<OperationResult>): Promise<boolean>;
  /**
   * Resolves true when the dialog may close. With {@link isDirty} set, asks `$ChangesSinceOpeningForm` with Yes, No
   * and Cancel: Cancel resolves false, Yes resolves as {@link save} does with `doSave`, and No clears the row's edit
   * lock and resolves true. Without changes, clears the row's edit lock and resolves true.
   */
  close(doSave: () => Promise<OperationResult>): Promise<boolean>;
  /**
   * Clears the row's edit lock when the caller's session holds it, after any pending lock request has answered, and
   * resets the first-change latch. Does nothing for a new row.
   */
  release(): Promise<void>;
}

/** Returns true when `id` is a positive safe integer, the id of an existing row. */
function isRowId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns the request path of the edit lock of row `id` of `type`. */
function lockPath(type: EditLockType, id: number): string {
  return `${EDIT_LOCKS_PATH}/${encodeURIComponent(type)}/${encodeURIComponent(String(id))}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Resolves once `promise` has fulfilled or rejected; its outcome is handled where the promise was created. */
function settled(promise: Promise<unknown>): Promise<void> {
  return promise.then(
    () => undefined,
    () => undefined,
  );
}

/**
 * Sends DELETE for the edit lock at `path` once the lock request of `held`, when there is one, has answered, and passes
 * a failure of the DELETE to `report`.
 */
async function sendUnlock(
  path: string,
  held: HeldLock | null,
  report: (error: unknown) => Promise<void>,
): Promise<void> {
  if (held !== null) {
    await settled(held.request);
  }

  try {
    await api.del<OperationResult>(path);
  } catch (error: unknown) {
    await report(error);
  }
}

/**
 * Soft edit lock of the edit dialog of row `id` of `type`; `id` null, undefined or not a positive integer edits a new
 * row, which takes no lock.
 *
 * On mount and whenever the row changes, reads the row's lock state. When the row is locked, by any session including
 * the caller's own, shows `$ThingCurrentlyEdited` titled `$ThingAlreadyBeingEdited` for the returned thing (or
 * `$Thing`) with the question icon, Yes and No; Yes sends the save request for the row and No does nothing. A read
 * that answers after the row changed or after unmount shows nothing, and a Yes given after then sends nothing.
 *
 * A failed lock-state read, lock, unlock or save request answered with 404 is ignored; an error outcome is shown in
 * its message box, a 401 shows nothing, and any other failure rejects. When the row changes or the component unmounts
 * after this hook requested the lock, the lock is cleared once that request has answered, and the first-change latch
 * is reset.
 */
export function useEditLock(type: EditLockType, id: number | null | undefined): EditLockHandle {
  const { element, show, showError } = useMessageBox();
  const [isDirty, setIsDirty] = useState(false);
  const [lastResult, setLastResult] = useState<OperationResult | null>(null);
  const dirtyRef = useRef(false);
  const firstChangeRef = useRef(false);
  const heldLockRef = useRef<HeldLock | null>(null);
  const path = isRowId(id) ? lockPath(type, id) : null;

  const reportFailure = useCallback(
    async (error: unknown): Promise<void> => {
      if (!isNotFound(error)) {
        await showError(error);
      }
    },
    [showError],
  );

  useEffect(() => {
    if (path === null) {
      return undefined;
    }

    let active = true;

    const promptWhenLocked = async (): Promise<void> => {
      let status: SoftLockStatus | undefined;
      try {
        status = await api.get<SoftLockStatus | undefined>(path);
      } catch (error: unknown) {
        if (active) {
          await reportFailure(error);
        }
        return;
      }

      if (!active || status?.isLocked !== true) {
        return;
      }

      const thing = status.thing || msg('Thing');
      const answer = await show({
        title: msg('ThingAlreadyBeingEdited', thing),
        message: msg('ThingCurrentlyEdited', thing),
        icon: 'question',
        buttons: 'yesNo',
      });

      if (!active || answer !== 'yes') {
        return;
      }

      try {
        await api.post<SaveRequestResponse>(`${path}${SAVE_REQUEST_SEGMENT}`);
      } catch (error: unknown) {
        await reportFailure(error);
      }
    };

    void promptWhenLocked();

    return () => {
      active = false;
    };
  }, [path, reportFailure, show]);

  useEffect(
    () => () => {
      const held = heldLockRef.current;
      heldLockRef.current = null;
      firstChangeRef.current = false;
      if (held !== null) {
        void sendUnlock(held.path, held, reportFailure);
      }
    },
    [path, reportFailure],
  );

  const markChanged = useCallback((): void => {
    dirtyRef.current = true;
    setIsDirty(true);

    if (firstChangeRef.current) {
      return;
    }
    firstChangeRef.current = true;

    if (path === null) {
      return;
    }

    const request = api.post<OperationResult>(path);
    heldLockRef.current = { path, request };
    void request.catch(reportFailure);
  }, [path, reportFailure]);

  const release = useCallback(async (): Promise<void> => {
    const held = heldLockRef.current;
    heldLockRef.current = null;
    firstChangeRef.current = false;

    if (path === null) {
      return;
    }

    await sendUnlock(path, held, reportFailure);
  }, [path, reportFailure]);

  const save = useCallback(
    async (doSave: () => Promise<OperationResult>): Promise<boolean> => {
      let result: OperationResult;
      try {
        result = await doSave();
      } catch (error: unknown) {
        await showError(error);
        return false;
      }

      if (!result.succeeded) {
        setLastResult(result);
        await show(displayErrorsOptions(result));
        return false;
      }

      setLastResult(null);
      dirtyRef.current = false;
      setIsDirty(false);
      await release();
      return true;
    },
    [release, show, showError],
  );

  const close = useCallback(
    async (doSave: () => Promise<OperationResult>): Promise<boolean> => {
      if (dirtyRef.current) {
        const answer = await show({
          title: msg('OutstandingChanges'),
          message: msg('ChangesSinceOpeningForm'),
          icon: 'question',
          buttons: 'yesNoCancel',
        });

        if (answer === 'cancel') {
          return false;
        }
        if (answer === 'yes') {
          return save(doSave);
        }
      }

      await release();
      return true;
    },
    [release, save, show],
  );

  return { element, isDirty, lastResult, markChanged, save, close, release };
}
