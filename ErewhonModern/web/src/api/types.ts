// Wire types of the JSON bodies sent to and received from /api/app/** and of the /hubs/erewhon event payloads.
// Property names are the camelCase names of the Erewhon.Api and Erewhon.Application record members; a nullable
// member is always present and holds null when it has no value.

/** Internal row id of a persisted row. */
export type Id = number;

/** Calendar date, `yyyy-MM-dd`. */
export type IsoDate = string;

/** Server-local timestamp without a zone, `yyyy-MM-ddTHH:mm:ss` with optional fractional seconds. */
export type IsoDateTime = string;

/** Binary content encoded as base64 text. */
export type Base64 = string;

/** Role of a session user. */
export type SessionRole = 'none' | 'client' | 'agent' | 'administrator';

/** Area of the application a user logs on to. */
export type LogonArea = 'shop' | 'admin';

/** Item kind selected in the shop search: all items, retail items only or tender items only. */
export type ShopItemKind = 'all' | 'retail' | 'tender';

/** Status of a tender. */
export type TenderStatus = 'open' | 'closed' | 'successful';

/** Kind of change of a persisted row. */
export type ChangeKind = 'added' | 'updated' | 'deleted';

/** The `showFor` query value of GET /api/app/admin/sales. */
export type SalesShowFor = 'agent' | 'client' | 'company';

/** Kind of an error outcome. */
export type ErrorOutcomeKind = 'exception' | 'lock' | 'deadlock';

/** The `{type}` route segment of /api/app/edit-locks/{type}/{id}. */
export type EditLockType =
  | 'Agent'
  | 'Client'
  | 'Company'
  | 'RetailItem'
  | 'TenderItem'
  | 'ItemCategory'
  | 'CommissionRate'
  | 'Country'
  | 'Region';

/** Domain class name of a changed row, as carried by the EntityChanged event. */
export type EntityTypeName =
  | 'Company'
  | 'Agent'
  | 'Client'
  | 'Country'
  | 'Region'
  | 'ItemCategory'
  | 'CommissionRate'
  | 'RetailItem'
  | 'TenderItem'
  | 'RetailSale'
  | 'TenderSale'
  | 'Tender';

/** Outcome of a create, update, delete, lock, unlock or checkout request. */
export interface OperationResult {
  succeeded: boolean;
  /** Error texts in the order they were raised. */
  errors: string[];
  /** Warning texts in the order they were raised. */
  warnings: string[];
  /** Field of the first error, such as `FOCUS: Name`. */
  focusField: string | null;
  /** The errors joined with CR LF; empty when there are none. */
  fullErrorText: string;
  /** Internal row id of the created or changed row. */
  id: Id | null;
}

/** Body of a status 500 error response. */
export interface ErrorOutcome {
  kind: ErrorOutcomeKind;
  heading: string;
  body: string;
}

/** Body of GET /api/app/edit-locks/{type}/{id}. */
export interface SoftLockStatus {
  /** Domain class name of the row. */
  entityType: string;
  id: Id;
  /** True when a session holds the row's soft edit lock, the caller's own included. */
  isLocked: boolean;
  /** True when the caller's session holds the row's soft edit lock. */
  isLockedByCaller: boolean;
  /** Name of the thing being edited. */
  thing: string;
  /** Text of the Yes/No prompt shown when the row is locked. */
  prompt: string;
  /** Title of the prompt. */
  title: string;
}

/** One row of GET /api/app/admin/documentation/candidates. */
export interface DocumentationCandidate {
  /** Heading text, agent name, or country name and region name. */
  text: string;
  /** 1 for a heading, 2 for an agent or region entry. */
  level: number;
  /** `Agent` or `Region` for an entry; null for a heading. */
  entityType: string | null;
  /** Row id of the agent or region; null for a heading. */
  id: Id | null;
}

/** One line of a session cart. */
export interface CartLineView {
  /** Internal id of the line's item. */
  itemId: Id;
  isTender: boolean;
  /** "Retail" or "Tender". */
  transaction: string;
  code: string;
  /** Current item name; empty when the item no longer exists. */
  itemName: string;
  /** Current item price; 0 when the item no longer exists. */
  itemPrice: number;
  offer: number;
}

