import { forwardRef, useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { api } from '../../api/client';
import { on } from '../../api/realtime';
import type {
  CloseNowRequest,
  CloseNowResponse,
  ClosureScheduleStateResponse,
  IsoDate,
  StartClosureScheduleRequest,
  StartClosureScheduleResponse,
  StopClosureScheduleResponse,
} from '../../api/types';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { dateString } from '../../format';
import { msg } from '../../messages/messages';

const SCHEDULE_PATH = '/api/app/admin/tenders/closure-schedule';
const CLOSE_NOW_PATH = '/api/app/admin/tenders/close-now';
const START_PATH = '/api/app/admin/tenders/closure-schedule/start';
const STOP_PATH = '/api/app/admin/tenders/closure-schedule/stop';

const DIALOG_CAPTION = 'Close Tenders';
const DESCRIPTION =
  'Closes bids on tender items where the closure date has been reached.\n' +
  'Note: Tenders are automatically closed when the Admin or Shop form app first opens.';
const IMMEDIATE_CAPTION = 'Close Tenders as at Current Date';
const CLOSE_NOW_CAPTION = 'Close Tenders';
const TENDERS_CLOSED_CAPTION = 'Tenders closed:';
const SCHEDULED_CAPTION = 'Simulate Daily Tender Closures';
const INITIAL_DATE_CAPTION = 'Initial Closure Date';
const PERFORM_EVERY_CAPTION = 'Perform Daily Closure every:';
const MINUTES_CAPTION = '(minutes)';
const START_CAPTION = 'Start';
const STOP_CAPTION = 'Stop';
const LAST_DATE_CAPTION = 'Closure date for last interval:';
const LAST_CLOSED_CAPTION = 'Tenders closed in last interval:';
const CLOSE_FORM_CAPTION = 'Close Form';

const SECONDS_PER_MINUTE = 60;
const MINUTES_MAX_LENGTH = 8;
const MAX_YEAR = 9999;
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const NON_DIGITS = /\D/g;
const FOCUSABLE_SELECTOR = 'button:not(:disabled), input:not(:disabled), [tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const GAP = '0.5rem';
const LABEL_COLUMN = '12.5rem';

const OVERLAY_STYLE: CSSProperties = {
  position: 'fixed',
  inset: 0,
  zIndex: 900,
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
  maxInlineSize: '34rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const BUSY_PANEL_STYLE: CSSProperties = {
  ...PANEL_STYLE,
  cursor: 'progress',
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
};

const DESCRIPTION_STYLE: CSSProperties = {
  margin: 0,
  whiteSpace: 'pre-line',
  overflowWrap: 'break-word',
};

const FIELDSET_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GAP,
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInlineStart: '0.625rem',
  paddingInlineEnd: '0.75rem',
};

const ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: GAP,
};

const FIELD_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  columnGap: GAP,
  rowGap: '0.25rem',
  flex: '1 1 auto',
  minInlineSize: 0,
};

const LABEL_STYLE: CSSProperties = {
  flex: '0 1 auto',
  minInlineSize: 0,
  overflowWrap: 'break-word',
};

const SCHEDULE_LABEL_STYLE: CSSProperties = {
  ...LABEL_STYLE,
  flexBasis: LABEL_COLUMN,
};

const VALUE_STYLE: CSSProperties = {
  flex: '1 1 auto',
  minInlineSize: 0,
  overflowWrap: 'anywhere',
};

const UNIT_STYLE: CSSProperties = {
  flex: '0 0 auto',
};

const FOOTER_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: GAP,
};

const SECONDS_STYLE: CSSProperties = {
  flex: '1 1 0',
  minInlineSize: 0,
  overflowWrap: 'anywhere',
};

const INPUT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 0 auto',
  maxInlineSize: '100%',
  minBlockSize: '1.375rem',
  margin: 0,
  paddingBlock: 0,
  paddingInline: '0.25rem',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: INPUT_BORDER_COLOR,
  backgroundColor: '#FFFFFF',
  color: '#000000',
  font: 'inherit',
};

