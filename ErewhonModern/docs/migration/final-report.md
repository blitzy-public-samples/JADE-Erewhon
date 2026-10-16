# Final report

This is the closing record of the migration of the Erewhon store from JADE to ErewhonModern. It lists, for later decision by the owners, every JADE quirk that was reproduced rather than fixed, every equivalence exception, every point where the JADE source overrode the request, the result of the inherited `CardSchema` start-up check, and the open risks. IDs, outcome wording and test tokens are those of `docs/migration/behaviour-catalogue.md`, which governs wherever another record differs. Reasons are recorded only in `docs/migration/decision-log.md` and are cited here by D-ID. Paths are relative to `ErewhonModern/`.

Counts at this report:

- **Features:** 22 (F-001 to F-022): 16 implemented, 5 replaced and 1 subsumed by PostgreSQL (F-019), per `docs/migration/feature-traceability.md`.
- **JADE unit tests:** 214 (127 model, 24 view, 38 REST, 25 SOAP): 213 executable ports and 1 row listed with EE-13 (`TestViewApp::testIsWebShopApp`).
- **Reproduced JADE quirks:** 34 (Q-01 to Q-34).
- **Equivalence exceptions:** 17 (EE-01 to EE-17). EE-01 is the only pre-approved one.
- **Request points the source overrode:** 16 (C-01 to C-16).
- **Inherited `CardSchema` start-up behaviour reproduced:** none.

## Machine-read format

`TraceabilityCoverageTests` reads this file. The format is fixed by D-48:

- Three tables carry IDs. Each is located by its exact header row:
  - quirks: `| ID | Source | Behaviour reproduced | Proving test |`, with IDs `Q-NN`;
  - equivalence exceptions: `| ID | JADE behaviour | What was done instead | Decision |`, with IDs `EE-NN`;
  - request points: `| ID | Request states | Source shows | Behaviour kept | Decision |`, with IDs `C-NN`.

  Every other table in this file has a first header cell other than `ID`.
- A table line starts with `|`, and each table row is one line. A line break inside a cell is written `<br>`, never a raw newline. A `|` inside a cell is escaped as `\|`.
- IDs are zero-padded and match `^(Q|EE|C)-\d{2}$`. The first cell holds only the ID. Each ID appears once, in ascending order, and the Q, EE and C ID sets equal those of `docs/migration/behaviour-catalogue.md`.
- JADE sources are cited in the catalogue's syntax `[Erewhon/<file>:<locator>]`, one file per bracket. A Source cell holds one or more such citations separated by a space.
- A Proving test cell holds backticked test tokens in the catalogue's syntax, separated by `, `: an xUnit test as `Class.method`, or `Class.*` for every method of that class; a catalogue test always with its class (`BehaviourCatalogueTests.B07_ItemPriceZeroIsRejected`); a React test as its file path (`web/src/shop/ItemDetails.test.tsx`). The tokens are those of the catalogue row with the same Q-ID.
- Decision cells:
  - C table: exactly one D-ID, the decision that settles the contradiction. It equals the D-ID that opens the catalogue's Decision cell and the D-ID of the `## Request contradictions` table of `docs/migration/decision-log.md`.
  - EE table: one D-ID, the word `pre-approved` (EE-01 only), or `—` when no decision applies.
- Every `D-` ID in this file exists in the decision table of `docs/migration/decision-log.md`.
- Messages are written as in the catalogue: the key with its `$` prefix, followed by the verbatim locale-5129 text in double quotes.

## Reproduced JADE quirks

Each quirk is reproduced, not fixed, and stays as listed until the owners decide on it.