/** Body of GET /api/app/shop/cart and POST /api/app/shop/cart/empty. */
export interface CartView {
  sessionId: string;
  /** Lines in ascending item-id order. */
  lines: CartLineView[];
  /** Tender offers plus the current prices of the retail items. */
  total: number;
  isEmpty: boolean;
  /** Number of cart lines. */
  size: number;
}

/** Confirmation of a successful checkout. */
export interface CheckoutReceipt {
  /** One line per checked-out cart line, in cart order. */
  lines: CheckoutReceiptLine[];
}

/** One row of a checkout confirmation. */
export interface CheckoutReceiptLine {
  /** Internal id of the line's item. */
  itemId: Id;
  /** "Retail" or "Tender". */
  transaction: string;
  code: string;
  itemName: string;
  itemPrice: number;
  offer: number;
  isTender: boolean;
}

/** Row id and name of an agent or client. */
export interface NameProjection {
  id: Id;
  name: string;
}

/** An agent with its address. */
export interface AgentProjection {
  id: Id;
  name: string;
  street: string;
  city: string;
  country: string;
  phone: string;
  fax: string;
  email: string;
  webSite: string;
}

/** A client with its address. */
export interface ClientProjection {
  id: Id;
  name: string;
  street: string;
  city: string;
  country: string;
  phone: string;
  fax: string;
  email: string;
  webSite: string;
}

/** The company with its address; body of GET /api/app/admin/company. */
export interface CompanyProjection {
  id: Id;
  name: string;
  street: string;
  city: string;
  country: string;
  phone: string;
  fax: string;
  email: string;
  webSite: string;
}

/** The sale of an item, embedded in {@link ItemProjection}. */
export interface SaleDetailProjection {
  id: Id;
  isTender: boolean;
  agentCommission: number;
  client: ClientProjection;
  /** Sale timestamp of a retail sale; null for a tender sale. */
  soldDate: IsoDateTime | null;
  /** Sale price of a retail sale; null for a tender sale. */
  price: number | null;
  /** Winning offer of a tender sale; null for a retail sale. */
  offer: number | null;
  /** Timestamp of the winning tender of a tender sale; null for a retail sale. */
  tenderDate: IsoDateTime | null;
}

/** A retail or tender item. */
export interface ItemProjection {
  id: Id;
  isTender: boolean;
  codeNumber: number;
  categoryPrefix: string | null;
  /** Category prefix followed by the code number. */
  code: string | null;
  name: string;
  description: string;
  itemCategoryId: Id;
  itemCategoryName: string | null;
  listedDate: IsoDate;
  photo: Base64 | null;
  /** Retail price, or the reserve price of a tender item. */
  price: number;
  regionId: Id | null;
  regionName: string | null;
  countryId: Id | null;
  countryName: string | null;
  agentId: Id | null;
  agent: AgentProjection | null;
  /** Closure date of a tender item; null for a retail item. */
  closureDate: IsoDate | null;
  isSold: boolean;
  sale: SaleDetailProjection | null;
  /** True for a tender item holding at least one tender. */
  hasTenders: boolean;
}

/** A tender on a tender item. */
export interface TenderProjection {
  id: Id;
  offer: number;
  timestamp: IsoDateTime;
  client: ClientProjection;
  tenderItem: ItemProjection;
  /** Id of the tender sale this tender won; null when it won none. */
  tenderSaleId: Id | null;
}

/** One row of a sales list. */
export interface SalesRowProjection {
  saleId: Id;
  isTender: boolean;
  clientId: Id;
  clientName: string;
  itemId: Id;
  itemCode: string | null;
  itemName: string;
  itemPrice: number;
  saleDate: IsoDate | null;
  /** Sale price of a retail sale; null for a tender sale. */
  salePrice: number | null;
  /** Winning offer of a tender sale; null for a retail sale. */
  offer: number | null;
  agentCommission: number;
  agentId: Id | null;
}

