// Shell of the admin area: the caption, the menu bar of the Administration application, the strip of open list tabs
// with their pages, and the one dialog the menus open.

import { Fragment, useEffect, useId, useLayoutEffect, useReducer, useRef, useState } from 'react';
import type {
  CSSProperties,
  FocusEvent as ReactFocusEvent,
  KeyboardEvent as ReactKeyboardEvent,
  MouseEvent as ReactMouseEvent,
  ReactElement,
} from 'react';
import { useNavigate } from 'react-router-dom';
import { useMessageBox } from '../components/MessageBox';
import type { MessageBoxOptions } from '../components/MessageBox';
import { msg } from '../messages/messages';
import type { MessageKey } from '../messages/messages';
import { useSession } from '../session/SessionProvider';
import { AboutDialog } from './AboutDialog';
import { AgentCommissionRatesPage } from './agents/AgentCommissionRatesPage';
import { AgentsListPage } from './agents/AgentsListPage';
import { EditAgentDialog } from './agents/EditAgentDialog';
import { ClientsListPage } from './clients/ClientsListPage';
import { EditClientDialog } from './clients/EditClientDialog';
import { CommissionRatesListPage } from './company/CommissionRatesListPage';
import { CountriesListPage } from './company/CountriesListPage';
import { DocumentationCandidatesPage } from './company/DocumentationCandidatesPage';
import { EditCompanyDialog } from './company/EditCompanyDialog';
import { ItemCategoryListPage } from './company/ItemCategoryListPage';
import { RegionsListPage } from './company/RegionsListPage';
import { EditRetailItemDialog } from './items/EditRetailItemDialog';
import { EditTenderItemDialog } from './items/EditTenderItemDialog';
import { ItemsByCategoryPage } from './items/ItemsByCategoryPage';
import { ItemsListPage } from './items/ItemsListPage';
import { SalesListPage } from './sales/SalesListPage';
import { CloseTendersDialog } from './tenders/CloseTendersDialog';
import { TendersListPage } from './tenders/TendersListPage';

/** List page that a menu entry opens in a new tab. */
type PageKind =
  | 'agents'
  | 'agentCommissionRates'
  | 'clients'
  | 'items'
  | 'itemsByCategory'
  | 'tenders'
  | 'sales'
  | 'commissionRates'
  | 'itemCategories'
  | 'countries'
  | 'regions'
  | 'documentation';

/** Dialog that a menu entry opens in the shell's dialog slot. */
type DialogKind =
  | 'addAgent'
  | 'myDetails'
  | 'addClient'
  | 'addRetailItem'
  | 'addTenderItem'
  | 'companyDetails'
  | 'closeTenders'
  | 'about';

/** What choosing a menu entry does. */
type MenuAction =
  | { readonly type: 'tab'; readonly page: PageKind }
  | { readonly type: 'dialog'; readonly dialog: DialogKind }
  | { readonly type: 'logout' }
  | { readonly type: 'exit' };

/** Sessions a menu or menu entry is rendered for: every session, agent sessions only, or non-agent sessions only. */
type Audience = 'all' | 'agent' | 'company';

/** Menu entry that runs an action. */
interface MenuCommand {
  readonly kind: 'command';
  readonly key: string;
  readonly caption: string;
  readonly action: MenuAction;
  readonly audience: Audience;
}

/** Separator line between menu entries. */
interface MenuSeparator {
  readonly kind: 'separator';
  readonly key: string;
  readonly audience: Audience;
}

type MenuEntry = MenuCommand | MenuSeparator;

/** Top-level menu of the menu bar with its entries in display order. */
interface MenuDefinition {
  readonly key: string;
  readonly caption: string;
  readonly audience: Audience;
  readonly entries: readonly MenuEntry[];
}

/** Entry of a menu that is opened and should receive the focus once rendered. */
type MenuFocus = 'first' | 'last';

/** One open tab: its key, the page it shows and its caption. */
interface OpenTab {
  readonly key: string;
  readonly page: PageKind;
  readonly caption: string;
}

/** Open tabs in strip order and the key of the selected one; null while no tab is open. */
interface TabsState {
  readonly tabs: readonly OpenTab[];
  readonly activeKey: string | null;
}

type TabsAction =
  | { readonly type: 'open'; readonly tab: OpenTab }
  | { readonly type: 'select'; readonly key: string }
  | { readonly type: 'close'; readonly key: string };

/** Dialog in the dialog slot: its kind, the agent of My Details, and a serial number unique to each opening. */
interface OpenDialog {
  readonly kind: DialogKind;
  readonly agentId: number | null;
  readonly serial: number;
}

/** Outcome of a rejected logout. */
interface LogoutFailure {
  readonly error: unknown;
}

const ADMIN_LOGON_PATH = '/admin';
const EXIT_PATH = '/';

const MENU_BAR_LABEL = 'Administration menu';
const CLOSE_TAB_CAPTION = 'Close';
const CLOSE_GLYPH = '\u2715';

function tabAction(page: PageKind): MenuAction {
  return { type: 'tab', page };
}

function dialogAction(dialog: DialogKind): MenuAction {
  return { type: 'dialog', dialog };
}

function command(key: string, caption: string, action: MenuAction, audience: Audience = 'all'): MenuCommand {
  return { kind: 'command', key, caption, action, audience };
}

function separator(key: string, audience: Audience = 'all'): MenuSeparator {
  return { kind: 'separator', key, audience };
}

