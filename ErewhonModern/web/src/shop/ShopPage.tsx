// Shop search screen: the search plinth over the company's items, the list of found items with Buy or Bid, one item's
// details in place of the list, and the session cart with its total, Checkout and Empty.

import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, ReactElement } from 'react';
import { useNavigate } from 'react-router-dom';
import { ErrorOutcomeError, UnauthorizedError, api } from '../api/client';
import { on, onEntityChanged } from '../api/realtime';
import type {
  CartLineView,
  CartView,
  EntityTypeName,
  Id,
  ShopItemKind,
  ShopReferenceResponse,
  ShopSearchRequest,
  ShopSearchResponse,
  ShopSearchRow,
} from '../api/types';
import { getItem, getSearchResultCells } from '../admin/items/itemDisplay';
import { DataTable } from '../components/DataTable';
import type { DataTableColumn, RowStyle } from '../components/DataTable';
import { useMessageBox } from '../components/MessageBox';
import { currencyFormat } from '../format';
import { msg } from '../messages/messages';
import { ItemDetails } from './ItemDetails';

const REFERENCE_PATH = '/api/app/shop/reference';
const SEARCH_PATH = '/api/app/shop/search';
const CART_PATH = '/api/app/shop/cart';
const EMPTY_PATH = '/api/app/shop/cart/empty';
const CHECKOUT_ROUTE = '/shop/checkout';

const PAGE_CAPTION = 'Erewhon Investments Inc';
const SEARCH_HEADING = 'Search';
const COUNTRY_CAPTION = 'Country:';
const REGION_CAPTION = 'Region:';
const CATEGORY_CAPTION = 'Product Category:';
const RETAIL_ITEMS_CAPTION = 'Retail Items';
const TENDER_ITEMS_CAPTION = 'Items for Tender';
const ALL_ITEMS_CAPTION = 'All Items';
const PRICE_LOWER_CAPTION = 'Lower price range:';
const PRICE_UPPER_CAPTION = 'Upper price range:';
const CLOSED_TENDERS_CAPTION = 'Include Closed Tenders';
const SEARCH_CAPTION = 'Search';
const RESET_CAPTION = 'Reset';
const SELECTION_HEADING = 'Selection';
const CLEAR_CAPTION = 'Clear';
const CART_HEADING = 'Shopping Cart';
const TOTAL_CAPTION = 'Total:';
const EMPTY_TOTAL_CAPTION = '$0.00';
const CHECKOUT_CAPTION = 'Checkout';
const EMPTY_CAPTION = 'Empty';

const NO_SELECTION = '';
const PRICE_MAX_LENGTH = 12;
const DECIMAL_PREFIX = /^\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))/;

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['RetailItem', 'TenderItem', 'ItemCategory', 'Tender'];

const ITEM_KIND_OPTIONS: ReadonlyArray<{ kind: ShopItemKind; caption: string }> = [
  { kind: 'retail', caption: RETAIL_ITEMS_CAPTION },
  { kind: 'tender', caption: TENDER_ITEMS_CAPTION },
  { kind: 'all', caption: ALL_ITEMS_CAPTION },
];

const PRODUCTS_BACKGROUND = 'rgb(255, 255, 128)';
const SEARCH_BACKGROUND = 'rgb(195, 211, 227)';
const ITEMS_BACKGROUND = 'rgb(225, 222, 215)';

const PAGE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1rem',
  boxSizing: 'border-box',
  maxInlineSize: '52rem',
  marginInline: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1.25rem',
  fontWeight: 'bold',
};

const MESSAGE_STYLE: CSSProperties = {
  margin: 0,
  whiteSpace: 'pre-wrap',
  overflowWrap: 'break-word',
};

const PRODUCTS_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'stretch',
  gap: '0.3125rem',
  boxSizing: 'border-box',
  paddingInlineStart: '1px',
  background: PRODUCTS_BACKGROUND,
};

const PLINTH_BASE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.375rem',
  boxSizing: 'border-box',
  minBlockSize: '25.375rem',
  minInlineSize: 0,
  paddingBlockStart: '0.125rem',
  paddingBlockEnd: '0.5625rem',
  paddingInline: '0.375rem',
  overflowWrap: 'break-word',
};