const MINUTES_INPUT_STYLE: CSSProperties = {
  ...INPUT_STYLE,
  inlineSize: '5.3125rem',
};

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/**
 * Returns `base` with the pressed or hovered border, pressed taking precedence, and the focus outline added when
 * `focusVisible` is true.
 */
function inputStyle(base: CSSProperties, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = base;
  if (pressed) {
    style = { ...base, borderColor: INPUT_PRESSED_BORDER_COLOR };
  } else if (hovered) {
    style = { ...base, borderColor: INPUT_HOVER_BORDER_COLOR };
  }
  return focusVisible ? { ...style, outline: FOCUS_OUTLINE, outlineOffset: 0 } : style;
}

/** Returns the seconds line for `seconds`, e.g. `60 seconds to go`. */
function secondsText(seconds: number): string {
  return `${seconds} ${msg('SecsToGo')}`;
}

/**
 * Returns the `yyyy-MM-dd` calendar date one day after `iso` on the Gregorian calendar, or null when `iso` is not a
 * `yyyy-MM-dd` date or the next day falls after the year 9999.
 */
function nextDay(iso: IsoDate): IsoDate | null {
  const match = ISO_DATE.exec(iso);
  if (match === null) {
    return null;
  }

  const [, year, month, day] = match;
  const date = new Date(0);
  date.setUTCFullYear(Number(year), Number(month) - 1, Number(day) + 1);
  const nextYear = date.getUTCFullYear();
  if (Number.isNaN(nextYear) || nextYear > MAX_YEAR) {
    return null;
  }

  const yyyy = String(nextYear).padStart(4, '0');
  const mm = String(date.getUTCMonth() + 1).padStart(2, '0');
  const dd = String(date.getUTCDate()).padStart(2, '0');
  return `${yyyy}-${mm}-${dd}`;
}

/** Settles a stop request sent after the dialog has closed, leaving no rejection behind. */
function ignoreStopFailure(): undefined {
  return undefined;
}

/** Sends POST /api/app/admin/tenders/closure-schedule/stop without waiting for or reporting its outcome. */
function sendStopAfterClose(): void {
  api.post<StopClosureScheduleResponse>(STOP_PATH).catch(ignoreStopFailure);
}

/** Properties of {@link DialogInput}. */
interface DialogInputProps {
  id: string;
  type: 'date' | 'text';
  value: string;
  /** Receives the input's value after every edit. */
  onValueChange: (value: string) => void;
  /** Base style of the input, before the hover, pressed and focus states. */
  baseStyle: CSSProperties;
  inputMode?: 'numeric';
  maxLength?: number;
  'aria-describedby'?: string;
}

/**
 * Single-line input of the dialog: a #AEAEAE border at rest, a rgb(128, 128, 128) border while the pointer is over
 * it, a rgb(0, 80, 160) border while the primary mouse button is held on it, and, focused with `:focus-visible`
 * (checked again on every key release), a 2px rgb(0, 80, 160) outline drawn against its border.
 */
const DialogInput = forwardRef<HTMLInputElement, DialogInputProps>(function DialogInput(props, ref) {
  const { id, type, value, onValueChange, baseStyle, inputMode, maxLength, 'aria-describedby': describedBy } = props;
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const updateFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return (
    <input
      ref={ref}
      id={id}
      type={type}
      value={value}
      inputMode={inputMode}
      maxLength={maxLength}
      autoComplete="off"
      aria-describedby={describedBy}
      style={inputStyle(baseStyle, pressed, hovered, focusVisible)}
      onChange={(event: ChangeEvent<HTMLInputElement>) => onValueChange(event.target.value)}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => {
        setHovered(false);
        setPressed(false);
      }}
      onMouseDown={(event: MouseEvent<HTMLInputElement>) => {
        if (event.button === 0) {
          setPressed(true);
        }
      }}
      onMouseUp={() => setPressed(false)}
      onFocus={updateFocusVisible}
      onKeyUp={updateFocusVisible}
      onBlur={() => setFocusVisible(false)}
    />
  );
});

