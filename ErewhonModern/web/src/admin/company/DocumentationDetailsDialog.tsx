import { useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, KeyboardEvent, ReactElement } from 'react';
import { ApiError, api } from '../../api/client';
import type { DocumentationDetailsResponse } from '../../api/types';
import { useMessageBox } from '../../components/MessageBox';

interface DocumentationDetailsDialogProps {
  /** Documented class as the documentation candidate carries it: `Agent` or `Region`. */
  entityType: string;
  /** Row id of the agent or region. */
  id: number;
  /** Called when the Close button is pressed or Escape is pressed in the dialog. */
  onClose: () => void;
}

interface LoadedDetails {
  /** Request path the outcome belongs to. */
  path: string;
  /** Documentation returned for the path; null when the request failed. */
  details: DocumentationDetailsResponse | null;
}

const DIALOG_CAPTION = 'Display Documentor Details';
const TEXT_CAPTION = 'Document as Text';
const RTF_CAPTION = 'Document as RTF';
const CLOSE_CAPTION = 'Close';
const RTF_EXTENSION = '.rtf';
const FOCUSABLE_SELECTOR = 'button:not(:disabled), [tabindex]:not([tabindex="-1"])';
const BORDER = '1px solid rgb(128, 128, 128)';

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
  maxInlineSize: '40.625rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
};

const SECTIONS_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '1rem',
};

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.25rem',
  flex: '1 1 0',
  minInlineSize: '14rem',
};

const SECTION_HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '0.875rem',
  fontWeight: 'normal',
};

const DETAILS_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  blockSize: '15.8125rem',
  margin: 0,
  padding: '0.25rem',
  overflow: 'auto',
  background: 'white',
  border: BORDER,
  fontFamily: 'monospace',
  fontSize: '0.8125rem',
  whiteSpace: 'pre-wrap',
  overflowWrap: 'anywhere',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.5rem',
};

const BUTTON_STYLE: CSSProperties = {
  minInlineSize: '5rem',
  paddingBlock: '0.25rem',
  paddingInline: '0.75rem',
  overflowWrap: 'anywhere',
};

/** Returns the path of GET /api/app/admin/documentation/{type}/{id}. */
function documentationPath(entityType: string, id: number): string {
  return `/api/app/admin/documentation/${encodeURIComponent(entityType)}/${id}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/**
 * Saves the blob as a file named `fileName` by clicking a temporary hidden link to an object URL, then revokes the
 * URL and removes the link. Does nothing where the environment cannot create object URLs.
 */
function saveBlob(blob: Blob, fileName: string): void {
  if (typeof URL.createObjectURL !== 'function') {
    return;
  }

  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.style.display = 'none';
  document.body.appendChild(anchor);

  try {
    anchor.click();
  } finally {
    if (typeof URL.revokeObjectURL === 'function') {
      URL.revokeObjectURL(url);
    }
    anchor.remove();
  }
}

/**
 * The "Display Documentor Details" dialog: the documentation of one agent or region, shown as text under
 * "Document as Text" and as unrendered Rich Text Format source under "Document as RTF".
 *
 * On mount, and whenever `entityType` or `id` changes, both blocks are emptied and the documentation is read from
 * GET /api/app/admin/documentation/{type}/{id}. A 404 leaves both blocks empty; an `ErrorOutcomeError` is shown in a
 * message box, an `UnauthorizedError` shows nothing, and any other error rejects unhandled. Once the
 * documentation is loaded, a button captioned `<name>.rtf` downloads GET .../{type}/{id}/rtf as `<name>.rtf`, with
 * the same error handling. The Close button and Escape call `onClose`.
 *
 * The Close button is focused on mount, Tab and Shift+Tab wrap within the dialog, and the element focused before the
 * dialog opened gets the focus back when the dialog closes and nothing else has taken it.
 */
export function DocumentationDetailsDialog(props: DocumentationDetailsDialogProps): ReactElement {
  const { entityType, id, onClose } = props;
  const path = documentationPath(entityType, id);
  const { element: messageBox, showError } = useMessageBox();
  const [loaded, setLoaded] = useState<LoadedDetails | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const textHeadingId = useId();
  const rtfHeadingId = useId();

  useEffect(() => {
    let active = true;

    api.get<DocumentationDetailsResponse>(path).then(
      (response) => {
        if (active) {
          setLoaded({ path, details: response });
        }
      },
      (error: unknown) => {
        if (!active) {
          return undefined;
        }
        setLoaded({ path, details: null });
        return isNotFound(error) ? undefined : showError(error);
      },
    );

    return () => {
      active = false;
    };
  }, [path, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    closeButtonRef.current?.focus();

    return () => {
      const current = document.activeElement;
      const focusLost =
        current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  const isCurrent = loaded !== null && loaded.path === path;
  const details = isCurrent ? loaded.details : null;
  const fileName = details === null ? '' : `${details.name}${RTF_EXTENSION}`;

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onClose();
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

  const handleDownload = async (downloadName: string): Promise<void> => {
    try {
      const blob = await api.blob(`${path}/rtf`);
      saveBlob(blob, downloadName);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        await showError(error);
      }
    }
  };

  return (
    <div style={OVERLAY_STYLE}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={DIALOG_CAPTION}
        aria-busy={!isCurrent}
        tabIndex={-1}
        style={PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 style={TITLE_STYLE}>{DIALOG_CAPTION}</h2>
        <div style={SECTIONS_STYLE}>
          <section aria-labelledby={textHeadingId} style={SECTION_STYLE}>
            <h3 id={textHeadingId} style={SECTION_HEADING_STYLE}>
              {TEXT_CAPTION}
            </h3>
            <pre tabIndex={0} style={DETAILS_STYLE}>
              {details === null ? '' : details.text}
            </pre>
          </section>
          <section aria-labelledby={rtfHeadingId} style={SECTION_STYLE}>
            <h3 id={rtfHeadingId} style={SECTION_HEADING_STYLE}>
              {RTF_CAPTION}
            </h3>
            <pre tabIndex={0} style={DETAILS_STYLE}>
              {details === null ? '' : details.rtf}
            </pre>
          </section>
        </div>
        <div style={BUTTON_ROW_STYLE}>
          {details !== null && (
            <button type="button" style={BUTTON_STYLE} onClick={() => void handleDownload(fileName)}>
              {fileName}
            </button>
          )}
          <button ref={closeButtonRef} type="button" style={BUTTON_STYLE} onClick={() => onClose()}>
            {CLOSE_CAPTION}
          </button>
        </div>
      </div>
      {messageBox}
    </div>
  );
}