const SEARCH_PLINTH_STYLE: CSSProperties = {
  ...PLINTH_BASE_STYLE,
  flex: '0 0 9.5625rem',
  background: SEARCH_BACKGROUND,
};

const ITEMS_PLINTH_STYLE: CSSProperties = {
  ...PLINTH_BASE_STYLE,
  flex: '1 1 27.8125rem',
  background: ITEMS_BACKGROUND,
};

const CART_PLINTH_STYLE: CSSProperties = {
  ...PLINTH_BASE_STYLE,
  flex: '0 1 11.875rem',
  background: ITEMS_BACKGROUND,
};

const PLINTH_HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
};

const FIELD_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.125rem',
};

const CONTROL_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  margin: 0,
  font: 'inherit',
};

const KIND_GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.25rem',
  margin: 0,
  padding: 0,
  paddingInlineStart: '0.75rem',
  border: 0,
};

const CHOICE_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.25rem',
};

const CHOICE_INPUT_STYLE: CSSProperties = {
  margin: 0,
};

const TABLE_AREA_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 0 auto',
  blockSize: '20.6875rem',
  overflow: 'auto',
  background: 'white',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '0.375rem',
  marginBlockStart: 'auto',
};

const CENTRED_BUTTON_ROW_STYLE: CSSProperties = {
  ...BUTTON_ROW_STYLE,
  justifyContent: 'center',
};

const BUTTON_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: '3.625rem',
  minBlockSize: '1.25rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.5rem',
  font: 'inherit',
};

const ROW_BUTTON_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  paddingBlock: 0,
  paddingInline: '0.375rem',
  font: 'inherit',
};

const TOTAL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  justifyContent: 'space-between',
  gap: '0.5rem',
  margin: 0,
  fontWeight: 'bold',
};

/** Values of the search plinth's controls; the price ranges as typed. */
interface SearchPlinth {
  countryId: Id | null;
  regionId: Id | null;
  itemCategoryId: Id | null;
  itemKind: ShopItemKind;
  priceLowText: string;
  priceHighText: string;
  includeClosedTenders: boolean;
}

/** The plinth after Reset: no country, region or category, all items, no price range, closed tenders excluded. */
const RESET_PLINTH: SearchPlinth = {
  countryId: null,
  regionId: null,
  itemCategoryId: null,
  itemKind: 'all',
  priceLowText: '',
  priceHighText: '',
  includeClosedTenders: false,
};

const CART_COLUMNS: DataTableColumn<CartLineView>[] = [
  { key: 'product', header: msg('Product'), render: (line) => line.itemName },
  {
    key: 'price',
    header: msg('Price'),
    render: (line) => currencyFormat(line.isTender ? line.offer : line.itemPrice),
  },
];

/**
 * Returns the decimal at the start of `text` after any leading white space: an optional sign, digits and an optional
 * fraction, e.g. ` 800 ` → 800, `12.50` → 12.5, `1,000` → 1; returns 0 when the text does not start with a number,
 * e.g. `''` or `abc`.
 */
function parsePrice(text: string): number {
  const match = DECIMAL_PREFIX.exec(text);
  return match === null ? 0 : Number(match[1]);
}

/** Returns the price range text shown for a stored bound: its integer part, or `''` for 0. */
function priceText(value: number): string {
  return value === 0 ? '' : String(Math.trunc(value));
}

/** Returns the select value of an id: its decimal text, or `''` for none. */
function optionValue(id: Id | null): string {
  return id === null ? NO_SELECTION : String(id);
}

/** Returns the id of a select value: the number it holds, or null for `''`. */
function selectedOption(value: string): Id | null {
  return value === NO_SELECTION ? null : Number(value);
}

/**
 * Returns the plinth for the stored search of `reference`: its country, region and category where the reference
 * lists them (the region among the country's regions), else none; its item kind; its price bounds through
 * {@link priceText}; and its closed-tender flag.
 */
