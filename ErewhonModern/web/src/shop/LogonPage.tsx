// Shop logon screen: loads the shop-area logon options, lists the company's clients, logs the chosen client on to the
// shop area through the session provider and shows the message of a rejected or failed logon.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FormEvent, KeyboardEvent, ReactElement } from 'react';
import { ErrorOutcomeError, UnauthorizedError, api } from '../api/client';
import type { LogonOptionsResponse } from '../api/types';
import { DialogButton, useMessageBox } from '../components/MessageBox';
import { msg } from '../messages/messages';
import { useSession } from '../session/SessionProvider';

const LOGON_OPTIONS_PATH = '/api/app/logon-options?area=shop';
const PAGE_TITLE = 'Erewhon Investments Inc - Logon';
const HEADING_CAPTION = 'logon';
const ADMINISTRATOR_CAPTION = 'Administrator';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';
const NO_USER = '';
const USER_LIST_NAME = 'userName';

const DISABLED_TEXT_COLOR = 'rgb(109, 109, 109)';

const MAIN_STYLE: CSSProperties = {
  display: 'flex',
  justifyContent: 'center',
  alignItems: 'flex-start',
  boxSizing: 'border-box',
  paddingBlock: '2rem',
  paddingInline: '1rem',
};

const PANEL_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '22.5rem',
  margin: 0,
  background: 'white',
  color: 'black',
  border: '1px solid rgb(128, 128, 128)',
  boxShadow: '6px 6px 8px rgba(0, 0, 0, 0.25)',
};

const HEADER_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  boxSizing: 'border-box',
  blockSize: '2.5rem',
  paddingInline: '0.9375rem',
  background: 'rgb(68, 140, 203)',
  borderBlockEnd: '1px solid rgb(214, 213, 212)',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontFamily: '"Arial Black", Arial, sans-serif',
  fontSize: '0.8125rem',
  fontWeight: 900,
  color: 'white',
};

const BODY_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1rem',
  paddingBlockStart: '0.5rem',
  paddingBlockEnd: '1rem',
  paddingInline: '1rem',
};

const VERSION_STYLE: CSSProperties = {
  margin: 0,
  overflowWrap: 'break-word',
};

const CHOICES_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'auto minmax(0, 1fr)',
  alignItems: 'center',
  columnGap: '0.75rem',
  rowGap: '0.5rem',
  paddingInlineStart: '0.5rem',
};

const OPTION_LABEL_STYLE: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: '0.25rem',
  gridColumn: '1',
  color: 'black',
};

const OPTION_LABEL_DISABLED_STYLE: CSSProperties = {
  ...OPTION_LABEL_STYLE,
  color: DISABLED_TEXT_COLOR,
};

const OPTION_INPUT_STYLE: CSSProperties = {
  margin: 0,
};

const USER_LIST_STYLE: CSSProperties = {
  gridColumn: '2',
  boxSizing: 'border-box',
  inlineSize: '100%',
  minInlineSize: 0,
  font: 'inherit',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'center',
  gap: '0.375rem',
};

/**
 * Logon screen of the shop area.
 * - On mount it reads GET /api/app/logon-options?area=shop and sets the document title to
 *   "Erewhon Investments Inc - Logon", restoring the previous title on unmount. Until the options arrive, the options
 *   and the user list are disabled and OK and Cancel are unavailable; a failed read is shown in a message box.
 * - It shows the "logon" heading, the version text (`$BaseVersion`, a space and the server's version), the
 *   Administrator option, enabled and selected as the options state, the user option captioned with the options'
 *   user caption, and the user list beside it, named by that caption, holding an empty entry and then the options'
 *   users in the order received. Selecting the Administrator option disables the user list.
 * - OK, or Enter pressed outside the buttons, sends the shop-area logon with the Administrator option state and the
 *   selected user's name, or `''` with no user selected, through {@link useSession}. A rejected logon shows the
 *   returned title and message with the error icon; an error outcome, such as the one for no selected user, is shown
 *   through `showError`; any other failure shows its message titled `$Error`. A successful logon leaves the new shop
 *   session in the session provider, and the page stays as it is.
 * - Cancel, or Escape, clears the user selection and restores the option selection of the loaded options.
 */
