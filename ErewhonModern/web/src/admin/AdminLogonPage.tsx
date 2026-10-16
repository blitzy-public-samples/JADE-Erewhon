// Admin-area logon screen: the Administrator and Agent options, the agent list, the version label and the OK and
// Cancel buttons, which open an admin session for the chosen user or leave the admin area.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, FormEvent, KeyboardEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ErrorOutcomeError, UnauthorizedError } from '../api/client';
import type { LogonOptionsResponse } from '../api/types';
import { DialogButton, useMessageBox } from '../components/MessageBox';
import { msg } from '../messages/messages';
import { useSession } from '../session/SessionProvider';

/** State of the logon-options request. */
type LoadState =
  | { status: 'loading' }
  | { status: 'loaded'; options: LogonOptionsResponse }
  | { status: 'failed' };

/** Control that can show the focus outline. */
type FocusTarget = 'administrator' | 'user' | 'select';

const LOGON_OPTIONS_PATH = '/api/app/logon-options?area=admin';
const LOGON_CAPTION = 'logon';
const ADMINISTRATOR_CAPTION = 'Administrator';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';
const USER_FIELD_NAME = 'userName';

const BORDER = '1px solid rgb(128, 128, 128)';
const RULE = '1px solid rgb(214, 213, 212)';
const DISABLED_TEXT_COLOR = 'rgb(109, 109, 109)';

const FOCUS_STYLE: CSSProperties = {
  outline: '2px solid rgb(0, 80, 160)',
  outlineOffset: '1px',
};

const PAGE_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: 0,
  padding: '1rem',
};

const WINDOW_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '22.5rem',
  marginInline: 'auto',
  paddingBlockStart: '1.0625rem',
  paddingBlockEnd: '1.5625rem',
  paddingInlineStart: '1.25rem',
  paddingInlineEnd: '1.5625rem',
  border: BORDER,
  backgroundColor: 'white',
  color: 'black',
  fontFamily: 'Tahoma, "MS Sans Serif", sans-serif',
};

const FRAME_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '2.6875rem 4.5rem minmax(0, 9.5625rem) minmax(0, 1fr)',
  gridTemplateRows:
    'auto 0.375rem minmax(1.3125rem, auto) 0.625rem minmax(1.0625rem, auto) 0.4375rem minmax(1.25rem, auto) ' +
    '1.75rem minmax(1.25rem, auto)',
  minInlineSize: 0,
  margin: 0,
  paddingBlockEnd: '1.3125rem',
  backgroundColor: 'white',
  boxShadow: '7px 7px 14px rgba(0, 0, 0, 0.26)',
};

const HEADER_STYLE: CSSProperties = {
  gridRow: 1,
  gridColumn: '1 / -1',
  paddingBlockEnd: '7px',
  borderBlockEnd: RULE,
};

const BAND_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minBlockSize: '2.375rem',
  paddingBlockStart: '0.5625rem',
  paddingBlockEnd: '0.25rem',
  paddingInlineStart: '0.875rem',
  paddingInlineEnd: '0.875rem',
  /* BLITZY [A11Y]: the white "logon" caption on rgb(68, 140, 203) is 3.6:1, below WCAG AA 4.5:1 for normal text;
     implemented as the LogonForm header paints it; flagged for designer review. */
  backgroundColor: 'rgb(68, 140, 203)',
  borderBlockEnd: RULE,
};

const HEADING_STYLE: CSSProperties = {
  inlineSize: 'fit-content',
  minInlineSize: '3.375rem',
  margin: 0,
  fontFamily: '"Arial Black", Arial, sans-serif',
  fontSize: '0.8125rem',
  fontWeight: 900,
  lineHeight: '1.25rem',
  textAlign: 'end',
  overflowWrap: 'break-word',
  color: 'white',
};

const VERSION_STYLE: CSSProperties = {
  gridRow: 3,
  gridColumn: '2 / 4',
  alignSelf: 'end',
  margin: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const OPTION_LABEL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  alignSelf: 'center',
  gap: '0.375rem',
  minInlineSize: 0,
  overflowWrap: 'break-word',
};

const ADMINISTRATOR_ROW_STYLE: CSSProperties = { gridRow: 5, gridColumn: '2 / -1' };

const USER_ROW_STYLE: CSSProperties = { gridRow: 7, gridColumn: 2 };