/**
 * The modal "Close Tenders" dialog of the administration area: closes the tenders due at an entered date at once, and
 * starts and stops the session's daily closure schedule, whose countdown and closures run on the server.
 *
 * - On mount it reads GET /api/app/admin/tenders/closure-schedule. The date input shows the running schedule's date,
 *   else the server date; a running schedule disables Start, enables Stop and shows its minutes and seconds to go.
 *   Until that answer, and while any later request is outstanding, Close Tenders, Start and Stop have
 *   `aria-disabled="true"` and the panel shows the progress cursor.
 * - Close Tenders enables Start, disables Stop and empties the seconds line, then posts close-now with the date and
 *   shows the number of items awarded under "Tenders closed:". With no date it shows 0, posts a stop when a schedule
 *   was running, and closes nothing.
 * - Start posts closure-schedule/start with the date and the minutes entered, 0 when none; a started schedule disables
 *   Start, enables Stop and shows `<minutes × 60> seconds to go` unless a schedule event has already arrived, and one
 *   that did not start leaves Start enabled and Stop disabled. With no date it sends nothing.
 * - Stop enables Start, disables Stop, empties the seconds line and posts closure-schedule/stop.
 * - While a schedule runs, each ClosureTick shows its seconds to go, and each ClosureCompleted shows its date and count
 *   as the last interval, moves the date input one day past that date and shows the full countdown again. Events that
 *   arrive while no schedule is running or starting change nothing.
 * - Close Form and Escape post a stop when a schedule is running or starting, wait for it and then call `onClose`;
 *   unmounting with a schedule running or starting posts a stop without waiting, and a start or state answer that
 *   reports a running schedule after the dialog closed posts one too.
 * - A failed request leaves Start enabled and Stop disabled; an error outcome is shown in a message box, an HTTP 401
 *   shows nothing, and any other failure rejects unhandled.
 *
 * The date input is focused on mount, Tab and Shift+Tab wrap within the dialog, and the element focused before the
 * dialog opened gets the focus back when the dialog closes and nothing else has taken it.
 */