function plinthFromReference(reference: ShopReferenceResponse): SearchPlinth {
  const search = reference.currentSearch;
  const country = reference.countries.find((option) => option.id === search.countryId);
  const regionListed = country !== undefined && country.regions.some((option) => option.id === search.regionId);
  const categoryListed = reference.categories.some((option) => option.id === search.itemCategoryId);

  return {
    countryId: country === undefined ? null : country.id,
    regionId: regionListed ? search.regionId : null,
    itemCategoryId: categoryListed ? search.itemCategoryId : null,
    itemKind: search.itemKind,
    priceLowText: priceText(search.priceLow),
    priceHighText: priceText(search.priceHigh),
    includeClosedTenders: search.includeClosedTenders,
  };
}

/** Returns the search request of the plinth's values, the price ranges read through {@link parsePrice}. */
function searchRequest(plinth: SearchPlinth): ShopSearchRequest {
  return {
    countryId: plinth.countryId,
    regionId: plinth.countryId === null ? null : plinth.regionId,
    itemCategoryId: plinth.itemCategoryId,
    itemKind: plinth.itemKind,
    priceLow: parsePrice(plinth.priceLowText),
    priceHigh: parsePrice(plinth.priceHighText),
    includeClosedTenders: plinth.includeClosedTenders,
  };
}

/** Returns the item id of the cart line, which keys its table row. */
function lineKey(line: CartLineView): Id {
  return line.itemId;
}

/** Returns the item id of the search row, which keys its table row. */
function rowKey(row: ShopSearchRow): Id {
  return row.id;
}

/** Returns `'closed'` for a closed tender item, which renders its row red, and no style otherwise. */
function resultRowStyle(row: ShopSearchRow): RowStyle {
  return row.isTender && row.isClosed ? 'closed' : undefined;
}

/**
 * Search screen of the shop area.
 * - On mount it sets the document title to "Erewhon Investments Inc", restoring the previous title on unmount, reads
 *   GET /api/app/shop/reference and sets the search plinth from its stored search, each non-zero price bound as its
 *   integer part and a zero bound as empty text, runs the search, and then reads the session cart through
 *   GET /api/app/shop/cart.
 * - The Search plinth holds the Country, Region and Product Category lists, each led by `$Select` for none, in the
 *   order the reference lists them; the Retail Items, Items for Tender and All Items options; the lower and upper
 *   price ranges; Include Closed Tenders; and Search and Reset. Region lists the chosen country's regions and is
 *   disabled while no country is chosen; choosing a country sets the region to none.
 * - Search sends POST /api/app/shop/search with the plinth's values, each price range read as the decimal at the
 *   start of its text, or 0 when the text does not start with a number, and shows the returned rows in the order
 *   received. A successful search closes the item that was open when it was sent, unless another item has been
 *   opened since. Only the response to the latest search is applied; a failed latest search keeps the rows shown.
 * - Reset sets the plinth to no country, region or category, All Items, empty price ranges and closed tenders
 *   excluded, and does not search. Clear empties the list without searching and drops the response of a search
 *   still pending; it is disabled while an item is open.
 * - Each row shows a Buy or Bid button, then the code, name, listed date and price, the five cells of
 *   `getSearchResultCells`; a closed tender item's row is red. Clicking a row selects it; its button, a double-click
 *   or Enter on the row opens the item in {@link ItemDetails} in place of the list. The row buttons are not tab
 *   stops; the rows are. Closing the details shows the list again; adding the item to the cart closes the details,
 *   reads the cart and searches again. When opening or closing the details leaves no element focused, the focus
 *   moves to the details' first button on opening, and on closing to the selected row, or to the list area after an
 *   add or when no row is selected.
 * - The Shopping Cart plinth lists each line's item name and price, the offer of a tender line or the item price of
 *   a retail line, then "Total:" with the cart total in currency format ($0.00 until the cart is read), Checkout and
 *   Empty.
 * - Empty sends POST /api/app/shop/cart/empty, shows the emptied cart and searches again. Checkout reads the cart:
 *   with no lines it shows `$ShoppingCartEmptyPleaseBuyBid` titled `$CartEmpty` with the information icon and the
 *   page stays; otherwise it navigates to `/shop/checkout`.
 * - A CartItemDeleted event shows `$ItemDeletedByAnotherUserRemoveFromCart` titled `$DeletedItem`, closes the item
 *   when it is the deleted one, reads the cart and searches again. An EntityChanged event for a retail item, tender
 *   item, item category or tender searches again while no item is open. Neither event searches before the plinth
 *   has been set from the reference.
 * - A failed request passes an error outcome or an HTTP 401 to `showError` and shows any other failure as its message
 *   titled `$Error` with the error icon. Only the response to the latest cart request is shown. After unmount no
 *   response is shown, no box is opened and nothing navigates.
 */