const RADIO_STYLE: CSSProperties = {
  flex: '0 0 auto',
  margin: 0,
};

const SELECT_STYLE: CSSProperties = {
  gridRow: 7,
  gridColumn: 3,
  alignSelf: 'center',
  boxSizing: 'border-box',
  inlineSize: '100%',
  minInlineSize: 0,
  minBlockSize: '1.125rem',
  margin: 0,
  font: 'inherit',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  gridRow: 9,
  gridColumn: '1 / -1',
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'center',
  gap: '0.375rem',
};

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns `style` with the focus outline added when `focused` is true. */
function withFocus(style: CSSProperties, focused: boolean): CSSProperties {
  return focused ? { ...style, ...FOCUS_STYLE } : style;
}

/** Returns the style of an option label: `row` placement, with grey text when the option is disabled. */
function optionLabelStyle(row: CSSProperties, disabled: boolean): CSSProperties {
  return disabled
    ? { ...OPTION_LABEL_STYLE, ...row, color: DISABLED_TEXT_COLOR }
    : { ...OPTION_LABEL_STYLE, ...row };
}

/**
 * Logon screen of the admin area, rendered at `/admin` while no admin session exists.
 * - While mounted the document title is `Administration`; the previous title is restored on unmount.
 * - On mount it reads GET /api/app/logon-options?area=admin and renders no control until the answer arrives. A failed
 *   read shows its message box and leaves only Cancel.
 * - The Administrator option starts checked as `administratorSelected` gives and is disabled when
 *   `administratorEnabled` is false; the user option is captioned `userOptionCaption`. The user list, named by that
 *   caption, starts with an empty entry followed by the users in the order received, and is disabled while
 *   Administrator is checked. The version label reads `Version <version>`.
 * - Once loaded, focus moves to the user list when it is enabled, otherwise to OK.
 * - OK, or Enter on any control other than a button, logs on through the session provider with the Administrator
 *   choice and, for the user option, the selected name as listed (`''` when the empty entry is selected). A rejected
 *   user shows the response's message box; an error outcome shows its box; any other failure shows its message titled
 *   `Error`. The page stays as it is after each of these. A successful logon is held by the session provider.
 * - Cancel, or Escape on any control, navigates to `/`.
 * - While a logon request is outstanding, OK and Cancel are marked `aria-disabled` and OK, Cancel, Enter and Escape do
 *   nothing.
 */