/** The menu bar of the Administration application with the audience of every menu and entry. */
const MENUS: readonly MenuDefinition[] = [
  {
    key: 'file',
    caption: 'File',
    audience: 'all',
    entries: [command('fileLogout', 'Logout', { type: 'logout' }), command('fileExit', 'Exit', { type: 'exit' })],
  },
  {
    key: 'agents',
    caption: 'Agents',
    audience: 'all',
    entries: [
      command('agentsDetails', 'My Details', dialogAction('myDetails'), 'agent'),
      command('agentsView', 'View Agents', tabAction('agents'), 'company'),
      command('agentsAdd', 'Add Agent', dialogAction('addAgent'), 'company'),
      separator('agentsSeparator', 'company'),
      command('agentsCommissionRates', 'Agent Commission Rates', tabAction('agentCommissionRates'), 'company'),
    ],
  },
  {
    key: 'clients',
    caption: 'Clients',
    audience: 'company',
    entries: [
      command('clientsView', 'View Clients', tabAction('clients')),
      command('clientsAdd', 'Add Client', dialogAction('addClient')),
    ],
  },
  {
    key: 'items',
    caption: 'Items',
    audience: 'all',
    entries: [
      command('itemsView', 'View Items', tabAction('items')),
      command('itemsViewByCategory', 'View Items (By Category)', tabAction('itemsByCategory')),
      separator('itemsSeparator'),
      command('itemsAddRetailItem', 'Add Retail Item', dialogAction('addRetailItem')),
      command('itemsAddTenderItem', 'Add Tender Item', dialogAction('addTenderItem')),
    ],
  },
  {
    key: 'tenders',
    caption: 'Tenders',
    audience: 'all',
    entries: [
      command('tendersView', 'View Tenders', tabAction('tenders')),
      command('tendersClose', 'Close Tenders', dialogAction('closeTenders'), 'company'),
    ],
  },
  {
    key: 'sales',
    caption: 'Sales',
    audience: 'all',
    entries: [command('salesView', 'View Sales', tabAction('sales'))],
  },
  {
    key: 'company',
    caption: 'Company',
    audience: 'all',
    entries: [
      command('companyDetails', 'Company Details', dialogAction('companyDetails'), 'company'),
      separator('companySeparator1', 'company'),
      command('companyCommissionRates', 'Commission Rates', tabAction('commissionRates')),
      command('companyItemCategories', 'Item Categories', tabAction('itemCategories'), 'company'),
      command('companyCountries', 'Countries', tabAction('countries'), 'company'),
      command('companyRegions', 'Regions', tabAction('regions'), 'company'),
      separator('companySeparator2'),
      command('companyDetailsViaInterface', 'Show Details via Interface', tabAction('documentation')),
    ],
  },
  {
    key: 'help',
    caption: 'Help',
    audience: 'all',
    entries: [command('helpAbout', 'About', dialogAction('about'))],
  },
];

/** Message key of the tab caption of each list page. */
const PAGE_CAPTIONS: Readonly<Record<PageKind, MessageKey>> = {
  agents: 'Agents',
  agentCommissionRates: 'AgentCommissionRates',
  clients: 'Clients',
  items: 'ViewItems',
  itemsByCategory: 'ItemsByCategory',
  tenders: 'TendersListFormCaption',
  sales: 'Sales',
  commissionRates: 'CommissionRates',
  itemCategories: 'ItemCategories',
  countries: 'Countries',
  regions: 'Regions',
  documentation: 'DocumentorSetup',
};

const INITIAL_TABS: TabsState = { tabs: [], activeKey: null };

const FONT_FAMILY = 'Tahoma, "MS Sans Serif", sans-serif';
const FACE_COLOR = 'rgb(240, 240, 240)';
const HOVER_COLOR = 'rgb(229, 241, 251)';
const PRESSED_COLOR = 'rgb(204, 228, 247)';
const ACCENT_COLOR = 'rgb(0, 80, 160)';
const BORDER = '1px solid rgb(128, 128, 128)';
const TAB_BORDER = '1px solid rgb(174, 174, 174)';
const ACCENT_BORDER = `1px solid ${ACCENT_COLOR}`;
const RULE = '1px solid rgb(214, 213, 212)';

const INHERITED_FONT_STYLE: CSSProperties = {
  fontFamily: 'inherit',
  fontSize: 'inherit',
  fontStyle: 'inherit',
  fontWeight: 'inherit',
};

const INSET_FOCUS_STYLE: CSSProperties = {
  outline: `2px solid ${ACCENT_COLOR}`,
  outlineOffset: '-2px',
};

const SHELL_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  boxSizing: 'border-box',
  minInlineSize: 0,
  backgroundColor: FACE_COLOR,
  color: 'black',
  fontFamily: FONT_FAMILY,
};

const HEADER_STYLE: CSSProperties = {
  paddingBlock: '0.375rem',
  paddingInline: '0.625rem',
  backgroundColor: 'white',
  borderBlockEnd: RULE,
};

const CAPTION_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const MENU_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '0.125rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.25rem',
  backgroundColor: FACE_COLOR,
  borderBlockEnd: RULE,
};

const MENU_SLOT_STYLE: CSSProperties = {
  position: 'relative',
  display: 'flex',
};

const BAR_ITEM_STYLE: CSSProperties = {
  margin: 0,
  paddingBlock: '0.25rem',
  paddingInline: '0.625rem',
  border: 'none',
  borderRadius: 0,
  backgroundColor: 'transparent',
  color: 'black',
  ...INHERITED_FONT_STYLE,
  whiteSpace: 'nowrap',
  cursor: 'default',
};

const BAR_ITEM_HOVER_STYLE: CSSProperties = {
  ...BAR_ITEM_STYLE,
  backgroundColor: HOVER_COLOR,
};

const BAR_ITEM_OPEN_STYLE: CSSProperties = {
  ...BAR_ITEM_STYLE,
  backgroundColor: PRESSED_COLOR,
};

