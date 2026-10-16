import { useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { api } from '../../api/client';
import type { DocumentationCandidate } from '../../api/types';
import { msg } from '../../messages/messages';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { DocumentationDetailsDialog } from './DocumentationDetailsDialog';

/** Properties of {@link DocumentationCandidatesPage}. */
export interface DocumentationCandidatesPageProps {
  /** Accepted from the admin shell's tab host; no control of the page calls it. */
  onClose?: () => void;
}

/** Agent or region the details dialog documents. */
interface DocumentedEntry {
  /** `Agent` or `Region`. */
  entityType: string;
  /** Row id of the agent or region. */
  id: number;
}

/** One rendered entry of the list. */
interface VisibleEntry {
  /** Position of the entry in the candidate list as the server returned it. */
  index: number;
  /** The candidate shown by the entry. */
  candidate: DocumentationCandidate;
  /** True when the candidate directly after this one has a higher level. */
  hasChildren: boolean;
  /** False once the entry has been collapsed. */
  expanded: boolean;
}

/** Picture drawn at the start of an entry: the expanded and collapsed pictures of an entry with children, or the leaf. */
type EntryPicture = 'open' | 'closed' | 'leaf';

const CANDIDATES_PATH = '/api/app/admin/documentation/candidates';
const PAGE_CAPTION = 'Documentation Candidates';
const LIST_NAME = 'Documentation Candidates';
const SELECT_ITEMS_CAPTION = 'Select items to document...';
const SHOW_DETAILS_CAPTION = 'Show Details';

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';
const ENTRY_INDENT_REM = 1.25;
const ENTRY_INLINE_START_REM = 0.25;

const PICTURE_BORDER = 'rgb(162, 151, 148)';
const PICTURE_FILL = 'rgb(164, 237, 250)';
const PICTURE_MARK = 'black';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  maxInlineSize: '20.1875rem',
  padding: '0.625rem',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const SELECT_ITEMS_STYLE: CSSProperties = {
  margin: 0,
  overflowWrap: 'break-word',
};

const LIST_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  blockSize: '14.5625rem',
  margin: 0,
  padding: 0,
  overflowY: 'auto',
  listStyle: 'none',
  background: 'white',
  color: 'black',
  border: LIST_BORDER,
};

const ENTRY_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.25rem',
  boxSizing: 'border-box',
  paddingBlock: '0.0625rem',
  paddingInlineEnd: '0.25rem',
  cursor: 'default',
  overflowWrap: 'anywhere',
  userSelect: 'none',
};

const PICTURE_STYLE: CSSProperties = {
  flex: '0 0 auto',
  display: 'block',
  inlineSize: '0.8125rem',
  blockSize: '0.8125rem',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.5rem',
};

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns the agent or region `candidate` documents, or null for a heading. */
function documentedEntry(candidate: DocumentationCandidate): DocumentedEntry | null {
  if (candidate.entityType === null || candidate.id === null) {
    return null;
  }
  return { entityType: candidate.entityType, id: candidate.id };
}

/**
 * Returns the entries to render, in candidate order: every candidate except those that follow a collapsed candidate
 * at a higher level, up to the next candidate at the collapsed candidate's level or lower.
 */
function visibleEntries(
  candidates: readonly DocumentationCandidate[],
  collapsed: ReadonlySet<number>,
): VisibleEntry[] {
  const entries: VisibleEntry[] = [];
  let hiddenAboveLevel: number | null = null;

  for (let index = 0; index < candidates.length; index += 1) {
    const candidate = candidates[index];
    if (candidate === undefined) {
      continue;
    }
    if (hiddenAboveLevel !== null && candidate.level > hiddenAboveLevel) {
      continue;
    }

    hiddenAboveLevel = null;
    const next = candidates[index + 1];
    const expanded = !collapsed.has(index);
    entries.push({
      index,
      candidate,
      hasChildren: next !== undefined && next.level > candidate.level,
      expanded,
    });
    if (!expanded) {
      hiddenAboveLevel = candidate.level;
    }
  }

  return entries;
}

/** Returns the picture of `entry`: open or closed by its expanded state when it has children, else the leaf. */
function entryPicture(entry: VisibleEntry): EntryPicture {
  if (!entry.hasChildren) {
    return 'leaf';
  }
  return entry.expanded ? 'open' : 'closed';
}

/** Returns the selected, pressed or hovered background of an entry, in that order, or `undefined`. */
function entryBackground(selected: boolean, pressed: boolean, hovered: boolean): string | undefined {
  if (selected) {
    return SELECTED_BACKGROUND;
  }
  if (pressed) {
    return PRESSED_BACKGROUND;
  }
  if (hovered) {
    return HOVER_BACKGROUND;
  }
  return undefined;
}