export function AdminLogonPage() {
  const { logon } = useSession();
  const navigate = useNavigate();
  const { element, show, showError } = useMessageBox();
  const headingId = useId();
  const optionGroupName = useId();
  const selectRef = useRef<HTMLSelectElement>(null);
  const okRef = useRef<HTMLButtonElement>(null);
  const pendingRef = useRef(false);
  const [load, setLoad] = useState<LoadState>({ status: 'loading' });
  const [administratorChosen, setAdministratorChosen] = useState(false);
  const [selectedName, setSelectedName] = useState('');
  const [pending, setPending] = useState(false);
  const [focusVisible, setFocusVisible] = useState<FocusTarget | null>(null);

  const reportFailure = useCallback(
    async (error: unknown): Promise<void> => {
      if (error instanceof ErrorOutcomeError || error instanceof UnauthorizedError) {
        await showError(error);
        return;
      }
      await show({
        title: msg('Error'),
        message: error instanceof Error ? error.message : String(error),
        icon: 'error',
        buttons: 'ok',
      });
    },
    [show, showError],
  );

  useEffect(() => {
    const previousTitle = document.title;
    document.title = msg('Administration');
    return () => {
      document.title = previousTitle;
    };
  }, []);

  useEffect(() => {
    let current = true;
    api.get<LogonOptionsResponse>(LOGON_OPTIONS_PATH).then(
      (options) => {
        if (current) {
          setAdministratorChosen(options.administratorSelected);
          setLoad({ status: 'loaded', options });
        }
      },
      (error: unknown) => {
        if (current) {
          setLoad({ status: 'failed' });
          void reportFailure(error);
        }
      },
    );
    return () => {
      current = false;
    };
  }, [reportFailure]);

  const loaded = load.status === 'loaded';

  useEffect(() => {
    if (!loaded) {
      return;
    }
    const select = selectRef.current;
    if (select !== null && !select.disabled) {
      select.focus();
    } else {
      okRef.current?.focus();
    }
  }, [loaded]);

  const submit = async (): Promise<void> => {
    if (load.status !== 'loaded' || pendingRef.current) {
      return;
    }
    pendingRef.current = true;
    setPending(true);
    try {
      const response = await logon({
        area: 'admin',
        administrator: administratorChosen,
        userName: administratorChosen ? null : selectedName,
      });
      if (!response.succeeded) {
        await show({
          title: response.messageTitle ?? '',
          message: response.message ?? '',
          icon: 'error',
          buttons: 'ok',
        });
      }
    } catch (error: unknown) {
      await reportFailure(error);
    } finally {
      pendingRef.current = false;
      setPending(false);
    }
  };

  const cancel = (): void => {
    if (pendingRef.current) {
      return;
    }
    navigate('/');
  };

  const handleSubmit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    void submit();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLFormElement>): void => {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    if (event.key === 'Enter' && !(event.target instanceof HTMLButtonElement)) {
      event.preventDefault();
      void submit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      cancel();
    }
  };

  const trackFocus = (target: FocusTarget) => {
    const update = (event: FocusEvent<HTMLElement> | KeyboardEvent<HTMLElement>): void => {
      const visible = matchesFocusVisible(event.currentTarget);
      setFocusVisible((current) => {
        if (visible) {
          return target;
        }
        return current === target ? null : current;
      });
    };
    return {
      onFocus: update,
      onKeyUp: update,
      onBlur: () => setFocusVisible((current) => (current === target ? null : current)),
    };
  };

  return (
    <main style={PAGE_STYLE}>
      <div style={WINDOW_STYLE}>
        <form
          aria-labelledby={headingId}
          aria-busy={load.status === 'loading'}
          noValidate
          style={FRAME_STYLE}
          onSubmit={handleSubmit}
          onKeyDown={handleKeyDown}
        >
          <div style={HEADER_STYLE}>
            <div style={BAND_STYLE}>
              <h1 id={headingId} style={HEADING_STYLE}>
                {LOGON_CAPTION}
              </h1>
            </div>
          </div>
          {load.status === 'loaded' && (
            <>
              <p style={VERSION_STYLE}>{`${msg('BaseVersion')} ${load.options.version}`}</p>
              <label style={optionLabelStyle(ADMINISTRATOR_ROW_STYLE, !load.options.administratorEnabled)}>
                <input
                  type="radio"
                  name={optionGroupName}
                  checked={administratorChosen}
                  disabled={!load.options.administratorEnabled}
                  style={withFocus(RADIO_STYLE, focusVisible === 'administrator')}
                  onChange={() => setAdministratorChosen(true)}
                  {...trackFocus('administrator')}
                />
                {ADMINISTRATOR_CAPTION}
              </label>
              <label style={optionLabelStyle(USER_ROW_STYLE, false)}>
                <input
                  type="radio"
                  name={optionGroupName}
                  checked={!administratorChosen}
                  style={withFocus(RADIO_STYLE, focusVisible === 'user')}
                  onChange={() => setAdministratorChosen(false)}
                  {...trackFocus('user')}
                />
                {load.options.userOptionCaption}
              </label>
              <select
                ref={selectRef}
                name={USER_FIELD_NAME}
                autoComplete="off"
                aria-label={load.options.userOptionCaption}
                value={selectedName}
                disabled={administratorChosen}
                style={withFocus(SELECT_STYLE, focusVisible === 'select' && !administratorChosen)}
                onChange={(event) => setSelectedName(event.target.value)}
                {...trackFocus('select')}
              >
                <option value="" />
                {load.options.users.map((user) => (
                  <option key={user.id} value={user.name}>
                    {user.name}
                  </option>
                ))}
              </select>
              <div style={BUTTON_ROW_STYLE}>
                <DialogButton ref={okRef} aria-disabled={pending} onClick={() => void submit()}>
                  {OK_CAPTION}
                </DialogButton>
                <DialogButton aria-disabled={pending} onClick={cancel}>
                  {CANCEL_CAPTION}
                </DialogButton>
              </div>
            </>
          )}
          {load.status === 'failed' && (
            <div style={BUTTON_ROW_STYLE}>
              <DialogButton onClick={cancel}>{CANCEL_CAPTION}</DialogButton>
            </div>
          )}
        </form>
      </div>
      {element}
    </main>
  );
}