/** Retail, tender and commission totals of a sales list. */
export interface SalesTotalsProjection {
  retailTotal: number;
  tenderTotal: number;
  commissionTotal: number;
}

/** Body of GET /api/app/logon-options. */
export interface LogonOptionsResponse {
  /** True when a company exists; false when the load-data prompt is shown instead. */
  dataLoaded: boolean;
  area: LogonArea;
  /** Caption of the user option: "Client" in the shop area, "Agent" in the admin area. */
  userOptionCaption: string;
  administratorEnabled: boolean;
  administratorSelected: boolean;
  /** Clients (shop area) or agents (admin area) in name order. */
  users: NameProjection[];
  version: string;
}

/** Body of POST /api/app/sessions. */
export interface CreateSessionRequest {
  area: LogonArea;
  /** True when the Administrator option is selected; read in the admin area only. */
  administrator: boolean;
  /** Selected user name, untrimmed; null or empty when no user is selected. */
  userName: string | null;
}

/** Response of POST /api/app/sessions. */
export interface CreateSessionResponse {
  succeeded: boolean;
  /** Value of the X-Erewhon-Session header and of the hub `session` query; null on failure. */
  sessionId: string | null;
  area: LogonArea;
  role: SessionRole;
  agentId: Id | null;
  clientId: Id | null;
  /** Validated user code: the company name for the Administrator option, otherwise the trimmed user name. */
  userName: string | null;
  /** Title of the message shown for a rejected user; null on success. */
  messageTitle: string | null;
  /** Text of the message shown for a rejected user; null on success. */
  message: string | null;
}

/** Response of POST /api/app/setup/load-data, status 200 or 409. */
export interface LoadDataResponse {
  /** True when the data was loaded; false when the request was refused. */
  loaded: boolean;
  tenderSalesBuilt: number;
  /** Loader console lines in the order written. */
  outputLines: string[];
}

/** Response of GET /api/app/about. */
export interface AboutResponse {
  version: string;
}

/** Response of POST /api/app/edit-locks/{type}/{id}/save-request. */
export interface SaveRequestResponse {
  /** True when the SaveOutstandingChanges event was delivered to the session holding the lock. */
  sent: boolean;
}

/** One region entry of the shop search plinth. */
export interface ShopRegionOption {
  id: Id;
  name: string;
}

/** One country entry of the shop search plinth, with its regions. */
export interface ShopCountryOption {
  id: Id;
  name: string;
  regions: ShopRegionOption[];
}

/** One item category entry of the shop search plinth. */
export interface ShopCategoryOption {
  id: Id;
  name: string;
  prefix: string;
}

/** Body of POST /api/app/shop/search; also the current search of GET /api/app/shop/reference. */
export interface ShopSearchRequest {
  countryId: Id | null;
  regionId: Id | null;
  itemCategoryId: Id | null;
  itemKind: ShopItemKind;
  /** Lowest price returned, inclusive; 0 for no lower bound. */
  priceLow: number;
  /** Highest price returned, inclusive; 0 for no upper bound. */
  priceHigh: number;
  includeClosedTenders: boolean;
}

/** Response of GET /api/app/shop/reference. */
export interface ShopReferenceResponse {
  countries: ShopCountryOption[];
  categories: ShopCategoryOption[];
  currentSearch: ShopSearchRequest;
}

/** One row of the shop search results. */
export interface ShopSearchRow {
  id: Id;
  isTender: boolean;
  code: string | null;
  name: string;
  listedDate: IsoDate;
  /** Retail price, or the reserve price of a tender item. */
  price: number;
  /** True for a tender item whose closure date is before the server date. */
  isClosed: boolean;
}

/** Response of POST /api/app/shop/search. */
export interface ShopSearchResponse {
  /** Rows in code-number order. */
  items: ShopSearchRow[];
}

/** Body of POST /api/app/shop/cart/items. */
export interface AddCartItemRequest {
  /** Item to buy or bid on; null when no item is selected. */
  itemId: Id | null;
  /** Offer typed for the item, the bid of a tender line. */
  offer: number;
}