/** Returns the style of an entry at `level`, indented by level, with its background and focus outline. */
function entryStyle(level: number, background: string | undefined, focusVisible: boolean): CSSProperties {
  const indent = ENTRY_INLINE_START_REM + (Math.max(level, 1) - 1) * ENTRY_INDENT_REM;
  const style: CSSProperties = { ...ENTRY_STYLE, paddingInlineStart: `${indent}rem` };
  if (background !== undefined) {
    style.background = background;
  }
  if (focusVisible) {
    style.outline = FOCUS_OUTLINE;
    style.outlineOffset = FOCUS_OUTLINE_OFFSET;
  }
  return style;
}

/**
 * The 13 by 13 picture of an entry: a light-blue box with a grey border holding a minus sign when `picture` is
 * `'open'`, a plus sign when it is `'closed'` and nothing for `'leaf'`. Hidden from assistive technologies.
 */
function EntryPictureIcon(props: { picture: EntryPicture }): ReactElement {
  const { picture } = props;
  return (
    <svg viewBox="0 0 13 13" aria-hidden="true" focusable="false" style={PICTURE_STYLE}>
      <rect x="0.5" y="0.5" width="12" height="12" rx="2" fill={PICTURE_FILL} stroke={PICTURE_BORDER} />
      {picture !== 'leaf' && <path d="M3.5 6.5h6" stroke={PICTURE_MARK} />}
      {picture === 'closed' && <path d="M6.5 3.5v6" stroke={PICTURE_MARK} />}
    </svg>
  );
}

/**
 * The "Documentation Candidates" page: the agents and regions of the bound company that can be documented, under
 * the headings "-- Agents --" and "-- Regions --", and the "Show Details" button that documents the selected one.
 *
 * The section is named by the `DocumentorSetup` message. The candidates are read once on mount from
 * GET /api/app/admin/documentation/candidates and listed in the order received, each entry showing its text unchanged
 * and indented 1.25rem per level above 1; an answer that arrives after unmount is ignored. The list has
 * `aria-busy="true"` until that request answers. A failure leaves the list empty and is passed to `showError`, so an
 * `ErrorOutcomeError` is shown in a message box, an `UnauthorizedError` shows nothing, and any other error rejects
 * unhandled.
 *
 * At most one entry is selected. Clicking an agent or region entry, pressing Space or Enter on it, or reaching it with
 * ArrowDown or ArrowUp selects it; a heading has `aria-disabled="true"` and is never selected. "Show Details" opens
 * {@link DocumentationDetailsDialog} for the selected entry and does nothing while no entry is selected; the dialog
 * closes, and unmounts, through its own `onClose`.
 *
 * Every entry starts expanded. Double-clicking an entry, pressing Enter on it or clicking its picture toggles it
 * between expanded and collapsed; the entries after a collapsed entry with a higher level are not rendered, up to the
 * next entry at its level or lower, and a selection among them is kept. The picture is a box holding a minus sign
 * for an expanded entry with entries below it, a plus sign for a collapsed one, and nothing for any other entry.
 *
 * One rendered entry is the tab stop: the entry focused last while it is rendered, otherwise the selected entry while
 * it is rendered, otherwise the first entry. ArrowDown and ArrowUp, pressed without Ctrl, Alt or Meta, focus the next
 * or previous rendered entry and do nothing on the last or first one. The selected entry has a rgb(204, 228, 247)
 * background; an agent or region entry that is not selected has a rgb(230, 230, 230) background while the primary
 * mouse button is held on it and a rgb(242, 242, 242) background while the pointer is over it; an entry focused with
 * `:focus-visible`, checked again on every key release, has a 2px rgb(0, 80, 160) outline inset by 2px.
 */