| ID | Source | Behaviour reproduced | Proving test |
|---|---|---|---|
| Q-01 | [Erewhon/ErewhonModelSchema.scm:L11444-11504] | `$ItemCategoryPrefixInvalid` says "Category Prefix must be exactly 4 characters", but 4 or 5 characters are accepted (`prefix` is `String[5]` and only a length below 4 is rejected) | `BehaviourCatalogueTests.B09_FiveCharacterPrefix` |
| Q-02 | [Erewhon/ErewhonModelSchema.scm:L11804] | The request says the item-price message "greater than 0" is wrong because 0 is accepted. Under JADE null semantics 0 is rejected, so the message is accurate (C-03) | `BehaviourCatalogueTests.B07_ItemPriceZeroIsRejected` |
| Q-03 | [Erewhon/ErewhonModelSchema.scm:L6670-6708] | Closure stops at the first sold or future-dated item instead of skipping it | `BehaviourCatalogueTests.B34_ClosureOrderStopAndTieBreak` |
| Q-04 | [Erewhon/ErewhonModelSchema.scm:L62-64] | `DecimalPercentPrecision = 6` differs from `percentage` `Decimal[5,2]`; the attribute governs | `BehaviourCatalogueTests.B14_PercentageStoredWithTwoDecimals` |
| Q-05 | [Erewhon/ErewhonModelSchema.scm:L12804] | `$SaleAgentCommissionInvalid` says "Agent commission must be greater than 0", but 0 is accepted (`agentCommission < 0`) | `BehaviourCatalogueTests.B19_ZeroCommissionAccepted` |
| Q-06 | [Erewhon/ErewhonModelSchema.scm:L12990] | A retail sale price of 0 reports `$RetailSalePriceRequired` "A price for the retail item is required", not the invalid-price message | `TestRetailSale.testInvalidPrice` |
| Q-07 | [Erewhon/ErewhonModelSchema.scm:L6322-6330] | Commission falls back to the category's lowest rate when the agent has none for the category | `BehaviourCatalogueTests.B29_CommissionFallbackAndRounding` |
| Q-08 | [Erewhon/ErewhonModelSchema.scm:L9007-9014] | The stale-edit check is dormant | `BehaviourCatalogueTests.B39_StaleEditLastWriterWins` |
| Q-09 | [Erewhon/ErewhonViewSchema.scm:L8737] | The save request has no effect, because no form subscribes to `Erewhon_Event_SaveOutstandingChanges` | `NotificationTests.SaveRequestReachesEditorWithoutEffect` |
| Q-10 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2652-2731] | REST create with a duplicate name returns 400, not 409 | `RestCatalogueTests.B60_RestDuplicateAgentIs400` |
| Q-11 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2328-2651] | REST `getItemByCode`/`getSale` reject trimmed prefixes longer than 4. `getItemByCode` ignores the prefix in its lookup; `getSale` looks up the untrimmed prefix. `getAgent` does not trim the name; `getClient` does | `RestTestGetItemByCode.*`, `BehaviourCatalogueTests.B09_FiveCharacterPrefix`, `RestCatalogueTests.B52_RestLookupTrimming` |
| Q-12 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L1139-1146] | `Error_Unauthorized` (403) is declared and never returned; so is `Error_Forbidden` (403) | `SoapCatalogueTests.B51_ErrorUnauthorizedIs403` |
| Q-13 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L6646-6681] | `getPhoto` returns the photo of the last retail-sale item that has one, overwriting earlier ones | `TestGetPhoto.testClientWithPhoto`, `SoapCatalogueTests.B47_PhotoOfLastRetailSaleWithPhoto` |
| Q-14 | [Erewhon/ErewhonModelSchema.scm:L13870-13949] | `$TenderAlreadyPlaced` is added once per matching tender | `TestTender.testDuplicateTender`, `BehaviourCatalogueTests.B30_DuplicateMessagesPerMatch` |
| Q-15 | [Erewhon/ErewhonViewSchema.scm:L18601-18644] [Erewhon/ErewhonModelSchema.scm:L13399-13406] | `$AlreadyTenderedAHigherOrSameOffer` is shown once per own tender ≥ the new offer, and only when the entered offer is not null: a zero or empty offer (JADE null, D-17) skips the check and gets the cart's below-reserve error instead (D-58) | `web/src/shop/ItemDetails.test.tsx` |
| Q-16 | [Erewhon/ErewhonModelSchema.scm:L13405-13436] | A cart tender's timestamp is set when the line is added, not at checkout | `BehaviourCatalogueTests.B24_TenderLineTimestampAtAdd` |
| Q-17 | [Erewhon/ErewhonModelSchema.scm:memberKeyDefinitions] | (dateTendered, offer) is unique company-wide (`TendersByTimeOfferDict` on `Company.allTenders`), so identical pairs on different items collide as a database-level failure | `BehaviourCatalogueTests.B30_TenderTimeOfferCollision` |
| Q-18 | [Erewhon/ErewhonModelSchema.scm:L8427-8509] | The seed purge commits before the reload transaction begins | `SeedEquivalenceTests.PurgeCommittedSeparately` |
| Q-19 | [Erewhon/ErewhonModelSchema.scm:L8288] | `loadCategories` prints the agent count in `$CategoriesLoadedInMS` | `SeedEquivalenceTests.ConsoleOutput` |
| Q-20 | [Erewhon/ErewhonModelSchema.scm:L8853-8927] | The loader counts unpersisted lines; 60 tenders are silently rejected | `SeedEquivalenceTests.Counts` |
| Q-21 | [Erewhon/ErewhonModelSchema.scm:L14363-14390] | `ItemSearch.initialize` does not reset `includeItemsInCart`, `myAgent` or `myClient` | `BehaviourCatalogueTests.B36_InitializeKeepsAgentClientAndCartFlag` |
| Q-22 | [Erewhon/ErewhonBaseSchema.scm:L728-762] | GIF is not a valid picture | `BehaviourCatalogueTests.B06_PictureValidity` |
| Q-23 | [Erewhon/ErewhonModelSchema.scm:L11164-11201] | Company names are not checked for uniqueness | `BehaviourCatalogueTests.B13_CompanyNameNotCheckedForUniqueness` |
| Q-24 | [Erewhon/ErewhonViewSchema.scm:L12884-12915] | An agent's tender Delete in the list silently does nothing | `web/src/admin/tenders/TendersListPage.test.tsx` |
| Q-25 | [Erewhon/ErewhonModelSchema.scm:L14350-14479] | A search price bound of 0 means no bound; `Ex_500002_Min_higher_than_max` fires only when both bounds are non-zero | `BehaviourCatalogueTests.B35_ItemSearchFilters` |
| Q-26 | [Erewhon/ErewhonModelSchema.scm:L11886-11890] | Item numbers are unique company-wide, not per category | `TestCompany.testGetNextCode` |
| Q-27 | [Erewhon/ErewhonModelSchema.scm:L6670-6708, L7208-7223] | Closure at a date processes items whose closure date equals that date, while `isClosed` still reports them open that day, so start-up closure awards items on their closure day | `BehaviourCatalogueTests.B33_TenderStatus`, `BehaviourCatalogueTests.B34_ClosureOrderStopAndTieBreak` |
| Q-28 | [Erewhon/ErewhonModelSchema.scm:L11727-11729, L11892-11895] | The code number is allocated, and its lock released, before the item is written, in Own and Ambient mode alike, so two concurrent creates can draw the same number; the later insert fails at the database | `ConcurrencyTests.Q28_AllocationWindowGivesDatabaseLevelFailure` |
| Q-29 | [Erewhon/ErewhonModelSchema.scm:L13420-13425, L13669-13671] | A cart retail line keeps the price copied when it was added, while the cart total reads the current price, so a repriced item sells at its old price | `BehaviourCatalogueTests.B24_PriceChangeBetweenAddAndCheckout` |
| Q-30 | [Erewhon/ErewhonModelSchema.scm:L6666] | `Company.agentExists` reads `app.myCompany`, not `self`, so it answers for the bound company whichever company it is called on | `BehaviourCatalogueTests.B73_AgentExistsReadsBoundCompany` |
| Q-31 | [Erewhon/ErewhonViewSchema.scm:L8730-8739] | Opening an editor prompts even when the opener's own session holds the soft lock | `AppEndpointRuleTests.OpeningOwnLockedRowReportsLock`, `web/src/admin/useEditLock.test.tsx` |
| Q-32 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L155-172, L2158-2177] | `DTOItem` declares `categoryPrefix` (`String[5]`) and `photo` (`Binary`) but `populateFromObject` never sets them, so every REST item response, and the `item`/`tenderItem` inside sale and tender responses, carries `categoryPrefix` null and `photo` null | `RestCatalogueTests.Q32_ItemCategoryPrefixAndPhotoNotPopulated` |
| Q-33 | [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2179-2194, L2196-2214, L2217-2260, L2261-2290] | `DTOTenderItem.populateFromObject` creates the declared base `DTOSaleDetail` for `sale`, so a sold tender item's `sale` holds only `guid`, `client` and `agentCommission`, and `DTOTenderSaleDetail` is never produced; a retail item's `sale` is a `DTORetailSaleDetail` with `soldDate` and `price`. `DTOSale.item` is a base `DTOItem` (no closure date, no sale). `DTOTenderSale.offer` is never set (0), and the offer appears in `tender.offer` | `RestCatalogueTests.Q33_TenderSaleDetailShapes` |
| Q-34 | [Erewhon/ErewhonModelSchema.scm:L13578-13605] [Erewhon/ErewhonModelSchema.scm:L12952-12963] [Erewhon/ErewhonModelSchema.scm:L13559-13577, L9275-9285] | A retail cart line keeps the `dateSold` its first checkout attempt set: `ShoppingCartTA.doPreValidate` pre-validates each line's own `RetailSaleTA`, which sets `dateSold` only when it is null or invalid, and neither the abort cleanup nor a later attempt clears it, so a checkout that fails at T1 and succeeds at T2 records the retail sale at T1 | `BehaviourCatalogueTests.B27_RetryKeepsFirstSoldTimestamp` |