/** Body of POST /api/app/shop/cart/remove. */
export interface RemoveCartItemRequest {
  /** Item id of the selected cart line; null when no line is selected. */
  itemId: Id | null;
}

/** Response of POST /api/app/shop/cart/items and POST /api/app/shop/cart/remove. */
export interface CartOperationResponse {
  result: OperationResult;
  cart: CartView;
}

/** Response of POST /api/app/shop/checkout. */
export interface CheckoutResponse {
  result: OperationResult;
  /** Confirmation of a successful checkout; null when the checkout failed. */
  receipt: CheckoutReceipt | null;
}

/** One tender the session client has placed on an item. */
export interface OwnTenderOffer {
  id: Id;
  offer: number;
  dateTendered: IsoDateTime;
}

/** Response of GET /api/app/items/{id}. */
export interface ItemDetailsResponse {
  /** The item, with `photo` null; the photo is served by GET /api/app/items/{id}/photo. */
  item: ItemProjection;
  hasPhoto: boolean;
  /** True for a tender item whose closure date is before the server date. */
  isClosed: boolean;
  /** The session client's tenders on the item, highest offer first, then by tender time. */
  ownTenders: OwnTenderOffer[];
}

/** Address fields of the agent, client and company edit dialogs, sent as typed. */
export interface AddressFields {
  street: string | null;
  city: string | null;
  country: string | null;
  phone: string | null;
  fax: string | null;
  email: string | null;
  webSite: string | null;
}

/** Body of POST /api/app/admin/agents and PUT /api/app/admin/agents/{id}. */
export interface AgentRequest {
  name: string | null;
  address: AddressFields | null;
}

/** One commission rate an agent holds. */
export interface AgentRateRow {
  commissionRateId: Id;
  itemCategoryId: Id;
  itemCategoryName: string;
  percentage: number;
}

/** Response of GET /api/app/admin/agents/{id}. */
export interface AgentDetailsResponse {
  agent: AgentProjection;
  /** The agent's items by category, prefix and code number, each with `photo` null. */
  items: ItemProjection[];
  /** The agent's rates in category name order. */
  commissionRates: AgentRateRow[];
}

/** One commission rate of a category with the agents using it. */
export interface CommissionRateAgents {
  commissionRateId: Id;
  percentage: number;
  /** Agents using the rate, in name order. */
  agents: NameProjection[];
}

/** Response of GET /api/app/admin/agent-commission-rates?category={id}. */
export interface AgentCommissionRatesResponse {
  itemCategoryId: Id;
  /** The category's rates in percentage order. */
  rates: CommissionRateAgents[];
  allAgents: NameProjection[];
}

/** Body of PUT /api/app/admin/agent-commission-rates. */
export interface ReplaceRateAgentsRequest {
  commissionRateId: Id;
  /** The complete set of agents the rate is to have. */
  agentIds: Id[];
}

/** Body of POST /api/app/admin/clients and PUT /api/app/admin/clients/{id}. */
export interface ClientRequest {
  name: string | null;
  address: AddressFields | null;
}

/** Response of GET /api/app/admin/clients/{id}. */
export interface ClientDetailsResponse {
  client: ClientProjection;
  retailSales: SalesRowProjection[];
  tenderSales: SalesRowProjection[];
}

/** Body of PUT /api/app/admin/company. */
export interface CompanyRequest {
  name: string | null;
  address: AddressFields | null;
}

/** Body of POST /api/app/admin/commission-rates and PUT /api/app/admin/commission-rates/{id}. */
export interface CommissionRateRequest {
  itemCategoryId: Id | null;
  percentage: number;
  /** Agents to hold the rate; null creates it with none (POST) or keeps its agents (PUT). */
  agentIds: Id[] | null;
}

/** One row of GET /api/app/admin/commission-rates and the body of GET /api/app/admin/commission-rates/{id}. */
export interface CommissionRateRow {
  id: Id;
  itemCategoryId: Id;
  itemCategoryName: string;
  percentage: number;
  agents: NameProjection[];
}