export function DocumentationCandidatesPage(_props: DocumentationCandidatesPageProps): ReactElement {
  const { element: messageBox, showError } = useMessageBox();
  const [candidates, setCandidates] = useState<readonly DocumentationCandidate[] | null>(null);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [collapsed, setCollapsed] = useState<ReadonlySet<number>>(() => new Set<number>());
  const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);
  const [pressedIndex, setPressedIndex] = useState<number | null>(null);
  const [focusVisibleIndex, setFocusVisibleIndex] = useState<number | null>(null);
  const [documented, setDocumented] = useState<DocumentedEntry | null>(null);
  const entryElements = useRef(new Map<number, HTMLLIElement>());
  const selectItemsId = useId();

  useEffect(() => {
    let active = true;

    api.get<DocumentationCandidate[] | undefined>(CANDIDATES_PATH).then(
      (response) => {
        if (active) {
          setCandidates(Array.isArray(response) ? response : []);
        }
      },
      (error: unknown) => {
        if (!active) {
          return undefined;
        }
        setCandidates([]);
        return showError(error);
      },
    );

    return () => {
      active = false;
    };
  }, [showError]);

  const entries = candidates === null ? [] : visibleEntries(candidates, collapsed);
  const isRendered = (index: number | null): index is number =>
    index !== null && entries.some((entry) => entry.index === index);

  let tabStopIndex: number | null = entries[0]?.index ?? null;
  if (isRendered(focusedIndex)) {
    tabStopIndex = focusedIndex;
  } else if (isRendered(selectedIndex)) {
    tabStopIndex = selectedIndex;
  }

  const select = (entry: VisibleEntry): void => {
    if (documentedEntry(entry.candidate) !== null) {
      setSelectedIndex(entry.index);
    }
  };

  const toggle = (index: number): void => {
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }
      return next;
    });
  };

  const focusEntry = (index: number): void => {
    setFocusedIndex(index);
    entryElements.current.get(index)?.focus();
  };

  const showDetails = (): void => {
    if (candidates === null || selectedIndex === null) {
      return;
    }
    const candidate = candidates[selectedIndex];
    const entry = candidate === undefined ? null : documentedEntry(candidate);
    if (entry !== null) {
      setDocumented(entry);
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLLIElement>, position: number): void => {
    const entry = entries[position];
    if (entry === undefined) {
      return;
    }

    switch (event.key) {
      case ' ':
        event.preventDefault();
        select(entry);
        return;
      case 'Enter':
        event.preventDefault();
        select(entry);
        toggle(entry.index);
        return;
      case 'ArrowDown':
      case 'ArrowUp': {
        if (event.ctrlKey || event.altKey || event.metaKey) {
          return;
        }
        event.preventDefault();
        const target = entries[position + (event.key === 'ArrowDown' ? 1 : -1)];
        if (target !== undefined) {
          focusEntry(target.index);
          select(target);
        }
        return;
      }
      default:
        return;
    }
  };

  const updateFocusVisible = (
    event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>,
    index: number,
  ): void => {
    setFocusVisibleIndex(matchesFocusVisible(event.currentTarget) ? index : null);
  };

  const handlePictureClick = (event: MouseEvent<HTMLSpanElement>, index: number): void => {
    if (event.detail <= 1) {
      toggle(index);
    }
  };

  return (
    <section aria-label={msg('DocumentorSetup')} style={SECTION_STYLE}>
      <h2 style={HEADING_STYLE}>{PAGE_CAPTION}</h2>
      <p id={selectItemsId} style={SELECT_ITEMS_STYLE}>
        {SELECT_ITEMS_CAPTION}
      </p>
      <ul
        role="listbox"
        aria-label={LIST_NAME}
        aria-describedby={selectItemsId}
        aria-busy={candidates === null}
        style={LIST_STYLE}
      >
        {entries.map((entry, position) => {
          const { index, candidate } = entry;
          const selectable = documentedEntry(candidate) !== null;
          const selected = index === selectedIndex;
          const background = entryBackground(
            selected,
            selectable && index === pressedIndex,
            selectable && index === hoveredIndex,
          );

          return (
            <li
              key={index}
              ref={(element) => {
                if (element === null) {
                  entryElements.current.delete(index);
                } else {
                  entryElements.current.set(index, element);
                }
              }}
              role="option"
              aria-selected={selected}
              aria-disabled={selectable ? undefined : true}
              tabIndex={index === tabStopIndex ? 0 : -1}
              style={entryStyle(candidate.level, background, index === focusVisibleIndex)}
              onClick={() => select(entry)}
              onDoubleClick={() => toggle(index)}
              onKeyDown={(event) => handleKeyDown(event, position)}
              onKeyUp={(event) => updateFocusVisible(event, index)}
              onFocus={(event) => {
                setFocusedIndex(index);
                updateFocusVisible(event, index);
              }}
              onBlur={() => setFocusVisibleIndex(null)}
              onMouseEnter={() => setHoveredIndex(index)}
              onMouseLeave={() => {
                setHoveredIndex(null);
                setPressedIndex(null);
              }}
              onMouseDown={(event) => {
                if (event.button === 0) {
                  setPressedIndex(index);
                }
              }}
              onMouseUp={() => setPressedIndex(null)}
            >
              <span
                onClick={(event) => handlePictureClick(event, index)}
                onDoubleClick={(event) => event.stopPropagation()}
              >
                <EntryPictureIcon picture={entryPicture(entry)} />
              </span>
              <span>{candidate.text}</span>
            </li>
          );
        })}
      </ul>
      <div style={BUTTON_ROW_STYLE}>
        <DialogButton onClick={showDetails}>{SHOW_DETAILS_CAPTION}</DialogButton>
      </div>
      {documented !== null && (
        <DocumentationDetailsDialog
          entityType={documented.entityType}
          id={documented.id}
          onClose={() => setDocumented(null)}
        />
      )}
      {messageBox}
    </section>
  );
}