const MENU_STYLE: CSSProperties = {
  position: 'absolute',
  insetBlockStart: '100%',
  insetInlineStart: 0,
  zIndex: 800,
  display: 'flex',
  flexDirection: 'column',
  boxSizing: 'border-box',
  minInlineSize: '12rem',
  maxInlineSize: 'calc(100vw - 1rem)',
  paddingBlock: '0.1875rem',
  backgroundColor: 'white',
  border: BORDER,
  boxShadow: '2px 2px 6px rgba(0, 0, 0, 0.3)',
};

const MENU_ENTRY_STYLE: CSSProperties = {
  display: 'block',
  boxSizing: 'border-box',
  inlineSize: '100%',
  margin: 0,
  paddingBlock: '0.25rem',
  paddingInline: '1.5rem',
  border: 'none',
  borderRadius: 0,
  backgroundColor: 'transparent',
  color: 'black',
  ...INHERITED_FONT_STYLE,
  textAlign: 'start',
  whiteSpace: 'nowrap',
  cursor: 'default',
};

const MENU_ENTRY_HOVER_STYLE: CSSProperties = {
  ...MENU_ENTRY_STYLE,
  backgroundColor: HOVER_COLOR,
};

const MENU_ENTRY_PRESSED_STYLE: CSSProperties = {
  ...MENU_ENTRY_STYLE,
  backgroundColor: PRESSED_COLOR,
};

const MENU_SEPARATOR_STYLE: CSSProperties = {
  marginBlock: '0.1875rem',
  marginInline: '0.125rem',
  borderBlockStart: RULE,
};

const TAB_AREA_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-end',
  boxSizing: 'border-box',
  minBlockSize: '2.625rem',
  paddingBlockStart: '0.1875rem',
  paddingInline: '0.3125rem',
  overflowX: 'auto',
  backgroundColor: 'white',
  borderBlockEnd: BORDER,
};

const TAB_LIST_STYLE: CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  alignItems: 'stretch',
  gap: '0.3125rem',
};

const TAB_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  flex: '0 0 auto',
  boxSizing: 'border-box',
  inlineSize: '14.5rem',
  minBlockSize: '2.25rem',
  borderBlockStart: TAB_BORDER,
  borderInlineStart: TAB_BORDER,
  borderInlineEnd: TAB_BORDER,
  borderStartStartRadius: '3px',
  borderStartEndRadius: '3px',
  backgroundColor: FACE_COLOR,
};

const TAB_HOVER_STYLE: CSSProperties = {
  ...TAB_STYLE,
  backgroundColor: HOVER_COLOR,
};

const TAB_ACTIVE_STYLE: CSSProperties = {
  ...TAB_STYLE,
  borderBlockStart: BORDER,
  borderInlineStart: BORDER,
  borderInlineEnd: BORDER,
  backgroundColor: 'white',
};

const TAB_ACTIVE_HOVER_STYLE: CSSProperties = {
  ...TAB_ACTIVE_STYLE,
  borderBlockStart: ACCENT_BORDER,
  borderInlineStart: ACCENT_BORDER,
  borderInlineEnd: ACCENT_BORDER,
};

const TAB_BUTTON_STYLE: CSSProperties = {
  flex: '1 1 auto',
  alignSelf: 'stretch',
  minInlineSize: 0,
  margin: 0,
  paddingBlock: '0.25rem',
  paddingInlineStart: '0.5rem',
  paddingInlineEnd: '0.25rem',
  border: 'none',
  borderRadius: 0,
  backgroundColor: 'transparent',
  color: 'black',
  ...INHERITED_FONT_STYLE,
  textAlign: 'start',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
  cursor: 'default',
};

const TAB_BUTTON_ACTIVE_STYLE: CSSProperties = {
  ...TAB_BUTTON_STYLE,
  fontWeight: 'bold',
};

/* BLITZY [A11Y]: the tab close button keeps the 18 x 17px size of the JADE tab terminate button, below the 44 x 44px
   touch-target minimum; flagged for designer review. */
const CLOSE_BUTTON_STYLE: CSSProperties = {
  flex: '0 0 auto',
  boxSizing: 'border-box',
  inlineSize: '1.125rem',
  blockSize: '1.0625rem',
  margin: 0,
  padding: 0,
  border: 'none',
  borderRadius: 0,
  backgroundColor: 'transparent',
  color: 'black',
  ...INHERITED_FONT_STYLE,
  fontSize: '0.75rem',
  lineHeight: 1,
  cursor: 'default',
};

const CLOSE_BUTTON_HOVER_STYLE: CSSProperties = {
  ...CLOSE_BUTTON_STYLE,
  backgroundColor: PRESSED_COLOR,
};

const CLOSE_BUTTON_PRESSED_STYLE: CSSProperties = {
  ...CLOSE_BUTTON_HOVER_STYLE,
  boxShadow: `inset 0 0 0 1px ${ACCENT_COLOR}`,
};

const MAIN_STYLE: CSSProperties = {
  flex: '1 1 auto',
  minInlineSize: 0,
  backgroundColor: FACE_COLOR,
};

/** Returns the style of a top-level menu item: open or pressed, hovered, or at rest, with the focus outline added. */
function barItemStyle(open: boolean, hovered: boolean, pressed: boolean, focusVisible: boolean): CSSProperties {
  let style = BAR_ITEM_STYLE;
  if (open || pressed) {
    style = BAR_ITEM_OPEN_STYLE;
  } else if (hovered) {
    style = BAR_ITEM_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...INSET_FOCUS_STYLE } : style;
}