export function ShopPage(): ReactElement {
  const navigate = useNavigate();
  const { element: messageBox, show, showError } = useMessageBox();
  const idPrefix = useId();
  const [reference, setReference] = useState<ShopReferenceResponse | null>(null);
  const [plinth, setPlinth] = useState<SearchPlinth>(RESET_PLINTH);
  const [rows, setRows] = useState<ShopSearchRow[]>([]);
  const [selectedId, setSelectedId] = useState<Id | null>(null);
  const [openItemId, setOpenItemId] = useState<Id | null>(null);
  const [cart, setCart] = useState<CartView | null>(null);
  const [loading, setLoading] = useState(true);
  const mountedRef = useRef(false);
  const plinthLoadedRef = useRef(false);
  const plinthRef = useRef<SearchPlinth>(RESET_PLINTH);
  const openItemRef = useRef<Id | null>(null);
  const searchRequestRef = useRef(0);
  const cartRequestRef = useRef(0);
  const shownItemRef = useRef<Id | null>(null);
  const focusRowOnCloseRef = useRef(true);
  const selectionRef = useRef<HTMLElement>(null);
  const resultsAreaRef = useRef<HTMLDivElement>(null);

  const searchHeadingId = `${idPrefix}search`;
  const selectionHeadingId = `${idPrefix}selection`;
  const cartHeadingId = `${idPrefix}cart`;
  const countryFieldId = `${idPrefix}country`;
  const regionFieldId = `${idPrefix}region`;
  const categoryFieldId = `${idPrefix}category`;
  const itemKindName = `${idPrefix}itemKind`;
  const priceLowerFieldId = `${idPrefix}priceLower`;
  const priceUpperFieldId = `${idPrefix}priceUpper`;

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    const previousTitle = document.title;
    document.title = PAGE_CAPTION;
    return () => {
      document.title = previousTitle;
    };
  }, []);

  /** Merges `patch` into the plinth values that searches read and that the controls show. */
  const updatePlinth = useCallback((patch: Partial<SearchPlinth>): void => {
    const next: SearchPlinth = { ...plinthRef.current, ...patch };
    plinthRef.current = next;
    setPlinth(next);
  }, []);

  /** Opens the details of the item with id `itemId` in place of the list, or shows the list for null. */
  const showItem = useCallback((itemId: Id | null): void => {
    openItemRef.current = itemId;
    setOpenItemId(itemId);
  }, []);

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

  /** Runs the search with the current plinth values and shows its rows when it is still the latest search. */
  const runSearch = useCallback(async (): Promise<void> => {
    searchRequestRef.current += 1;
    const requestId = searchRequestRef.current;
    const openAtRequest = openItemRef.current;

    let response: ShopSearchResponse;
    try {
      response = await api.post<ShopSearchResponse>(SEARCH_PATH, searchRequest(plinthRef.current));
    } catch (error: unknown) {
      if (mountedRef.current && requestId === searchRequestRef.current) {
        await reportFailure(error);
      }
      return;
    }

    if (!mountedRef.current || requestId !== searchRequestRef.current) {
      return;
    }

    const found = response.items;
    setRows(found);
    setSelectedId((current) => (current !== null && found.some((row) => row.id === current) ? current : null));
    if (openItemRef.current === openAtRequest) {
      showItem(null);
    }
  }, [reportFailure, showItem]);

  /** Numbers a new cart request; only the response of the latest numbered request is shown. */
  const startCartRequest = useCallback((): number => {
    cartRequestRef.current += 1;
    return cartRequestRef.current;
  }, []);

  /** Shows `next` when `requestId` is the latest cart request and the page is mounted. */
  const commitCart = useCallback((requestId: number, next: CartView): void => {
    if (mountedRef.current && requestId === cartRequestRef.current) {
      setCart(next);
    }
  }, []);

  /** Reads the session cart, shows it, and resolves to it; resolves to null after reporting a failed read. */
  const loadCart = useCallback(async (): Promise<CartView | null> => {
    const requestId = startCartRequest();
    try {
      const loaded = await api.get<CartView>(CART_PATH);
      commitCart(requestId, loaded);
      return loaded;
    } catch (error: unknown) {
      if (mountedRef.current) {
        await reportFailure(error);
      }
      return null;
    }
  }, [commitCart, reportFailure, startCartRequest]);

  useEffect(() => {
    const load = async (): Promise<void> => {
      let loaded: ShopReferenceResponse;
      try {
        loaded = await api.get<ShopReferenceResponse>(REFERENCE_PATH);
      } catch (error: unknown) {
        if (mountedRef.current) {
          await reportFailure(error);
        }
        return;
      }

      if (!mountedRef.current) {
        return;
      }

      setReference(loaded);
      updatePlinth(plinthFromReference(loaded));
      plinthLoadedRef.current = true;
      await runSearch();
      if (mountedRef.current) {
        await loadCart();
      }
    };

    void load().finally(() => {
      if (mountedRef.current) {
        setLoading(false);
      }
    });
  }, [loadCart, reportFailure, runSearch, updatePlinth]);

  useEffect(() => {
    const previous = shownItemRef.current;
    shownItemRef.current = openItemId;
    if (previous === openItemId) {
      return;
    }

    const focusRow = focusRowOnCloseRef.current;
    focusRowOnCloseRef.current = true;
    const active = document.activeElement;
    if (active !== null && active !== document.body) {
      return;
    }

    if (openItemId !== null) {
      selectionRef.current?.querySelector<HTMLElement>('button')?.focus();
      return;
    }

    const area = resultsAreaRef.current;
    const selectedRow = focusRow ? area?.querySelector<HTMLElement>('tr[aria-selected="true"]') : null;
    (selectedRow ?? area)?.focus();
  }, [openItemId]);

  useEffect(
    () =>
      on('CartItemDeleted', async (message) => {
        await show({
          title: msg('DeletedItem'),
          message: msg('ItemDeletedByAnotherUserRemoveFromCart'),
          buttons: 'ok',
        });
        if (!mountedRef.current) {
          return;
        }

        if (openItemRef.current === message.itemId) {
          showItem(null);
        }
        await loadCart();
        if (mountedRef.current && plinthLoadedRef.current) {
          await runSearch();
        }
      }),
    [loadCart, runSearch, show, showItem],
  );

  useEffect(
    () =>
      onEntityChanged(WATCHED_ENTITY_TYPES, () => {
        if (!mountedRef.current || !plinthLoadedRef.current || openItemRef.current !== null) {
          return undefined;
        }
        return runSearch();
      }),
    [runSearch],
  );

  /** Selects the row and opens its item's details in place of the list. */
  const openRow = useCallback(
    (row: ShopSearchRow): void => {
      setSelectedId(row.id);
      showItem(getItem(row).id);
    },
    [showItem],
  );

  const handleCountryChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    updatePlinth({ countryId: selectedOption(event.target.value), regionId: null });
  };

  const handleRegionChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    updatePlinth({ regionId: selectedOption(event.target.value) });
  };

  const handleCategoryChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    updatePlinth({ itemCategoryId: selectedOption(event.target.value) });
  };

  const handleReset = (): void => {
    updatePlinth(RESET_PLINTH);
  };

  const handleClear = (): void => {
    searchRequestRef.current += 1;
    setRows([]);
    setSelectedId(null);
  };

  const handleCloseItem = (): void => {
    showItem(null);
  };

  const handleItemAdded = async (): Promise<void> => {
    focusRowOnCloseRef.current = false;
    showItem(null);
    await loadCart();
    if (mountedRef.current) {
      await runSearch();
    }
  };

  const handleEmpty = async (): Promise<void> => {
    const requestId = startCartRequest();
    let emptied: CartView;
    try {
      emptied = await api.post<CartView>(EMPTY_PATH);
    } catch (error: unknown) {
      if (mountedRef.current) {
        await reportFailure(error);
      }
      return;
    }

    if (!mountedRef.current) {
      return;
    }

    commitCart(requestId, emptied);
    await runSearch();
  };

  const handleCheckout = async (): Promise<void> => {
    const loaded = await loadCart();
    if (!mountedRef.current || loaded === null) {
      return;
    }

    if (loaded.lines.length === 0) {
      await show({
        title: msg('CartEmpty'),
        message: msg('ShoppingCartEmptyPleaseBuyBid'),
        icon: 'information',
        buttons: 'ok',
      });
      return;
    }

    navigate(CHECKOUT_ROUTE);
  };

  const cellsById = useMemo(
    () => new Map<Id, string[]>(rows.map((row) => [row.id, getSearchResultCells(row)])),
    [rows],
  );

  const resultColumns = useMemo((): DataTableColumn<ShopSearchRow>[] => {
    const cellsOf = (row: ShopSearchRow): string[] => cellsById.get(row.id) ?? getSearchResultCells(row);
    const nameCellId = (row: ShopSearchRow): string => `${idPrefix}name${row.id}`;

    return [
      {
        key: 'buyBid',
        header: '',
        render: (row) => (
          <button
            type="button"
            tabIndex={-1}
            style={ROW_BUTTON_STYLE}
            aria-describedby={nameCellId(row)}
            onClick={() => openRow(row)}
          >
            {cellsOf(row)[0]}
          </button>
        ),
      },
      { key: 'code', header: msg('Code'), render: (row) => cellsOf(row)[1] },
      { key: 'product', header: msg('Product'), render: (row) => <span id={nameCellId(row)}>{cellsOf(row)[2]}</span> },
      { key: 'listed', header: msg('Listed'), render: (row) => cellsOf(row)[3] },
      { key: 'price', header: msg('Price'), render: (row) => cellsOf(row)[4] },
    ];
  }, [cellsById, idPrefix, openRow]);

  const regions = reference?.countries.find((option) => option.id === plinth.countryId)?.regions ?? [];
  const totalText = cart === null ? EMPTY_TOTAL_CAPTION : currencyFormat(cart.total);
  const itemOpen = openItemId !== null;

  return (
    <main style={PAGE_STYLE} aria-busy={loading}>
      <h1 style={HEADING_STYLE}>{PAGE_CAPTION}</h1>
      <p style={MESSAGE_STYLE}>{msg('ClientFormSearchInstructions')}</p>
      <div style={PRODUCTS_STYLE}>
        <section aria-labelledby={searchHeadingId} style={SEARCH_PLINTH_STYLE}>
          <h2 id={searchHeadingId} style={PLINTH_HEADING_STYLE}>
            {SEARCH_HEADING}
          </h2>
          <div style={FIELD_STYLE}>
            <label htmlFor={countryFieldId}>{COUNTRY_CAPTION}</label>
            <select
              id={countryFieldId}
              value={optionValue(plinth.countryId)}
              onChange={handleCountryChange}
              style={CONTROL_STYLE}
            >
              <option value={NO_SELECTION}>{msg('Select')}</option>
              {(reference?.countries ?? []).map((option) => (
                <option key={option.id} value={optionValue(option.id)}>
                  {option.name}
                </option>
              ))}
            </select>
          </div>
          <div style={FIELD_STYLE}>
            <label htmlFor={regionFieldId}>{REGION_CAPTION}</label>
            <select
              id={regionFieldId}
              value={optionValue(plinth.regionId)}
              disabled={plinth.countryId === null}
              onChange={handleRegionChange}
              style={CONTROL_STYLE}
            >
              <option value={NO_SELECTION}>{msg('Select')}</option>
              {regions.map((option) => (
                <option key={option.id} value={optionValue(option.id)}>
                  {option.name}
                </option>
              ))}
            </select>
          </div>
          <div style={FIELD_STYLE}>
            <label htmlFor={categoryFieldId}>{CATEGORY_CAPTION}</label>
            <select
              id={categoryFieldId}
              value={optionValue(plinth.itemCategoryId)}
              onChange={handleCategoryChange}
              style={CONTROL_STYLE}
            >
              <option value={NO_SELECTION}>{msg('Select')}</option>
              {(reference?.categories ?? []).map((option) => (
                <option key={option.id} value={optionValue(option.id)}>
                  {option.name}
                </option>
              ))}
            </select>
          </div>
          <fieldset style={KIND_GROUP_STYLE}>
            {ITEM_KIND_OPTIONS.map(({ kind, caption }) => (
              <label key={kind} style={CHOICE_STYLE}>
                <input
                  type="radio"
                  name={itemKindName}
                  value={kind}
                  checked={plinth.itemKind === kind}
                  onChange={() => updatePlinth({ itemKind: kind })}
                  style={CHOICE_INPUT_STYLE}
                />
                {caption}
              </label>
            ))}
          </fieldset>
          <div style={FIELD_STYLE}>
            <label htmlFor={priceLowerFieldId}>{PRICE_LOWER_CAPTION}</label>
            <input
              id={priceLowerFieldId}
              type="text"
              inputMode="decimal"
              autoComplete="off"
              maxLength={PRICE_MAX_LENGTH}
              value={plinth.priceLowText}
              onChange={(event: ChangeEvent<HTMLInputElement>) => updatePlinth({ priceLowText: event.target.value })}
              style={CONTROL_STYLE}
            />
          </div>
          <div style={FIELD_STYLE}>
            <label htmlFor={priceUpperFieldId}>{PRICE_UPPER_CAPTION}</label>
            <input
              id={priceUpperFieldId}
              type="text"
              inputMode="decimal"
              autoComplete="off"
              maxLength={PRICE_MAX_LENGTH}
              value={plinth.priceHighText}
              onChange={(event: ChangeEvent<HTMLInputElement>) => updatePlinth({ priceHighText: event.target.value })}
              style={CONTROL_STYLE}
            />
          </div>
          <label style={CHOICE_STYLE}>
            <input
              type="checkbox"
              checked={plinth.includeClosedTenders}
              onChange={(event: ChangeEvent<HTMLInputElement>) =>
                updatePlinth({ includeClosedTenders: event.target.checked })
              }
              style={CHOICE_INPUT_STYLE}
            />
            {CLOSED_TENDERS_CAPTION}
          </label>
          <div style={CENTRED_BUTTON_ROW_STYLE}>
            <button type="button" style={BUTTON_STYLE} onClick={() => void runSearch()}>
              {SEARCH_CAPTION}
            </button>
            <button type="button" style={BUTTON_STYLE} onClick={handleReset}>
              {RESET_CAPTION}
            </button>
          </div>
        </section>
        <section ref={selectionRef} aria-labelledby={selectionHeadingId} style={ITEMS_PLINTH_STYLE}>
          <h2 id={selectionHeadingId} style={PLINTH_HEADING_STYLE}>
            {SELECTION_HEADING}
          </h2>
          {openItemId !== null ? (
            <ItemDetails itemId={openItemId} onClose={handleCloseItem} onAdded={() => void handleItemAdded()} />
          ) : (
            <div ref={resultsAreaRef} tabIndex={-1} style={TABLE_AREA_STYLE}>
              <DataTable
                columns={resultColumns}
                rows={rows}
                rowKey={rowKey}
                selectedKey={selectedId}
                onSelect={(row) => setSelectedId(row.id)}
                onDoubleClick={openRow}
                rowStyle={resultRowStyle}
              />
            </div>
          )}
          <div style={BUTTON_ROW_STYLE}>
            <button type="button" style={BUTTON_STYLE} disabled={itemOpen} onClick={handleClear}>
              {CLEAR_CAPTION}
            </button>
          </div>
        </section>
        <section aria-labelledby={cartHeadingId} style={CART_PLINTH_STYLE}>
          <h2 id={cartHeadingId} style={PLINTH_HEADING_STYLE}>
            {CART_HEADING}
          </h2>
          <div style={TABLE_AREA_STYLE}>
            <DataTable columns={CART_COLUMNS} rows={cart?.lines ?? []} rowKey={lineKey} />
          </div>
          <p style={TOTAL_STYLE}>
            <span>{TOTAL_CAPTION}</span>
            <span>{totalText}</span>
          </p>
          <div style={CENTRED_BUTTON_ROW_STYLE}>
            <button type="button" style={BUTTON_STYLE} onClick={() => void handleCheckout()}>
              {CHECKOUT_CAPTION}
            </button>
            <button type="button" style={BUTTON_STYLE} onClick={() => void handleEmpty()}>
              {EMPTY_CAPTION}
            </button>
          </div>
        </section>
      </div>
      {messageBox}
    </main>
  );
}