## Equivalence exceptions

EE-01, the storage engine and its error text, is the only pre-approved exception. Each of EE-02 to EE-17 went through the stop-record-continue procedure: work on the case stopped; the JADE behaviour, why it cannot be matched or tested, and what was done instead were recorded in `docs/migration/behaviour-catalogue.md`; then work continued.

| ID | JADE behaviour | What was done instead | Decision |
|---|---|---|---|
| EE-01 | Database-level failures (key already used, 1310; object locked) show JADE error codes and text | The same web exception body (`$BaseExceptionBodyWeb`) with the PostgreSQL SQLSTATE and message as error code and text | pre-approved |
| EE-02 | `getOidString` returns `classNumber.instanceId` | `ObjectIdentity` = `<JADE class number>.<row id>` (e.g. `5774.3` for an agent, `5781.<id>` for a region); tests compare with the same function | D-21 |
| EE-03 | `ErewhonException` text (`exObj.text`) comes from the JADE runtime, not the source | `$LockNotObtainedException` "A lock could not be obtained on the specified object" for 500001; for 500003, 500004 and 500005 the part of the `ErewhonExceptions` constant name after `Ex_<number>_`, with underscores turned into spaces (e.g. "Company already exists") | D-22 |
| EE-04 | `foreach ... reversed` over a dictionary whose key changes during iteration (tender closure) | Key-positioned iteration: every due unsold item is processed in the dictionary order (D-09) | D-09 |
| EE-05 | `JadeRegex` (PCRE) `\w` and case folding | .NET `RegexOptions.ECMAScript \| IgnoreCase` (ASCII `\w`), anchored whole-string match | — |
| EE-06 | Lock waits and the 1-second timer run on wall-clock time | Same nominal values; tests use tolerance windows and shorten the waits through `Erewhon:Locks:TimeoutMs`, `Erewhon:Locks:AllocationTimeoutMs` and `Erewhon:Closure:TickMs` | D-28 |
| EE-07 | Desktop dialogs offer Retry/Cancel (`$BaseExceptionBody`, `$BaseLockMessageBody`) | The JADE web variants (`$BaseExceptionBodyWeb`, `$BaseLockMessageBodyWeb`, `$BaseDeadlockBodyWeb`), OK only | D-22 |
| EE-08 | `WebServiceTCP` (`jadehttp.tcp2`) and the RPC/encoded consumer proxy | HTTP SOAP 1.1 document/literal for both services; outcomes identical (D-05) | D-05 |
| EE-09 | `generateUuid(VariantDce).uuidAsString()` format | `Guid.NewGuid().ToString("B")`, matching the seed files (`{xxxxxxxx-...}`, lowercase) (D-32) | D-32 |
| EE-10 | The soft-lock owner is the JADE process OID | The session id (D-10) | D-10 |
| EE-11 | The loader's console `write` goes to the JADE output window | The same strings go to standard output and the Serilog log | D-114 |
| EE-12 | `TimeStamp.String` hour format (the only documented sample is 24-hour) | `HH:mm:ss` | D-54 |
| EE-13 | `TestViewApp::testIsWebShopApp` asserts the application is not the JADE WebShop application [Erewhon/ErewhonViewSchema.scm:L6747-6756] | Listed with this reason in the test inventory; the session scoping it guarded is covered by `TestViewApp.testGetClient` and `TestViewApp.testSetClient` (C-14) | D-27 |
| EE-14 | The lock waits come from the JADE INI file: `tryLockingObject` uses the server-defined `ServerTimeout` [Erewhon/ErewhonModelSchema.scm:L9683-9688]; `getNextCodeNumber` uses the process default [Erewhon/ErewhonModelSchema.scm:L11872], which no Erewhon schema sets with `setDefaultLockTimeout` | The documented defaults, 10,000 ms each, as two settings: `Erewhon:Locks:TimeoutMs` and `Erewhon:Locks:AllocationTimeoutMs` (D-08) | D-08 |
| EE-15 | `TestViewRetailSaleItem::testGetSearchResultString` and `TestViewTenderSaleItem::testGetSearchResultString` expect a string without the code cell [Erewhon/ErewhonViewSchema.scm:L7208, L7437] | The two React ports in `web/src/shop/ShopPage.test.tsx` keep the original inputs and assert the source string with the code cell; the matrix row records both the original and the asserted value (C-15, D-45) | D-45 |
| EE-16 | `Binary.ebsIsValidPicture` delegates to the runtime's `pictureType` [Erewhon/ErewhonBaseSchema.scm:L728-762] | Classification by leading signature only (B-06, D-31); `BehaviourCatalogueTests.B06_PictureValidity` pins every signature, GIF, random bytes and a truncated JPEG | D-31 |
| EE-17 | Error 1035 raised inside a REST or SOAP request: a value longer than its fixed-width `DTO*` field during deserialisation [Erewhon/ErehwhonExternalInterfacesSchema.scm:L107-147], or a scalar parameter at the bounded assignment the operation reaches [Erewhon/ErehwhonExternalInterfacesSchema.scm:L6700-6745] | REST: 500 with an empty body, as the D-40 failure path. SOAP: a fault carrying "String too long (1035)". Branches before a scalar assignment, such as the 404 lookup, keep their outcome (B-22, D-41) | D-41 |