/** Returns the style of a menu entry: pressed, hovered, or at rest, with the focus outline added. */
function menuEntryStyle(hovered: boolean, pressed: boolean, focusVisible: boolean): CSSProperties {
  let style = MENU_ENTRY_STYLE;
  if (pressed) {
    style = MENU_ENTRY_PRESSED_STYLE;
  } else if (hovered) {
    style = MENU_ENTRY_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...INSET_FOCUS_STYLE } : style;
}

/** Returns the style of an open menu, moved towards the inline start by `shift` pixels. */
function menuStyle(shift: number): CSSProperties {
  return shift > 0 ? { ...MENU_STYLE, insetInlineStart: `${-shift}px` } : MENU_STYLE;
}

/** Returns the style of a tab: selected or not, each with its hovered variant. */
function tabStyle(active: boolean, hovered: boolean): CSSProperties {
  if (active) {
    return hovered ? TAB_ACTIVE_HOVER_STYLE : TAB_ACTIVE_STYLE;
  }
  return hovered ? TAB_HOVER_STYLE : TAB_STYLE;
}

/** Returns the style of a tab's caption button, bold when selected, with the focus outline added. */
function tabButtonStyle(active: boolean, focusVisible: boolean): CSSProperties {
  const style = active ? TAB_BUTTON_ACTIVE_STYLE : TAB_BUTTON_STYLE;
  return focusVisible ? { ...style, ...INSET_FOCUS_STYLE } : style;
}

/** Returns the style of a tab's close button: pressed, hovered, or at rest, with the focus outline added. */
function closeButtonStyle(hovered: boolean, pressed: boolean, focusVisible: boolean): CSSProperties {
  let style = CLOSE_BUTTON_STYLE;
  if (pressed) {
    style = CLOSE_BUTTON_PRESSED_STYLE;
  } else if (hovered) {
    style = CLOSE_BUTTON_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...INSET_FOCUS_STYLE } : style;
}

/** Returns whether `element` matches `:focus-visible`; true where the selector is not supported. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns whether a menu or entry for `audience` is rendered in an agent session (`agentSession`) or another one. */
function isShownTo(audience: Audience, agentSession: boolean): boolean {
  return audience === 'all' || (audience === 'agent') === agentSession;
}

/** Returns the menus rendered for the session, each holding only the entries rendered for it. */
function visibleMenus(agentSession: boolean): MenuDefinition[] {
  return MENUS.filter((menu) => isShownTo(menu.audience, agentSession)).map((menu) => ({
    ...menu,
    entries: menu.entries.filter((entry) => isShownTo(entry.audience, agentSession)),
  }));
}

/**
 * Applies a tab action: `open` appends the tab and selects it; `select` selects a listed tab; `close` removes a
 * listed tab and, when it was selected, selects the tab that took its place, else the one before it, else none.
 */
function tabsReducer(state: TabsState, action: TabsAction): TabsState {
  switch (action.type) {
    case 'open':
      return { tabs: [...state.tabs, action.tab], activeKey: action.tab.key };
    case 'select':
      if (state.activeKey === action.key || !state.tabs.some((tab) => tab.key === action.key)) {
        return state;
      }
      return { ...state, activeKey: action.key };
    case 'close': {
      const index = state.tabs.findIndex((tab) => tab.key === action.key);
      if (index === -1) {
        return state;
      }
      const tabs = [...state.tabs.slice(0, index), ...state.tabs.slice(index + 1)];
      if (state.activeKey !== action.key) {
        return { tabs, activeKey: state.activeKey };
      }
      const neighbour: OpenTab | undefined = tabs[index] ?? tabs[index - 1];
      return { tabs, activeKey: neighbour?.key ?? null };
    }
  }
}

/** Returns the list page of `page`, whose Close button calls `onClose`. */
function renderPage(page: PageKind, onClose: () => void): ReactElement {
  switch (page) {
    case 'agents':
      return <AgentsListPage onClose={onClose} />;
    case 'agentCommissionRates':
      return <AgentCommissionRatesPage onClose={onClose} />;
    case 'clients':
      return <ClientsListPage onClose={onClose} />;
    case 'items':
      return <ItemsListPage onClose={onClose} />;
    case 'itemsByCategory':
      return <ItemsByCategoryPage onClose={onClose} />;
    case 'tenders':
      return <TendersListPage onClose={onClose} />;
    case 'sales':
      return <SalesListPage onClose={onClose} />;
    case 'commissionRates':
      return <CommissionRatesListPage hideAddEditRemove onClose={onClose} />;
    case 'itemCategories':
      return <ItemCategoryListPage onClose={onClose} />;
    case 'countries':
      return <CountriesListPage onClose={onClose} />;
    case 'regions':
      return <RegionsListPage onClose={onClose} />;
    case 'documentation':
      return <DocumentationCandidatesPage onClose={onClose} />;
  }
}

/** Returns the dialog of `open`, which calls `onClose` when it closes. */
function renderDialog(open: OpenDialog, onClose: () => void): ReactElement {
  switch (open.kind) {
    case 'addAgent':
      return <EditAgentDialog agentId={null} onClose={onClose} />;
    case 'myDetails':
      return <EditAgentDialog agentId={open.agentId} onClose={onClose} />;
    case 'addClient':
      return <EditClientDialog clientId={null} onClose={onClose} />;
    case 'addRetailItem':
      return <EditRetailItemDialog itemId={null} onClose={onClose} />;
    case 'addTenderItem':
      return <EditTenderItemDialog itemId={null} onClose={onClose} />;
    case 'companyDetails':
      return <EditCompanyDialog onClose={onClose} />;
    case 'closeTenders':
      return <CloseTendersDialog onClose={onClose} />;
    case 'about':
      return <AboutDialog onClose={onClose} />;
  }
}

