import { useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, KeyboardEvent, ReactElement } from 'react';
import { api } from '../api/client';
import type { AboutResponse } from '../api/types';
import { DialogButton, useMessageBox } from '../components/MessageBox';
import { msg } from '../messages/messages';

const ABOUT_PATH = '/api/app/about';

const DIALOG_CAPTION = 'About Erewhon Investments';

const COPYRIGHT_CAPTION = '© 2005-2023 JADE SOFTWARE CORPORATION LIMITED - ALL RIGHTS RESERVED';

const LEGAL_CAPTION =
  'Copyright in the whole and every part of this program belongs to Jade Software Corporation Limited. ' +
  'You may use the program for your own internal business purposes and use the classes of the demonstration ' +
  'system in your own systems, but you shall not otherwise use, sell, licence, transfer, copy or reproduce in ' +
  'whole or in part in any manner or form the program in or on any media to any person otherwise without the ' +
  'prior written consent of Jade Software Corporation Limited.' +
  '\n\n' +
  'Note: The full names, contact details and products referred to in the program are fictional and bear no ' +
  'relation to any real person or product.';

const OK_CAPTION = 'OK';

const BORDER = '1px solid rgb(128, 128, 128)';

const NOTICE_BACKGROUND = 'rgb(192, 192, 192)';

const TEXT_SIZE = '0.8125rem';

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
  maxInlineSize: '55.09em',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
  fontSize: TEXT_SIZE,
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const CONTENT_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'center',
  gap: '2.55em',
};

const NOTICE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'center',
  gap: '0.55em',
  inlineSize: '100%',
};

const CENTRED_LABEL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  boxSizing: 'border-box',
  inlineSize: '100%',
  margin: 0,
  textAlign: 'center',
  overflowWrap: 'break-word',
};

const VERSION_STYLE: CSSProperties = {
  ...CENTRED_LABEL_STYLE,
  maxInlineSize: '28.09em',
  minBlockSize: '2.18em',
};

const COPYRIGHT_STYLE: CSSProperties = {
  ...CENTRED_LABEL_STYLE,
  maxInlineSize: '45.73em',
  minBlockSize: '1.36em',
  background: NOTICE_BACKGROUND,
  fontWeight: 'bold',
};

const LEGAL_STYLE: CSSProperties = {
  ...CENTRED_LABEL_STYLE,
  maxInlineSize: '44.82em',
  minBlockSize: '10.64em',
  background: NOTICE_BACKGROUND,
  whiteSpace: 'pre-wrap',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  justifyContent: 'center',
};

/**
 * The "About Erewhon Investments" dialog: the caption as its heading and accessible name, the version label, the
 * copyright line, the legal notice and an OK button.
 *
 * The version label reads "Version" until GET /api/app/about answers, then "Version <version>" (for example
 * "Version 2.0"). An `ErrorOutcomeError` from that request is shown in a message box, an `UnauthorizedError` shows
 * nothing, any other error rejects unhandled, and an answer that arrives after unmount is ignored.
 *
 * OK and Escape call `onClose`, and so does Enter pressed anywhere in the dialog outside the button. OK is focused on
 * mount and keeps the focus on Tab and Shift+Tab, and the element focused before the dialog opened gets the focus
 * back when the dialog closes and nothing else has taken it.
 */
export function AboutDialog({ onClose }: { onClose: () => void }): ReactElement {
  const { element: messageBox, showError } = useMessageBox();
  const [version, setVersion] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const okButtonRef = useRef<HTMLButtonElement>(null);
  const titleId = useId();

  useEffect(() => {
    let active = true;

    api.get<AboutResponse>(ABOUT_PATH).then(
      (response: AboutResponse | undefined) => {
        if (active && typeof response?.version === 'string') {
          setVersion(response.version);
        }
      },
      (error: unknown) => (active ? showError(error) : undefined),
    );

    return () => {
      active = false;
    };
  }, [showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    okButtonRef.current?.focus();

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape' || (event.key === 'Enter' && !(event.target instanceof HTMLButtonElement))) {
      event.preventDefault();
      event.stopPropagation();
      onClose();
      return;
    }

    if (event.key === 'Tab') {
      event.preventDefault();
      okButtonRef.current?.focus();
    }
  };

  const versionCaption = version === null ? msg('BaseVersion') : `${msg('BaseVersion')} ${version}`;

  return (
    <div style={OVERLAY_STYLE}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        style={PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 id={titleId} style={TITLE_STYLE}>
          {DIALOG_CAPTION}
        </h2>
        <div style={CONTENT_STYLE}>
          <p style={VERSION_STYLE}>{versionCaption}</p>
          <div style={NOTICE_STYLE}>
            <p style={COPYRIGHT_STYLE}>{COPYRIGHT_CAPTION}</p>
            <p style={LEGAL_STYLE}>{LEGAL_CAPTION}</p>
          </div>
        </div>
        <div style={BUTTON_ROW_STYLE}>
          <DialogButton ref={okButtonRef} onClick={() => onClose()}>
            {OK_CAPTION}
          </DialogButton>
        </div>
      </div>
      {messageBox}
    </div>
  );
}