export function CloseTendersDialog({ onClose }: { onClose: () => void }): ReactElement {
  const { element: messageBox, showError } = useMessageBox();
  const titleId = useId();
  const descriptionId = useId();
  const idPrefix = useId();
  const dateInputId = `${idPrefix}date`;
  const minutesInputId = `${idPrefix}minutes`;
  const minutesUnitId = `${idPrefix}unit`;
  const numClosedNowId = `${idPrefix}closedNow`;
  const lastDateId = `${idPrefix}lastDate`;
  const lastClosedId = `${idPrefix}lastClosed`;

  const [closeDate, setCloseDate] = useState<IsoDate>('');
  const [minutesText, setMinutesText] = useState('');
  const [numClosedNow, setNumClosedNow] = useState('0');
  const [running, setRunning] = useState(false);
  const [secondsLine, setSecondsLine] = useState('');
  const [lastClosureDate, setLastClosureDate] = useState(() => msg('None'));
  const [numClosedLast, setNumClosedLast] = useState('0');
  const [busy, setBusy] = useState(true);

  const busyRef = useRef(true);
  const runningRef = useRef(false);
  const minutesRef = useRef(0);
  const mountedRef = useRef(false);
  const closingRef = useRef(false);
  const eventSinceStartRef = useRef(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const dateInputRef = useRef<HTMLInputElement>(null);

  const setBusyState = (next: boolean): void => {
    busyRef.current = next;
    setBusy(next);
  };

  const showStopped = (): void => {
    runningRef.current = false;
    setRunning(false);
    setSecondsLine('');
  };

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
      if (runningRef.current) {
        runningRef.current = false;
        sendStopAfterClose();
      }
    };
  }, []);

  useEffect(() => {
    let active = true;

    const load = async (): Promise<void> => {
      let state: ClosureScheduleStateResponse;
      try {
        state = await api.get<ClosureScheduleStateResponse>(SCHEDULE_PATH);
      } catch (error: unknown) {
        if (!active) {
          return;
        }
        setBusyState(false);
        await showError(error);
        return;
      }

      if (!mountedRef.current || closingRef.current) {
        if (state.running) {
          sendStopAfterClose();
        }
        return;
      }

      if (!active) {
        return;
      }

      setCloseDate(state.date ?? state.serverDate);
      if (state.running) {
        const minutes = state.minutes ?? 0;
        minutesRef.current = minutes;
        runningRef.current = true;
        setMinutesText(String(minutes));
        setRunning(true);
        setSecondsLine(state.secondsToGo === null ? '' : secondsText(state.secondsToGo));
      }
      setBusyState(false);
    };

    void load();

    return () => {
      active = false;
    };
  }, [showError]);

  useEffect(() => {
    const offTick = on('ClosureTick', (message) => {
      if (runningRef.current) {
        eventSinceStartRef.current = true;
        setSecondsLine(secondsText(message.secondsToGo));
      }
    });

    const offCompleted = on('ClosureCompleted', (message) => {
      if (!runningRef.current) {
        return;
      }
      eventSinceStartRef.current = true;
      setLastClosureDate(dateString(message.date));
      setNumClosedLast(String(message.numClosed));
      const next = nextDay(message.date);
      if (next !== null) {
        setCloseDate(next);
      }
      setSecondsLine(secondsText(minutesRef.current * SECONDS_PER_MINUTE));
    });

    return () => {
      offTick();
      offCompleted();
    };
  }, []);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    dateInputRef.current?.focus();

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  /**
   * Runs `operation` with the action buttons disabled and resolves to its value, or to null when it failed, in which
   * case Start is enabled, Stop disabled, the seconds line emptied and the failure passed to `showError`.
   */
  const request = async <T,>(operation: () => Promise<T>): Promise<{ value: T } | null> => {
    setBusyState(true);
    let outcome: { value: T } | null = null;
    let failure: { error: unknown } | null = null;
    try {
      outcome = { value: await operation() };
    } catch (error: unknown) {
      failure = { error };
    } finally {
      setBusyState(false);
    }

    if (failure !== null) {
      showStopped();
      await showError(failure.error);
    }
    return outcome;
  };

  const handleCloseNow = async (): Promise<void> => {
    if (busyRef.current || closingRef.current) {
      return;
    }

    const wasRunning = runningRef.current;
    showStopped();

    if (closeDate === '') {
      setNumClosedNow('0');
      if (wasRunning) {
        await request(() => api.post<StopClosureScheduleResponse>(STOP_PATH));
      }
      return;
    }

    const body: CloseNowRequest = { date: closeDate };
    const outcome = await request(() => api.post<CloseNowResponse>(CLOSE_NOW_PATH, body));
    if (outcome !== null) {
      setNumClosedNow(String(outcome.value.numClosed));
    }
  };

  const handleStart = async (): Promise<void> => {
    if (busyRef.current || closingRef.current || runningRef.current || closeDate === '') {
      return;
    }

    const minutes = Number.parseInt(minutesText.trim(), 10) || 0;
    const body: StartClosureScheduleRequest = { date: closeDate, minutes };
    runningRef.current = true;
    eventSinceStartRef.current = false;
    const outcome = await request(() => api.post<StartClosureScheduleResponse>(START_PATH, body));
    if (outcome === null) {
      return;
    }

    const { started, secondsToGo } = outcome.value;
    if (!mountedRef.current || closingRef.current) {
      if (started) {
        sendStopAfterClose();
      }
      return;
    }

    if (started) {
      minutesRef.current = minutes;
      runningRef.current = true;
      setRunning(true);
      if (!eventSinceStartRef.current) {
        setSecondsLine(secondsText(secondsToGo));
      }
    } else {
      showStopped();
    }
  };

  const handleStop = async (): Promise<void> => {
    if (busyRef.current || closingRef.current || !runningRef.current) {
      return;
    }

    showStopped();
    await request(() => api.post<StopClosureScheduleResponse>(STOP_PATH));
  };

  const requestClose = async (): Promise<void> => {
    if (closingRef.current) {
      return;
    }

    closingRef.current = true;
    try {
      if (runningRef.current) {
        showStopped();
        await request(() => api.post<StopClosureScheduleResponse>(STOP_PATH));
      }
    } finally {
      onClose();
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void requestClose();
      return;
    }

    const panel = panelRef.current;
    if (event.key !== 'Tab' || panel === null) {
      return;
    }

    const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
    if (focusable.length === 0) {
      return;
    }

    const last = focusable.length - 1;
    const index = focusable.findIndex((element) => element === document.activeElement);
    let next: number | null = null;
    if (index === -1) {
      next = event.shiftKey ? last : 0;
    } else if (event.shiftKey && index === 0) {
      next = last;
    } else if (!event.shiftKey && index === last) {
      next = 0;
    }

    if (next !== null) {
      event.preventDefault();
      focusable[next]?.focus();
    }
  };

  const handleMinutesChange = (text: string): void => {
    setMinutesText(text.replace(NON_DIGITS, '').slice(0, MINUTES_MAX_LENGTH));
  };

  return (
    <div style={OVERLAY_STYLE}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        tabIndex={-1}
        style={busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 id={titleId} style={TITLE_STYLE}>
          {DIALOG_CAPTION}
        </h2>
        <p id={descriptionId} style={DESCRIPTION_STYLE}>
          {DESCRIPTION}
        </p>
        <fieldset style={FIELDSET_STYLE}>
          <legend>{IMMEDIATE_CAPTION}</legend>
          <div style={ROW_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={numClosedNowId} style={LABEL_STYLE}>
                {TENDERS_CLOSED_CAPTION}
              </label>
              <output id={numClosedNowId} style={VALUE_STYLE}>
                {numClosedNow}
              </output>
            </div>
            <DialogButton aria-disabled={busy} onClick={() => void handleCloseNow()}>
              {CLOSE_NOW_CAPTION}
            </DialogButton>
          </div>
        </fieldset>
        <fieldset style={FIELDSET_STYLE}>
          <legend>{SCHEDULED_CAPTION}</legend>
          <div style={ROW_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={dateInputId} style={SCHEDULE_LABEL_STYLE}>
                {INITIAL_DATE_CAPTION}
              </label>
              <DialogInput
                ref={dateInputRef}
                id={dateInputId}
                type="date"
                value={closeDate}
                onValueChange={setCloseDate}
                baseStyle={INPUT_STYLE}
              />
            </div>
            <DialogButton aria-disabled={busy || running} onClick={() => void handleStart()}>
              {START_CAPTION}
            </DialogButton>
          </div>
          <div style={ROW_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={minutesInputId} style={SCHEDULE_LABEL_STYLE}>
                {PERFORM_EVERY_CAPTION}
              </label>
              <DialogInput
                id={minutesInputId}
                type="text"
                inputMode="numeric"
                maxLength={MINUTES_MAX_LENGTH}
                value={minutesText}
                onValueChange={handleMinutesChange}
                baseStyle={MINUTES_INPUT_STYLE}
                aria-describedby={minutesUnitId}
              />
              <span id={minutesUnitId} style={UNIT_STYLE}>
                {MINUTES_CAPTION}
              </span>
            </div>
            <DialogButton aria-disabled={busy || !running} onClick={() => void handleStop()}>
              {STOP_CAPTION}
            </DialogButton>
          </div>
          <div style={FIELD_STYLE}>
            <label htmlFor={lastDateId} style={SCHEDULE_LABEL_STYLE}>
              {LAST_DATE_CAPTION}
            </label>
            <output id={lastDateId} style={VALUE_STYLE}>
              {lastClosureDate}
            </output>
          </div>
          <div style={FIELD_STYLE}>
            <label htmlFor={lastClosedId} style={SCHEDULE_LABEL_STYLE}>
              {LAST_CLOSED_CAPTION}
            </label>
            <output id={lastClosedId} style={VALUE_STYLE}>
              {numClosedLast}
            </output>
          </div>
        </fieldset>
        <div style={FOOTER_STYLE}>
          <div role="timer" style={SECONDS_STYLE}>
            {secondsLine}
          </div>
          <DialogButton onClick={() => void requestClose()}>{CLOSE_FORM_CAPTION}</DialogButton>
        </div>
      </div>
      {messageBox}
    </div>
  );
}