/** Returns the Yes/No question asked before File › Logout and File › Exit leave the admin area. */
function exitConfirmationOptions(): MessageBoxOptions {
  return {
    title: msg('ConfirmExit'),
    message: msg('AreYouSureYouWantToExit'),
    icon: 'question',
    buttons: 'yesNo',
  };
}

function noLogoutFailure(): null {
  return null;
}

function toLogoutFailure(error: unknown): LogoutFailure {
  return { error };
}

/** Settles a rejected error display of a failed logout; the admin area is left either way. */
function ignoreLogoutFailure(): undefined {
  return undefined;
}

/** Returns the first or last of `items`, or undefined when there are none. */
function pickMenuItem(items: readonly HTMLButtonElement[], focus: MenuFocus): HTMLButtonElement | undefined {
  return focus === 'first' ? items[0] : items[items.length - 1];
}

/**
 * The admin area's main window: the caption `$Administration - <user>` as its `<h1>` and the document title, the
 * menu bar, the tab strip and the open list pages, and the dialog slot.
 *
 * - The session is an agent's when it holds an agent id. An agent session renders File (Logout, Exit), Agents (My
 *   Details), Items (View Items, View Items (By Category), a separator, Add Retail Item, Add Tender Item), Tenders
 *   (View Tenders), Sales (View Sales), Company (Commission Rates, a separator, Show Details via Interface) and Help
 *   (About). Any other session renders every menu and entry except Agents › My Details. Entries not rendered for the
 *   session are absent from the DOM. With no session the shell renders nothing.
 * - The menu bar is a `menubar` of `menuitem` buttons with one tab stop. Clicking an item, or Enter, Space or
 *   ArrowDown on it, opens its `menu`; ArrowUp opens it with the last entry focused; ArrowLeft, ArrowRight, Home and
 *   End move along the bar, opening the adjacent menu while one is open, and so does pointing at another item. A
 *   click on an item closes its open menu unless that pointing opened it. In an open menu ArrowDown, ArrowUp, Home
 *   and End move between entries, ArrowLeft and ArrowRight open the adjacent menu, Tab closes it, and Escape closes
 *   it, returning the focus to its item when the focus was in the menu bar. A press outside the menu bar, or the focus
 *   moving out of it, closes the open menu. A menu that would overflow the viewport's inline end is moved back
 *   towards its start.
 * - Choosing an entry closes the menu, focuses its top-level item and runs the entry. A list entry appends a new tab,
 *   even when a tab of the same page is open, selects it and focuses it. A dialog entry opens its dialog in the one
 *   dialog slot: Add Agent, My Details for the session's agent (only while the session holds an agent id), Add Client,
 *   Add Retail Item, Add Tender Item, Company Details, Close Tenders or About. Company › Commission Rates opens the
 *   commission rates list without its Add, Edit and Remove buttons.
 * - File › Logout and File › Exit ask `$AreYouSureYouWantToExit` titled `$ConfirmExit` (Yes/No, question icon) and
 *   do nothing more unless the answer is Yes. They then end the session through `logout`, show an error outcome of a
 *   failed logout in its message box, and navigate to `/admin` (Logout) or `/` (Exit).
 * - The tab strip is a `tablist` labelled by the caption, rendered while a tab is open. Each `tab` shows the page's
 *   caption, is selected by a click, and has a close button labelled `Close <caption>` outside the tab order; a
 *   middle-button press on the tab, or Delete on it, closes it too. ArrowLeft, ArrowRight, Home and End select and
 *   focus the previous, next, first or last tab. Every open page stays mounted in its own `tabpanel`, hidden while its
 *   tab is not selected, and its Close button closes its tab. Closing the selected tab selects the tab that took its
 *   place, else the one before it; when the focus was in the closed tab or page, the selected tab, or with no tab
 *   left the menu bar, receives it.
 * - While mounted the document title is the caption; the previous title is restored on unmount.
 */