## Request points the source overrode

At each of these points the JADE source contradicts the request or does not confirm it. The source governs, so the behaviour stays as JADE has it.

| ID | Request states | Source shows | Behaviour kept | Decision |
|---|---|---|---|---|
| C-01 | Seed yields 2 categories | `Categories.txt` holds 3 records (DEST, ANQU, PROP), the last without a trailing newline; tender items reference Properties [Erewhon/DataFiles/Categories.txt:L1-3] | 3 categories | D-18 |
| C-02 | Seed yields 160 tenders | 60 of the 160 lines target 3 items whose shifted closure date is past, and fail with `$TenderItemAlreadyClosed`; the loader ignores the failure [Erewhon/ErewhonModelSchema.scm:L8853-8927] | 100 tenders persisted | D-18 |
| C-03 | Item price 0 is accepted | `price = null or price < 0` treats 0 as null [Erewhon/ErewhonModelSchema.scm:L11804] | 0 rejected | D-17 |
| C-04 | Retail sale price 0 or more | 0 → `$RetailSalePriceRequired`, pinned by `TestRetailSale.testInvalidPrice` [Erewhon/ErewhonModelSchema.scm:L12990, L19893-19943] | 0 rejected | D-17 |
| C-05 | REST duplicate → 409 | `postCreateAgent`/`postCreateClient` return 400 with errors; `HTTP_Conflict` is unused in REST; 409 is the SOAP Admin outcome [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2652-2731, L293-300] | 400 for REST, 409 for SOAP | D-04 |
| C-06 | Commission 0 when the agent has no rate | Falls back to the category's lowest rate [Erewhon/ErewhonModelSchema.scm:L6322-6330] | Fallback kept | D-19 |
| C-07 | A stale edit is refused | `checkEdition` always returns true and `expectedEdition` is never set [Erewhon/ErewhonModelSchema.scm:L9007-9014] | Check kept dormant | D-11 |
| C-08 | Company name unique | `CompanyTA` checks only name presence and the address [Erewhon/ErewhonModelSchema.scm:L11164-11201] | No uniqueness check | D-20 |
| C-09 | Self-documentation for agents, items and other documented objects | Only `Agent` and `Region` implement `DocumentInterface` [Erewhon/ErewhonModelSchema.scm:L3221, L3490] | Agents and regions | D-21 |
| C-10 | Answering Yes asks the editor to save | The event is caused, but no form subscribes to `Erewhon_Event_SaveOutstandingChanges` [Erewhon/ErewhonViewSchema.scm:L8737] | Event delivered, no effect | D-10 |
| C-11 | `Error_Unauthorized` 403 is reproduced | Declared and never returned, as is `Error_Forbidden` 403 [Erewhon/ErehwhonExternalInterfacesSchema.scm:L1139-1146] | Constants kept, never returned | D-05 |
| C-12 | Closure processes every due item | The JADE iterator's behaviour while each award changes the key is runtime-defined [Erewhon/ErewhonModelSchema.scm:L6670-6708] | Every due unsold item, in key order (EE-04) | D-09 |
| C-13 | `checkOffer` boundaries are business behaviour | `checkOffer` has no production caller [Erewhon/ErewhonViewSchema.scm:L4567, L4701] | Ported as the cart's offer check | D-34 |
| C-14 | View tests are business or display tests | `testIsWebShopApp` asserts JADE application identity [Erewhon/ErewhonViewSchema.scm:L6747-6756] | Listed with a reason (EE-13) | D-27 |
| C-15 | Ported view tests keep their expected values | Both `testGetSearchResultString` originals expect no code cell, but `getSearchResultString` emits the code after `$Buy`/`$Bid` [Erewhon/ErewhonViewSchema.scm:L4682-4686, L4807-4811] [Erewhon/ErewhonViewSchema.scm:L7208, L7437] | The source string governs; the two ports assert it (EE-15) | D-45 |
| C-16 | One company | The loader purges the existing company before creating its own and refuses only a survivor; `CompanyTA` creates further companies and `TestCompany.testCreateCompany` asserts it [Erewhon/ErewhonModelSchema.scm:L8448-8463, L7913-7930, L17352-17384] | One company only through the loader and the interactive-load gate | D-43 |