/** Body of POST /api/app/admin/item-categories and PUT /api/app/admin/item-categories/{id}. */
export interface ItemCategoryRequest {
  name: string | null;
  prefix: string | null;
  description: string | null;
}

/** One row of GET /api/app/admin/item-categories and the body of GET /api/app/admin/item-categories/{id}. */
export interface ItemCategoryRow {
  id: Id;
  name: string;
  prefix: string;
  description: string;
}

/** Body of POST /api/app/admin/countries and PUT /api/app/admin/countries/{id}. */
export interface CountryRequest {
  name: string | null;
}

/** One row of GET /api/app/admin/regions and the body of GET /api/app/admin/regions/{id}. */
export interface RegionRow {
  id: Id;
  name: string;
  countryId: Id;
  countryName: string;
}

/** One row of GET /api/app/admin/countries and the body of GET /api/app/admin/countries/{id}. */
export interface CountryRow {
  id: Id;
  name: string;
  regions: RegionRow[];
}

/** Body of POST /api/app/admin/regions and PUT /api/app/admin/regions/{id}. */
export interface RegionRequest {
  name: string | null;
  countryId: Id | null;
}

/** Body of POST /api/app/admin/items/search. */
export interface AdminItemSearchRequest {
  agentId: Id | null;
  clientId: Id | null;
  countryId: Id | null;
  regionId: Id | null;
  itemCategoryId: Id | null;
  onlyRetailItems: boolean;
  onlyTenderItems: boolean;
  /** Lowest price listed, inclusive; 0 for no lower bound. */
  priceLow: number;
  /** Highest price listed, inclusive; 0 for no upper bound. */
  priceHigh: number;
  includeSoldItems: boolean;
  includeClosedTenders: boolean;
}

/**
 * One item row of POST /api/app/admin/items/search and GET /api/app/admin/items/by-category; the body of
 * GET /api/app/admin/items/find.
 */
export interface ItemListRow {
  /** The item, with `photo` null. */
  item: ItemProjection;
  /** True for a tender item whose closure date is before the server date. */
  isClosed: boolean;
}

/** Response of GET /api/app/admin/items/by-category. */
export interface ItemsByCategoryResponse {
  itemCategoryId: Id;
  items: ItemListRow[];
}

/** Body of POST /api/app/admin/retail-items and PUT /api/app/admin/retail-items/{id}. */
export interface RetailItemRequest {
  /** 0 allocates the company's next code number. */
  codeNumber: number;
  name: string | null;
  description: string | null;
  /** On POST, null gives the server date; on PUT, null keeps the stored listed date. A date replaces it. */
  listedDate: IsoDate | null;
  /** Picture file content; null for no picture. */
  photoBase64: Base64 | null;
  price: number;
  agentId: Id | null;
  itemCategoryId: Id | null;
  regionId: Id | null;
}

/** Body of POST /api/app/admin/tender-items and PUT /api/app/admin/tender-items/{id}. */
export interface TenderItemRequest {
  /** 0 allocates the company's next code number. */
  codeNumber: number;
  name: string | null;
  description: string | null;
  /** On POST, null gives the server date; on PUT, null keeps the stored listed date. A date replaces it. */
  listedDate: IsoDate | null;
  /** Picture file content; null for no picture. */
  photoBase64: Base64 | null;
  /** Reserve price. */
  price: number;
  agentId: Id | null;
  itemCategoryId: Id | null;
  regionId: Id | null;
  closureDate: IsoDate | null;
}

/** Body of POST /api/app/admin/tenders/search. */
export interface AdminTenderSearchRequest {
  agentId: Id | null;
  clientId: Id | null;
  /** Tender item whose tenders are listed; null for every tender item. */
  itemId: Id | null;
  includeOpenTenders: boolean;
  includeClosedTenders: boolean;
  includeSuccessfulTenders: boolean;
}

