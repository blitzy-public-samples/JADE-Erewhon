import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, KeyboardEvent, MouseEvent, ReactElement, SyntheticEvent } from 'react';
import { createPortal } from 'react-dom';
import type { ErrorOutcome } from '../api/types';
import { ErrorOutcomeError, UnauthorizedError } from '../api/client';

/** Button set of a message box: OK only, Yes and No, or Yes, No and Cancel. */
export type MessageBoxButtons = 'ok' | 'yesNo' | 'yesNoCancel';

/** Icon shown beside the title of a message box. */
export type MessageBoxIcon = 'information' | 'question' | 'error' | 'exclamation';

/** Button the user chose to close a message box. */
export type MessageBoxResult = 'ok' | 'yes' | 'no' | 'cancel';

/** Content of a message box. */
export interface MessageBoxOptions {
  /** Title of the box, which is also its accessible name. */
  title: string;
  /** Message text, shown unchanged with its line breaks. */
  message: string;
  /** Icon beside the title; no icon when omitted. */
  icon?: MessageBoxIcon;
  /** Buttons of the box; `'ok'` when omitted. */
  buttons?: MessageBoxButtons;
}

interface QueueEntry {
  id: number;
  options: MessageBoxOptions;
  resolve: (result: MessageBoxResult) => void;
}

type Lifecycle = 'pending' | 'mounted' | 'unmounted';

const BUTTON_RESULTS: Record<MessageBoxButtons, readonly MessageBoxResult[]> = {
  ok: ['ok'],
  yesNo: ['yes', 'no'],
  yesNoCancel: ['yes', 'no', 'cancel'],
};

const BUTTON_CAPTIONS: Record<MessageBoxResult, string> = {
  ok: 'OK',
  yes: 'Yes',
  no: 'No',
  cancel: 'Cancel',
};

const ESCAPE_RESULTS: Record<MessageBoxButtons, MessageBoxResult | null> = {
  ok: 'ok',
  yesNo: null,
  yesNoCancel: 'cancel',
};

const DISMISS_RESULTS: Record<MessageBoxButtons, MessageBoxResult> = {
  ok: 'ok',
  yesNo: 'no',
  yesNoCancel: 'cancel',
};

const ICON_GLYPHS: Record<MessageBoxIcon, string> = {
  information: '\u2139',
  question: '?',
  error: '\u2716',
  exclamation: '\u26A0',
};

const ICON_BASE_STYLE: CSSProperties = {
  flex: '0 0 auto',
  fontSize: '1.5rem',
  lineHeight: 1,
};

const ICON_STYLES: Record<MessageBoxIcon, CSSProperties> = {
  information: { ...ICON_BASE_STYLE, color: 'rgb(0, 80, 160)' },
  question: { ...ICON_BASE_STYLE, color: 'rgb(0, 80, 160)' },
  error: { ...ICON_BASE_STYLE, color: 'rgb(192, 0, 0)' },
  exclamation: { ...ICON_BASE_STYLE, color: 'rgb(154, 103, 0)' },
};

const OVERLAY_STYLE: CSSProperties = {
  position: 'fixed',
  inset: 0,
  zIndex: 1000,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  boxSizing: 'border-box',
  padding: '1rem',
  background: 'rgba(0, 0, 0, 0.3)',
};

const PANEL_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '30rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
  border: '1px solid rgb(128, 128, 128)',
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const HEADER_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.5rem',
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const MESSAGE_STYLE: CSSProperties = {
  whiteSpace: 'pre-wrap',
  overflowWrap: 'break-word',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  gap: '0.5rem',
};

const BUTTON_STYLE: CSSProperties = {
  minInlineSize: '5rem',
  paddingBlock: '0.25rem',
  paddingInline: '0.75rem',
};

function stopPropagation(event: SyntheticEvent): void {
  event.stopPropagation();
}

function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  event.stopPropagation();
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

function dismissEntries(entries: readonly QueueEntry[]): void {
  for (const entry of entries) {
    entry.resolve(DISMISS_RESULTS[entry.options.buttons ?? 'ok']);
  }
}

/**
 * Modal message box rendered into `document.body`. Each button calls `onClose` with its result; Escape calls it
 * with `'ok'` for an OK-only box and `'cancel'` for a Yes/No/Cancel box, and does nothing for a Yes/No box. The first
 * button is focused on mount, Tab and Shift+Tab cycle through the buttons, and the element that held the focus before
 * the box opened gets it back when the box closes and nothing else has taken it. Clicks outside the buttons do
 * nothing, a click on the backdrop leaves the focus where it was, and no click, double-click, mouse-down or key event
 * leaves the box.
 */