## Inherited CardSchema start-up behaviour

`ErewhonBaseSchemaApp`, `ErewhonBaseSchemaGlobal` and `ErewhonBaseSchemaSession` subclass the three root classes below [Erewhon/ErewhonBaseSchema.scm:L73-75]. The check covered what each class runs at application or session start.

| Class | Runs at application or session start | Seen by a user | Result |
|---|---|---|---|
| `CardSchema` (application class, subclass of `RootSchemaApp`) [Erewhon/CardSchema.scm:L860] | Declares no `initialize` or `create` method. `finalize` returns at once when no JADECare Start controller was instantiated (`app.myCnCntrl` null) [Erewhon/CardSchema.scm:L20188-20266]. `timerEvent` [Erewhon/CardSchema.scm:L20473] and `userNotification` [Erewhon/CardSchema.scm:L20492] react only to JADECare Start event tags, which Erewhon never raises. No Erewhon schema calls a `cn*` method; only commented-out instrumentation in `ModelTA` mentions them [Erewhon/ErewhonModelSchema.scm:L9134-9778] | No | Nothing reproduced |
| `GCardSchema` (global class) [Erewhon/CardSchema.scm:L987] | Server-information getters (`cnGet*`) [Erewhon/CardSchema.scm:L74633-74852, L74905-74908] and two utilities, `cnSetProfileStringServer` [Erewhon/CardSchema.scm:L74853-74862] and `cnStartBackgroundClient` [Erewhon/CardSchema.scm:L74863-74903]. Each runs only when called, no Erewhon schema calls any of them, and nothing runs implicitly (D-58) | No | Nothing reproduced |
| `CnSchemaSession` (session class) [Erewhon/CardSchema.scm:L992] | Declares no methods [Erewhon/CardSchema.scm:L12648-12650] | No | Nothing reproduced |