/** One row of POST /api/app/admin/tenders/search. */
export interface TenderListRow {
  id: Id;
  /** Status at the server date. */
  status: TenderStatus;
  /** Date part of `dateTendered`. */
  tenderDate: IsoDate;
  dateTendered: IsoDateTime;
  closureDate: IsoDate;
  clientId: Id;
  clientName: string;
  tenderItemId: Id;
  itemCode: string | null;
  itemName: string;
  /** Reserve price of the tender item. */
  itemPrice: number;
  offer: number;
  agentName: string | null;
  /** True when the item's tender sale awards it to this tender. */
  isWinning: boolean;
}

/** Response of GET /api/app/admin/tenders/{id}. */
export interface TenderDetailsResponse {
  tender: TenderProjection;
  /** Status at the server date. */
  status: TenderStatus;
  isWinning: boolean;
  itemSold: boolean;
  /** Client the item was sold to; null when the item is unsold. */
  winnerClientName: string | null;
  /** Offer that won the item; null when the item is unsold. */
  winningOffer: number | null;
}

/** Response of GET /api/app/admin/sales. */
export interface SalesListResponse {
  rows: SalesRowProjection[];
  totals: SalesTotalsProjection;
}

/** Response of GET /api/app/admin/tenders/closure-schedule. */
export interface ClosureScheduleStateResponse {
  /** Database server date. */
  serverDate: IsoDate;
  running: boolean;
  /** Date the next scheduled closure uses; null when no schedule is running. */
  date: IsoDate | null;
  /** Minutes between scheduled closures; null when no schedule is running. */
  minutes: number | null;
  /** Seconds left before the next scheduled closure; null when no schedule is running. */
  secondsToGo: number | null;
}

/** Body of POST /api/app/admin/tenders/close-now. */
export interface CloseNowRequest {
  date: IsoDate;
}

/** Response of POST /api/app/admin/tenders/close-now. */
export interface CloseNowResponse {
  /** Number of tender items awarded. */
  numClosed: number;
}

/** Body of POST /api/app/admin/tenders/closure-schedule/start. */
export interface StartClosureScheduleRequest {
  date: IsoDate;
  minutes: number;
}

/** Response of POST /api/app/admin/tenders/closure-schedule/start. */
export interface StartClosureScheduleResponse {
  started: boolean;
  /** Minutes multiplied by 60. */
  secondsToGo: number;
}

/** Response of POST /api/app/admin/tenders/closure-schedule/stop. */
export interface StopClosureScheduleResponse {
  /** True when a running schedule was removed; false when none was running. */
  stopped: boolean;
}

/** Response of GET /api/app/admin/documentation/{type}/{id}. */
export interface DocumentationDetailsResponse {
  /** `Agent` or `Region`. */
  entityType: string;
  id: Id;
  name: string;
  /** Text shown as "Document as Text". */
  text: string;
  /** RTF source shown as "Document as RTF". */
  rtf: string;
}

/** Payload of the EntityChanged hub event, sent to every session. */
export interface EntityChangedMessage {
  entityType: EntityTypeName;
  id: Id;
  kind: ChangeKind;
}

/** Payload of the CartItemDeleted hub event: an item removed from the receiving session's cart. */
export interface CartItemDeletedMessage {
  itemId: Id;
}

/** Payload of the SaveOutstandingChanges hub event, sent to the session holding a row's soft edit lock. */
export interface SaveOutstandingChangesMessage {
  entityType: string;
  id: Id;
}

/** Payload of the ClosureTick hub event, sent to the session running a closure schedule. */
export interface ClosureTickMessage {
  secondsToGo: number;
}

/** Payload of the ClosureCompleted hub event, sent to the session running a closure schedule. */
export interface ClosureCompletedMessage {
  /** Date the closure ran at. */
  date: IsoDate;
  /** Number of tender items awarded. */
  numClosed: number;
}

/** Payload of each /hubs/erewhon event, keyed by event name. */
export interface RealtimeEvents {
  EntityChanged: EntityChangedMessage;
  CartItemDeleted: CartItemDeletedMessage;
  SaveOutstandingChanges: SaveOutstandingChangesMessage;
  ClosureTick: ClosureTickMessage;
  ClosureCompleted: ClosureCompletedMessage;
}

/** Name of a /hubs/erewhon event. */
export type RealtimeEventName = keyof RealtimeEvents;