export function MessageBox(
  props: MessageBoxOptions & { onClose: (result: MessageBoxResult) => void },
): ReactElement {
  const { title, message, icon, buttons = 'ok', onClose } = props;
  const titleId = useId();
  const messageId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const firstButtonRef = useRef<HTMLButtonElement>(null);
  const results = BUTTON_RESULTS[buttons];

  useEffect(() => {
    const previous = document.activeElement;
    const dialog = dialogRef.current;
    firstButtonRef.current?.focus();

    return () => {
      const active = document.activeElement;
      const focusLost = active === null || active === document.body || (dialog !== null && dialog.contains(active));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    event.stopPropagation();

    if (event.key === 'Escape') {
      const result = ESCAPE_RESULTS[buttons];
      if (result !== null) {
        event.preventDefault();
        onClose(result);
      }
      return;
    }

    const dialog = dialogRef.current;
    if (event.key !== 'Tab' || dialog === null) {
      return;
    }

    const buttonElements = Array.from(dialog.querySelectorAll('button'));
    const count = buttonElements.length;
    if (count === 0) {
      return;
    }

    const index = buttonElements.findIndex((element) => element === document.activeElement);
    let next: number;
    if (index === -1) {
      next = event.shiftKey ? count - 1 : 0;
    } else {
      next = (index + (event.shiftKey ? count - 1 : 1)) % count;
    }

    event.preventDefault();
    buttonElements[next]?.focus();
  };

  return createPortal(
    <div
      style={OVERLAY_STYLE}
      onKeyDown={handleKeyDown}
      onKeyUp={stopPropagation}
      onClick={stopPropagation}
      onDoubleClick={stopPropagation}
      onMouseDown={handleOverlayMouseDown}
    >
      <div
        ref={dialogRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={messageId}
        style={PANEL_STYLE}
      >
        <div style={HEADER_STYLE}>
          {icon !== undefined && (
            <span role="img" aria-label={icon} style={ICON_STYLES[icon]}>
              {ICON_GLYPHS[icon]}
            </span>
          )}
          <h2 id={titleId} style={TITLE_STYLE}>
            {title}
          </h2>
        </div>
        <div id={messageId} style={MESSAGE_STYLE}>
          {message}
        </div>
        <div style={BUTTON_ROW_STYLE}>
          {results.map((result, index) => (
            <button
              key={result}
              ref={index === 0 ? firstButtonRef : undefined}
              type="button"
              style={BUTTON_STYLE}
              onClick={() => onClose(result)}
            >
              {BUTTON_CAPTIONS[result]}
            </button>
          ))}
        </div>
      </div>
    </div>,
    document.body,
  );
}

/**
 * Returns the OK-only message box for an error outcome: the outcome heading as the title, its body as the message,
 * and the `'error'` icon for the `'exception'` kind or no icon for the `'lock'` and `'deadlock'` kinds.
 */
export function errorOutcomeOptions(outcome: ErrorOutcome): MessageBoxOptions {
  const options: MessageBoxOptions = { title: outcome.heading, message: outcome.body, buttons: 'ok' };
  if (outcome.kind === 'exception') {
    options.icon = 'error';
  }
  return options;
}

/**
 * Queues message boxes for one component. `element` is the box at the head of the queue, or null when the queue is
 * empty, and is rendered once by the component. `show` appends a box and resolves with the button that closes it;
 * boxes are shown one at a time in the order they were requested. `showError` shows the box of an
 * {@link ErrorOutcomeError} and resolves once it is closed, resolves without a box for an {@link UnauthorizedError},
 * and rejects with any other value. Both functions keep their identity for the component's lifetime. On unmount,
 * every queued box resolves without being shown further: `'ok'` for an OK-only box, `'no'` for a Yes/No box and
 * `'cancel'` for a Yes/No/Cancel box; a box requested after unmount resolves the same way.
 */
export function useMessageBox(): {
  element: ReactElement | null;
  show: (o: MessageBoxOptions) => Promise<MessageBoxResult>;
  showError: (e: unknown) => Promise<void>;
} {
  const queueRef = useRef<QueueEntry[]>([]);
  const nextIdRef = useRef(1);
  const lifecycleRef = useRef<Lifecycle>('pending');
  const [queue, setQueue] = useState<QueueEntry[]>(queueRef.current);

  useEffect(() => {
    lifecycleRef.current = 'mounted';
    setQueue(queueRef.current);

    return () => {
      lifecycleRef.current = 'unmounted';
      const pending = queueRef.current;
      queueRef.current = [];
      dismissEntries(pending);
    };
  }, []);

  const show = useCallback(
    (o: MessageBoxOptions): Promise<MessageBoxResult> =>
      new Promise<MessageBoxResult>((resolve) => {
        const entry: QueueEntry = { id: nextIdRef.current, options: { ...o }, resolve };
        nextIdRef.current += 1;
        queueRef.current = [...queueRef.current, entry];

        if (lifecycleRef.current === 'mounted') {
          setQueue(queueRef.current);
        } else if (lifecycleRef.current === 'unmounted') {
          queueMicrotask(() => {
            if (lifecycleRef.current === 'unmounted') {
              const pending = queueRef.current;
              queueRef.current = [];
              dismissEntries(pending);
            }
          });
        }
      }),
    [],
  );

  const showError = useCallback(
    async (e: unknown): Promise<void> => {
      if (e instanceof ErrorOutcomeError) {
        await show(errorOutcomeOptions(e.outcome));
        return;
      }
      if (e instanceof UnauthorizedError) {
        return;
      }
      throw e;
    },
    [show],
  );

  const close = useCallback((entry: QueueEntry, result: MessageBoxResult): void => {
    if (queueRef.current[0] !== entry) {
      return;
    }
    queueRef.current = queueRef.current.slice(1);
    if (lifecycleRef.current === 'mounted') {
      setQueue(queueRef.current);
    }
    entry.resolve(result);
  }, []);

  const head: QueueEntry | undefined = queue[0];
  const element =
    head === undefined ? null : (
      <MessageBox key={head.id} {...head.options} onClose={(result) => close(head, result)} />
    );

  return { element, show, showError };
}