No application `initializeMethod` declared in the Erewhon `.ddx` files names a `CardSchema` method (D-52). The declarations are:

- `ErewhonDotNetConnection` → `initialize` (Joob, retired) [Erewhon/ErehwhonExternalInterfacesSchema.ddx:L41-65];
- `DataLoader` → `dataLoaderApp`, ported as the `seed` CLI verb [Erewhon/ErewhonModelSchema.ddx:L2333-2357];
- `ErewhonWebServiceConsumerSchema` and `SetConsumerDetailsThenRun` → `initialize` (consumer forms retired, D-06) [Erewhon/ErewhonWebServiceConsumerSchema.ddx:L3-27, L49-73].

Every other Erewhon application declares none.

Result: nothing a user sees runs at application or session start, so nothing is reproduced, and no `CardSchema` code or text is copied into `ErewhonModern/` (D-25).

## Open risks

- **No JADE runtime.** None was available, so every expected outcome comes from reading the JADE source and its tests, not from running them. Where the runtime alone defines a behaviour, the case is an equivalence exception (EE-03, EE-04, EE-05, EE-09, EE-12, EE-16, EE-17).
- **.NET 8 end of support.** Support for .NET 8 ends on 2026-11-10. The SDK, the runtime and every server package are on the .NET 8 line (D-24, D-25).
- **Closure iteration.** Tender closure under key change follows one reading of runtime-defined iterator behaviour: key-positioned iteration over every due unsold item, in dictionary order (EE-04, D-09, C-12).
- **Regex UCP.** Whether `JadeRegex` enables UCP is unknown. `EmailRule` matches in .NET ECMAScript mode with ASCII `\w`, so an e-mail address with non-ASCII word characters may be judged differently from JADE (EE-05).
- **Timing.** Lock waits and the 1-second closure timer run on wall-clock time, and tests check them with tolerance windows and shortened waits (EE-06, D-28).
- **Lock wait values.** The JADE values come from a deployment INI file that is not in the repository. Both waits use the documented 10,000 ms defaults (EE-14, D-08).
- **Runtime-defined outcomes.** Two outcomes are defined by the JADE runtime and are not reproduced from it: how a truncated or malformed picture with a recognised signature is classified (EE-16, D-31), and the HTTP outcome of error 1035 inside a REST or SOAP request (EE-17, D-41).
- **Vulnerable transitive packages.** svcutil's bootstrapper reports vulnerable `System.Security.Cryptography.*` packages at generation time (D-06). CoreWCF 1.9.1 and System.ServiceModel 8.1.2 also resolve them. The central transitive pins hold `System.Security.Cryptography.Xml` at 8.0.5 and `System.Security.Cryptography.Pkcs` at 8.0.2, and must be raised by hand for later servicing releases (D-50).
- **npm advisories.** `npm audit` reports two moderate advisories in react-router 6.30.6 (GHSA-wrjc-x8rr-h8h6, GHSA-337j-9hxr-rhxg). They are fixed only in react-router 7, which is outside the pinned majors (D-24).
- **In-process state.** Session, cart and closure-schedule state is lost on restart, and the API cannot scale out beyond one instance (D-14).

## Related records

- [Behaviour catalogue](behaviour-catalogue.md)
- [Decision log](decision-log.md)
- [Feature traceability](feature-traceability.md)
- [Traceability matrix](traceability-matrix.md)
- [Operations note](../operations.md)
- [REST API](../rest-api.md)