export function AdminShell(): ReactElement | null {
  const { session, logout } = useSession();
  const navigate = useNavigate();
  const { element: messageBox, show, showError } = useMessageBox();
  const idPrefix = useId();
  const headingId = `${idPrefix}caption`;

  const agentSession = session !== null && session.agentId !== null;
  const caption = `${msg('Administration')} - ${session?.userName ?? ''}`;
  const menus = visibleMenus(agentSession);

  const [tabsState, dispatchTabs] = useReducer(tabsReducer, INITIAL_TABS);
  const [dialog, setDialog] = useState<OpenDialog | null>(null);
  const [openMenuKey, setOpenMenuKey] = useState<string | null>(null);
  const [menuShift, setMenuShift] = useState(0);
  const [barFocusKey, setBarFocusKey] = useState<string | null>(null);
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const [pressedKey, setPressedKey] = useState<string | null>(null);
  const [focusVisibleKey, setFocusVisibleKey] = useState<string | null>(null);
  const [hoveredTabKey, setHoveredTabKey] = useState<string | null>(null);

  const menuBarRef = useRef<HTMLElement>(null);
  const openMenuRef = useRef<HTMLDivElement>(null);
  const barItemElements = useRef(new Map<string, HTMLButtonElement>());
  const tabElements = useRef(new Map<string, HTMLDivElement>());
  const pendingMenuFocusRef = useRef<MenuFocus | null>(null);
  const hoverOpenedKeyRef = useRef<string | null>(null);
  const pendingTabFocusRef = useRef(false);
  const nextTabNumberRef = useRef(1);
  const nextDialogNumberRef = useRef(1);
  const leavingRef = useRef(false);

  const rovingKey = menus.some((menu) => menu.key === barFocusKey) ? barFocusKey : (menus[0]?.key ?? null);
  const { tabs, activeKey } = tabsState;

  const tabIdOf = (key: string): string => `${idPrefix}tab-${key}`;
  const panelIdOf = (key: string): string => `${idPrefix}panel-${key}`;
  const barItemIdOf = (key: string): string => `${idPrefix}menubar-${key}`;
  const menuIdOf = (key: string): string => `${idPrefix}menu-${key}`;

  useEffect(() => {
    const previousTitle = document.title;
    document.title = caption;
    return () => {
      document.title = previousTitle;
    };
  }, [caption]);

  useLayoutEffect(() => {
    const element = openMenuRef.current;
    if (openMenuKey === null || element === null || menuShift > 0) {
      return;
    }
    const bounds = element.getBoundingClientRect();
    const overflow = bounds.right - document.documentElement.clientWidth;
    const shift = Math.min(overflow, bounds.left);
    if (overflow > 0 && shift > 0) {
      setMenuShift(shift);
    }
  }, [openMenuKey, menuShift]);

  useEffect(() => {
    const focus = pendingMenuFocusRef.current;
    if (openMenuKey === null || focus === null) {
      return;
    }
    pendingMenuFocusRef.current = null;
    pickMenuItem(openMenuItems(), focus)?.focus();
  }, [openMenuKey]);

  useEffect(() => {
    if (openMenuKey === null) {
      return undefined;
    }
    const handleMouseDown = (event: MouseEvent): void => {
      const bar = menuBarRef.current;
      if (bar !== null && event.target instanceof Node && !bar.contains(event.target)) {
        pendingMenuFocusRef.current = null;
        setOpenMenuKey(null);
      }
    };
    const handleKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') {
        return;
      }
      const bar = menuBarRef.current;
      const focusInBar = bar !== null && bar.contains(document.activeElement);
      pendingMenuFocusRef.current = null;
      setOpenMenuKey(null);
      if (focusInBar) {
        barItemElements.current.get(openMenuKey)?.focus();
      }
    };
    document.addEventListener('mousedown', handleMouseDown, true);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleMouseDown, true);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [openMenuKey]);

  useEffect(() => {
    if (!pendingTabFocusRef.current) {
      return;
    }
    pendingTabFocusRef.current = false;
    if (activeKey !== null) {
      focusTab(activeKey);
    } else if (rovingKey !== null) {
      barItemElements.current.get(rovingKey)?.focus();
    }
  }, [tabsState]);

  /** Returns the entries of the open menu in display order. */
  function openMenuItems(): HTMLButtonElement[] {
    return Array.from(openMenuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);
  }

  function focusTab(key: string): void {
    tabElements.current.get(key)?.querySelector<HTMLButtonElement>('[role="tab"]')?.focus();
  }

  function clearIfCurrent(setter: (update: (current: string | null) => string | null) => void, key: string): void {
    setter((current) => (current === key ? null : current));
  }

  function trackFocusVisible(element: Element, key: string): void {
    const visible = matchesFocusVisible(element);
    setFocusVisibleKey((current) => {
      if (visible) {
        return key;
      }
      return current === key ? null : current;
    });
  }

  /** Pointer and focus handlers that track the hovered, pressed and focus-visible state of the element `key`. */
  function interactionHandlers(key: string) {
    return {
      onMouseEnter: (): void => setHoveredKey(key),
      onMouseLeave: (): void => {
        clearIfCurrent(setHoveredKey, key);
        clearIfCurrent(setPressedKey, key);
      },
      onMouseDown: (event: ReactMouseEvent<HTMLElement>): void => {
        if (event.button === 0) {
          setPressedKey(key);
        }
      },
      onMouseUp: (): void => clearIfCurrent(setPressedKey, key),
      onFocus: (event: ReactFocusEvent<HTMLElement>): void => trackFocusVisible(event.currentTarget, key),
      onKeyUp: (event: ReactKeyboardEvent<HTMLElement>): void => trackFocusVisible(event.currentTarget, key),
      onBlur: (): void => clearIfCurrent(setFocusVisibleKey, key),
    };
  }

  /** Opens the menu `key`, or keeps it open, making its item the menu bar's tab stop and focusing `focus` in it. */
  function openMenu(key: string, focus: MenuFocus | null): void {
    setBarFocusKey(key);
    if (openMenuKey === key) {
      if (focus !== null) {
        pickMenuItem(openMenuItems(), focus)?.focus();
      }
      return;
    }
    pendingMenuFocusRef.current = focus;
    setMenuShift(0);
    setOpenMenuKey(key);
  }

  /** Closes the open menu, focusing its top-level item when `returnFocus` is true. */
  function closeMenu(returnFocus: boolean): void {
    const key = openMenuKey;
    pendingMenuFocusRef.current = null;
    setOpenMenuKey(null);
    if (returnFocus && key !== null) {
      barItemElements.current.get(key)?.focus();
    }
  }

  function openTab(page: PageKind): void {
    const key = `tab${nextTabNumberRef.current}`;
    nextTabNumberRef.current += 1;
    pendingTabFocusRef.current = true;
    dispatchTabs({ type: 'open', tab: { key, page, caption: msg(PAGE_CAPTIONS[page]) } });
  }

  function selectTab(key: string, focus: boolean): void {
    dispatchTabs({ type: 'select', key });
    if (focus) {
      focusTab(key);
    }
  }

  /** Closes the tab `key`; when the focus is in that tab or its page, or nowhere, it moves to the selected tab. */
  function closeTab(key: string): void {
    const tab = tabElements.current.get(key);
    if (tab === undefined) {
      return;
    }
    const focused = document.activeElement;
    const panel = document.getElementById(panelIdOf(key));
    if (
      focused === null ||
      focused === document.body ||
      tab.contains(focused) ||
      (panel !== null && panel.contains(focused))
    ) {
      pendingTabFocusRef.current = true;
    }
    dispatchTabs({ type: 'close', key });
  }

  function openDialog(kind: DialogKind): void {
    let agentId: number | null = null;
    if (kind === 'myDetails') {
      agentId = session?.agentId ?? null;
      if (agentId === null) {
        return;
      }
    }
    const serial = nextDialogNumberRef.current;
    nextDialogNumberRef.current += 1;
    setDialog({ kind, agentId, serial });
  }

  function closeDialog(serial: number): void {
    setDialog((current) => (current !== null && current.serial === serial ? null : current));
  }

  /** Asks the exit question and, on Yes, ends the session and navigates to `destination`. */
  async function leave(destination: string): Promise<void> {
    if (leavingRef.current) {
      return;
    }
    leavingRef.current = true;
    try {
      if ((await show(exitConfirmationOptions())) !== 'yes') {
        return;
      }
      const failure = await logout().then(noLogoutFailure, toLogoutFailure);
      if (failure !== null) {
        await showError(failure.error).catch(ignoreLogoutFailure);
      }
      navigate(destination);
    } finally {
      leavingRef.current = false;
    }
  }

  function runAction(action: MenuAction): void {
    switch (action.type) {
      case 'tab':
        openTab(action.page);
        return;
      case 'dialog':
        openDialog(action.dialog);
        return;
      case 'logout':
        void leave(ADMIN_LOGON_PATH);
        return;
      case 'exit':
        void leave(EXIT_PATH);
        return;
    }
  }

  function chooseEntry(action: MenuAction): void {
    closeMenu(true);
    runAction(action);
  }

  /** Opens the item's menu, or closes it when open, except that it stays open when pointing at the item opened it. */
  function handleBarItemClick(event: ReactMouseEvent<HTMLButtonElement>, key: string): void {
    const openedByHover = hoverOpenedKeyRef.current === key;
    hoverOpenedKeyRef.current = null;
    if (openMenuKey === key) {
      if (!openedByHover) {
        closeMenu(false);
      }
      return;
    }
    openMenu(key, event.detail === 0 ? 'first' : null);
  }

  function handleBarItemMouseEnter(key: string): void {
    setHoveredKey(`bar:${key}`);
    if (openMenuKey === null || openMenuKey === key) {
      return;
    }
    const bar = menuBarRef.current;
    const focusInBar = bar !== null && bar.contains(document.activeElement);
    openMenu(key, null);
    hoverOpenedKeyRef.current = key;
    if (focusInBar) {
      barItemElements.current.get(key)?.focus();
    }
  }

  function handleBarItemMouseLeave(key: string): void {
    clearIfCurrent(setHoveredKey, `bar:${key}`);
    clearIfCurrent(setPressedKey, `bar:${key}`);
    if (hoverOpenedKeyRef.current === key) {
      hoverOpenedKeyRef.current = null;
    }
  }

  function handleBarItemKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>, index: number): void {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const count = menus.length;
    let target: number;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        openMenu(menus[index].key, 'first');
        return;
      case 'ArrowUp':
        event.preventDefault();
        openMenu(menus[index].key, 'last');
        return;
      case 'ArrowRight':
        target = (index + 1) % count;
        break;
      case 'ArrowLeft':
        target = (index + count - 1) % count;
        break;
      case 'Home':
        target = 0;
        break;
      case 'End':
        target = count - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const next = menus[target];
    if (openMenuKey !== null) {
      openMenu(next.key, null);
    } else {
      setBarFocusKey(next.key);
    }
    barItemElements.current.get(next.key)?.focus();
  }

  function handleMenuKeyDown(event: ReactKeyboardEvent<HTMLDivElement>, menuIndex: number): void {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const items = openMenuItems();
    const count = items.length;
    const current = items.findIndex((item) => item === document.activeElement);
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        items[(current + 1) % count]?.focus();
        return;
      case 'ArrowUp':
        event.preventDefault();
        items[current <= 0 ? count - 1 : current - 1]?.focus();
        return;
      case 'Home':
        event.preventDefault();
        items[0]?.focus();
        return;
      case 'End':
        event.preventDefault();
        items[count - 1]?.focus();
        return;
      case 'ArrowRight':
      case 'ArrowLeft': {
        event.preventDefault();
        const step = event.key === 'ArrowRight' ? 1 : menus.length - 1;
        openMenu(menus[(menuIndex + step) % menus.length].key, 'first');
        return;
      }
      case 'Tab':
        closeMenu(false);
        return;
      default:
        return;
    }
  }

  function handleMenuBarBlur(event: ReactFocusEvent<HTMLElement>): void {
    const next = event.relatedTarget;
    if (openMenuKey !== null && next instanceof Node && !event.currentTarget.contains(next)) {
      closeMenu(false);
    }
  }

  function handleTabKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>, index: number): void {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const count = tabs.length;
    let target: OpenTab | undefined;
    switch (event.key) {
      case 'ArrowRight':
        target = tabs[(index + 1) % count];
        break;
      case 'ArrowLeft':
        target = tabs[(index + count - 1) % count];
        break;
      case 'Home':
        target = tabs[0];
        break;
      case 'End':
        target = tabs[count - 1];
        break;
      case 'Delete':
        event.preventDefault();
        closeTab(tabs[index].key);
        return;
      default:
        return;
    }
    event.preventDefault();
    if (target !== undefined) {
      selectTab(target.key, true);
    }
  }

  function handleTabMouseDown(event: ReactMouseEvent<HTMLDivElement>, key: string): void {
    if (event.button === 1) {
      event.preventDefault();
      closeTab(key);
    }
  }

  if (session === null) {
    return null;
  }

  return (
    <div style={SHELL_STYLE}>
      <header style={HEADER_STYLE}>
        <h1 id={headingId} style={CAPTION_STYLE}>
          {caption}
        </h1>
      </header>
      <nav
        ref={menuBarRef}
        role="menubar"
        aria-label={MENU_BAR_LABEL}
        style={MENU_BAR_STYLE}
        onBlur={handleMenuBarBlur}
      >
        {menus.map((menu, index) => {
          const open = openMenuKey === menu.key;
          const barKey = `bar:${menu.key}`;
          const barItemId = barItemIdOf(menu.key);
          const menuId = menuIdOf(menu.key);
          return (
            <div key={menu.key} role="none" style={MENU_SLOT_STYLE}>
              <button
                ref={(element) => {
                  if (element === null) {
                    barItemElements.current.delete(menu.key);
                  } else {
                    barItemElements.current.set(menu.key, element);
                  }
                }}
                id={barItemId}
                type="button"
                role="menuitem"
                aria-haspopup="menu"
                aria-expanded={open}
                aria-controls={open ? menuId : undefined}
                tabIndex={menu.key === rovingKey ? 0 : -1}
                style={barItemStyle(
                  open,
                  hoveredKey === barKey,
                  pressedKey === barKey,
                  focusVisibleKey === barKey,
                )}
                {...interactionHandlers(barKey)}
                onMouseEnter={() => handleBarItemMouseEnter(menu.key)}
                onMouseLeave={() => handleBarItemMouseLeave(menu.key)}
                onClick={(event) => handleBarItemClick(event, menu.key)}
                onKeyDown={(event) => handleBarItemKeyDown(event, index)}
              >
                {menu.caption}
              </button>
              {open && (
                <div
                  ref={openMenuRef}
                  id={menuId}
                  role="menu"
                  aria-labelledby={barItemId}
                  style={menuStyle(menuShift)}
                  onKeyDown={(event) => handleMenuKeyDown(event, index)}
                >
                  {menu.entries.map((entry) => {
                    if (entry.kind === 'separator') {
                      return <div key={entry.key} role="separator" style={MENU_SEPARATOR_STYLE} />;
                    }
                    const entryKey = `entry:${entry.key}`;
                    return (
                      <button
                        key={entry.key}
                        type="button"
                        role="menuitem"
                        tabIndex={-1}
                        style={menuEntryStyle(
                          hoveredKey === entryKey,
                          pressedKey === entryKey,
                          focusVisibleKey === entryKey,
                        )}
                        {...interactionHandlers(entryKey)}
                        onClick={() => chooseEntry(entry.action)}
                      >
                        {entry.caption}
                      </button>
                    );
                  })}
                </div>
              )}
            </div>
          );
        })}
      </nav>
      <div style={TAB_AREA_STYLE}>
        {tabs.length > 0 && (
          <div role="tablist" aria-labelledby={headingId} style={TAB_LIST_STYLE}>
            {tabs.map((tab, index) => {
              const active = tab.key === activeKey;
              const tabKey = `tab:${tab.key}`;
              const closeKey = `close:${tab.key}`;
              return (
                <div
                  key={tab.key}
                  ref={(element) => {
                    if (element === null) {
                      tabElements.current.delete(tab.key);
                    } else {
                      tabElements.current.set(tab.key, element);
                    }
                  }}
                  role="presentation"
                  style={tabStyle(active, hoveredTabKey === tab.key)}
                  onMouseEnter={() => setHoveredTabKey(tab.key)}
                  onMouseLeave={() => clearIfCurrent(setHoveredTabKey, tab.key)}
                  onMouseDown={(event) => handleTabMouseDown(event, tab.key)}
                >
                  <button
                    id={tabIdOf(tab.key)}
                    type="button"
                    role="tab"
                    aria-selected={active}
                    aria-controls={panelIdOf(tab.key)}
                    tabIndex={active ? 0 : -1}
                    style={tabButtonStyle(active, focusVisibleKey === tabKey)}
                    onFocus={(event) => trackFocusVisible(event.currentTarget, tabKey)}
                    onKeyUp={(event) => trackFocusVisible(event.currentTarget, tabKey)}
                    onBlur={() => clearIfCurrent(setFocusVisibleKey, tabKey)}
                    onClick={() => selectTab(tab.key, false)}
                    onKeyDown={(event) => handleTabKeyDown(event, index)}
                  >
                    {tab.caption}
                  </button>
                  <button
                    type="button"
                    tabIndex={-1}
                    aria-label={`${CLOSE_TAB_CAPTION} ${tab.caption}`}
                    style={closeButtonStyle(
                      hoveredKey === closeKey,
                      pressedKey === closeKey,
                      focusVisibleKey === closeKey,
                    )}
                    {...interactionHandlers(closeKey)}
                    onClick={() => closeTab(tab.key)}
                  >
                    <span aria-hidden="true">{CLOSE_GLYPH}</span>
                  </button>
                </div>
              );
            })}
          </div>
        )}
      </div>
      <main style={MAIN_STYLE}>
        {tabs.map((tab) => (
          <div
            key={tab.key}
            id={panelIdOf(tab.key)}
            role="tabpanel"
            aria-labelledby={tabIdOf(tab.key)}
            hidden={tab.key !== activeKey}
          >
            {renderPage(tab.page, () => closeTab(tab.key))}
          </div>
        ))}
      </main>
      {dialog !== null && (
        <Fragment key={dialog.serial}>{renderDialog(dialog, () => closeDialog(dialog.serial))}</Fragment>
      )}
      {messageBox}
    </div>
  );
}