export function LogonPage(): ReactElement {
  const { logon } = useSession();
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();
  const optionGroupName = useId();
  const userCaptionId = useId();
  const [options, setOptions] = useState<LogonOptionsResponse | null>(null);
  const [administrator, setAdministrator] = useState(false);
  const [selectedUserId, setSelectedUserId] = useState(NO_USER);
  const [submitting, setSubmitting] = useState(false);
  const submittingRef = useRef(false);

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
    document.title = PAGE_TITLE;
    return () => {
      document.title = previousTitle;
    };
  }, []);

  useEffect(() => {
    let active = true;

    api.get<LogonOptionsResponse>(LOGON_OPTIONS_PATH).then(
      (loaded) => {
        if (active) {
          setOptions(loaded);
          setAdministrator(loaded.administratorSelected);
        }
      },
      (error: unknown) => {
        if (active) {
          void reportFailure(error);
        }
      },
    );

    return () => {
      active = false;
    };
  }, [reportFailure]);

  const loading = options === null;
  const busy = loading || submitting;
  const users = options?.users ?? [];
  const userOptionCaption = options?.userOptionCaption ?? msg('Client');
  const administratorDisabled = loading || !options.administratorEnabled;
  const userListDisabled = loading || administrator;
  const versionText = loading ? msg('BaseVersion') : `${msg('BaseVersion')} ${options.version}`;

  const handleOk = async (): Promise<void> => {
    if (options === null || submittingRef.current) {
      return;
    }

    const selectedUser = users.find((user) => String(user.id) === selectedUserId);
    submittingRef.current = true;
    setSubmitting(true);

    try {
      const response = await logon({ area: 'shop', administrator, userName: selectedUser?.name ?? '' });
      if (!response.succeeded) {
        await show({
          title: response.messageTitle ?? msg('Error'),
          message: response.message ?? '',
          icon: 'error',
          buttons: 'ok',
        });
      }
    } catch (error: unknown) {
      await reportFailure(error);
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  const handleCancel = (): void => {
    if (options === null || submittingRef.current) {
      return;
    }

    setSelectedUserId(NO_USER);
    setAdministrator(options.administratorSelected);
  };

  const handleSubmit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    void handleOk();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLFormElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      handleCancel();
      return;
    }

    if (event.key === 'Enter' && !(event.target instanceof HTMLButtonElement)) {
      event.preventDefault();
      void handleOk();
    }
  };

  const handleUserChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    setSelectedUserId(event.target.value);
  };

  return (
    <main style={MAIN_STYLE}>
      <form
        aria-labelledby={headingId}
        aria-busy={busy}
        style={PANEL_STYLE}
        onSubmit={handleSubmit}
        onKeyDown={handleKeyDown}
      >
        <div style={HEADER_STYLE}>
          <h1 id={headingId} style={HEADING_STYLE}>
            {HEADING_CAPTION}
          </h1>
        </div>
        <div style={BODY_STYLE}>
          <p style={VERSION_STYLE}>{versionText}</p>
          <div style={CHOICES_STYLE}>
            <label style={administratorDisabled ? OPTION_LABEL_DISABLED_STYLE : OPTION_LABEL_STYLE}>
              <input
                type="radio"
                name={optionGroupName}
                checked={administrator}
                disabled={administratorDisabled}
                style={OPTION_INPUT_STYLE}
                onChange={() => setAdministrator(true)}
              />
              <span>{ADMINISTRATOR_CAPTION}</span>
            </label>
            <label style={loading ? OPTION_LABEL_DISABLED_STYLE : OPTION_LABEL_STYLE}>
              <input
                type="radio"
                name={optionGroupName}
                checked={!administrator}
                disabled={loading}
                style={OPTION_INPUT_STYLE}
                onChange={() => setAdministrator(false)}
              />
              <span id={userCaptionId}>{userOptionCaption}</span>
            </label>
            <select
              name={USER_LIST_NAME}
              aria-labelledby={userCaptionId}
              value={selectedUserId}
              disabled={userListDisabled}
              style={USER_LIST_STYLE}
              onChange={handleUserChange}
            >
              <option value={NO_USER} />
              {users.map((user) => (
                <option key={user.id} value={String(user.id)}>
                  {user.name}
                </option>
              ))}
            </select>
          </div>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={busy} onClick={() => void handleOk()}>
              {OK_CAPTION}
            </DialogButton>
            <DialogButton aria-disabled={busy} onClick={handleCancel}>
              {CANCEL_CAPTION}
            </DialogButton>
          </div>
        </div>
      </form>
      {messageBox}
    </main>
  );
}
