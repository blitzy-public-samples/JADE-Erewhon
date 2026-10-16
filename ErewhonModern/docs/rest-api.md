# REST API reference

This document describes the 13 REST operations that replace the JADE `ErewhonRestService` (`JadeRestService`, hosted by the JADE application `ErewhonREST` [Erewhon/ErehwhonExternalInterfacesSchema.ddx:L77-152]). Every operation lives under `/api/rest/**` of the `Erewhon.Api` host, exchanges JSON over HTTP, and is anonymous: it takes no `X-Erewhon-Session` header and no credentials (D-13). The JADE source of every rule is `Erewhon/ErehwhonExternalInterfacesSchema.scm`; each operation section cites its method. Reasons for every decision cited here as `D-xx` are in `docs/migration/decision-log.md`; `B-`, `T-`, `C-`, `Q-` and `EE-` entries are in `docs/migration/behaviour-catalogue.md`.

Contents:

- [OpenAPI document](#openapi-document)
- [Conventions](#conventions)
- [Shared shapes](#shared-shapes)
- [Response population](#response-population)
- [Operations](#operations)
- [Errors common to every operation](#errors-common-to-every-operation)
- [Mapping to the JADE operations](#mapping-to-the-jade-operations)

## OpenAPI document

The `Erewhon.Api` host serves the OpenAPI document `rest` at [`/swagger/rest/swagger.json`](http://localhost:5080/swagger/rest/swagger.json) (title "Erewhon REST API", version `v1`) and the Swagger UI at [`/swagger`](http://localhost:5080/swagger), whatever address it listens on; both links use `http://localhost:5080`, the address of the development launch profile. Swashbuckle generates the document from the same seven controllers this reference describes, each marked `[ApiExplorerSettings(GroupName = "rest")]`, and from their XML doc comments, which state what each operation does (D-25). The document lists exactly the 13 paths and verbs of the [mapping table](#mapping-to-the-jade-operations). Each schema is named after the full name of its type, such as `Erewhon.Api.Rest.Contracts.ItemDto` for `ItemDto` and `Microsoft.AspNetCore.Mvc.ProblemDetails` for the problem details, and every `$ref` and discriminator mapping uses that name (D-168). Its schemas carry the [type discriminator](#conventions), read from the contracts' `[JsonPolymorphic]` and `[JsonDerivedType]` attributes: `ItemDto`, `SaleDto` and `SaleDetailDto` each declare an optional string property `type` and a discriminator `type` that maps `retailItem`, `tenderItem`, `retailSale`, `tenderSale`, `retailSaleDetail` and `tenderSaleDetail` to their derived schemas. Each derived schema extends its base with `allOf`, and a value declared with one of the three base types is `oneOf` the base and its derived schemas, so a base value without `type` matches the base schema (D-150).

## Conventions

**Routes and parameters.** Routes, verbs and JSON names are new; every JADE status code and body is kept per operation (D-03). A method a route does not serve, `HEAD` and `OPTIONS` included, is answered by the framework before any operation runs: status 405 with an `Allow` header that lists the route's methods, such as `Allow: GET, POST` for `/api/rest/agents` and `Allow: GET` for `/api/rest/agents/by-name`, and an empty body (D-124). Lookups take query parameters. Request bodies are JSON; send them with `Content-Type: application/json`. A body sent as `text/json`, or as any `application/` or `text/` type with the `+json` suffix such as `application/merge-patch+json`, is read the same way. A body is read as UTF-8 unless the `Content-Type` has a `charset` parameter, which must name UTF-8 or little-endian UTF-16 by a name .NET recognises, such as `utf-8`, `utf-16`, `utf-16le` or `unicode`. A request with a body in any other content type, in any other `charset`, such as `iso-8859-1`, `us-ascii`, `utf8` or `utf-16be`, or with no `Content-Type`, is answered by the framework before the operation runs: status 415 with the framework's problem-details JSON (`type`, `title`, `status`, `traceId`). A request sent with `Content-Length: 0`, or with neither `Content-Length` nor `Transfer-Encoding`, has no body and is never answered 415, whatever its content type or `charset`; each POST operation reads it as its section describes. A chunked request (`Transfer-Encoding: chunked`) has a body even when it sends no data; with no data it gets the binding 400 below when its content type and `charset` are accepted, and 415 otherwise.

**Request binding.** A query parameter declared as an integer (`codeNumber`, `minPrice`, `maxPrice`) that is absent binds as `0`, and an absent string parameter is read as empty; both then reach the operation's own checks. A string query parameter is read as sent, so a value made only of white space other than spaces, for example a tab, a line feed or a no-break space, reaches the operation's trimming and checks unchanged: `codePrefix=%09` passes the prefix check of `getItemByCode`, which trims only spaces (D-161). A value that cannot be bound to its declared type, such as `codeNumber=abc`, `minPrice=1.5`, a value outside the 32-bit integer range, malformed JSON, or a body field that cannot be converted, is rejected by ASP.NET Core model validation before the operation runs: status 400 with the framework's validation-problem JSON (`type`, `title`, `status`, `errors`, `traceId`), and no `ResultDto`. That 400 precedes every check of the operation, the company read included (D-109, D-110). A JSON number may also be sent as a JSON string that holds a number, for example `"offer": "250.50"`.

**Field names.** JSON names are camelCase. The JADE `DTO*` field names are kept as they are, so `website`, `itemCategory`, `agentCommission`, `soldDate` and `tenderDate` appear exactly so. Every declared field is written; an unset reference or string is written as `null`.

**Type discriminator.** A body whose declared type is `ItemDto` or `SaleDto`, alone or as an array element, carries the discriminator property `type` as its first property (D-03):

| Declared type | `type` values |
|---|---|
| `ItemDto` (operation results) | `retailItem`, `tenderItem` |
| `SaleDto` (operation results) | `retailSale`, `tenderSale` |
| `SaleDetailDto` (the `sale` of an item) | `retailSaleDetail` |

A value of the base type itself carries no `type`: the base `ItemDto` in a sale's `item`, and the base `SaleDetailDto` in a sold tender item's `sale` ([Response population](#response-population)). The `tenderItem` of a `TenderDto` is declared `TenderItemDto` and carries no `type`. Properties a derived type adds are written before the inherited ones, for example `type`, `closureDate`, `guid`, `number`, … for a tender item.

**Value encodings.**

| Value | JADE type | JSON encoding |
|---|---|---|
| Money: `price`, `offer`, `agentCommission` | `Decimal[12,2]` | JSON number; values read from the database keep 2 decimals (`999.99`, `0.00`); a field the operation never sets is `0` |
| `number` (item code number), `statusCode` | `Integer` | JSON number |
| `listedDate`, `closureDate` | `Date` | `yyyy-MM-dd` string, for example `"2026-10-22"` |
| `timestamp`, `soldDate`, `tenderDate` | `TimeStamp` | ISO 8601 local date-time without offset, in the database server's local time, millisecond precision; fractional seconds are written only when non-zero, for example `"2026-10-15T09:41:27.318"` or `"2026-11-30T16:11:03"` (D-15) |
| `guid` | `String[39]` | The source GUID string; GUIDs generated by the new stack are lowercase hexadecimal in braces, `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`; seeded rows keep the GUID of their data file (D-32, EE-09) |
| `photo` | `Binary` | base64 string; always `null` in responses ([Response population](#response-population)) |
| `allErrors`, `allWarnings` | `StringArray` | JSON array of strings |

**Empty body.** "Empty" means the status only: no content, `Content-Length: 0` and no `Content-Type` (D-63). The GET operations give their own 400, 404 and 500 outcomes with an empty body; the four POST operations give their own 400 and 404 outcomes as a `ResultDto` and their own 500 outcomes with an empty body. The binding 400 above and the error outcome of [Unexpected and database-level failures](#unexpected-and-database-level-failures) carry a JSON body.

**Lookups.** Name lookups are exact and case-sensitive (`AgentsByNameDict` and `ClientsByNameDict` keys). `getClient` removes leading and trailing spaces first; `getAgent`, `postCreateRetailSale` and `postCreateTender` use the name as given (Q-11). The `getSale` lookup compares the stored category prefix, which is upper case, with the prefix as given, case-sensitively (`SalesByItemDict`). `getItemByCode`, `postCreateRetailSale` and `postCreateTender` find the item by its code number alone; a negative number is an ordinary miss.

**Company binding.** Every operation reads the bound company: the company with the lowest `companies.id` (D-43). With no company, the operation answers 500 with an empty body at the point where it first reads the company ([No company](#no-company), D-40).

**Created resources.** A successful POST answers 201 with a `ResultDto` holding the new row's `guid`; no `Location` header is sent.

## Shared shapes

The contracts live in `src/Erewhon.Api/Rest/Contracts/`. Lengths are those of the JADE `DTO*` attributes; on input they are enforced as JADE error 1035 ([String too long](#string-too-long)).

### `AgentDto`, `ClientDto`, `CompanyDto`

`DTOAgent` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L107-120], `DTOClient` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L121-134], `DTOCompany` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L135-148], each with `guid` from `DTOObject` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L98-106]. The three contracts have the same fields.

| Field | JSON type | JADE field and length |
|---|---|---|
| `guid` | string or null | `guid`, `String[39]` |
| `name` | string or null | `name`, `String[81]` |
| `street` | string or null | `street`, `String[51]` |
| `city` | string or null | `city`, `String[51]` |
| `country` | string or null | `country`, `String[51]` |
| `phone` | string or null | `phone`, `String[26]` |
| `fax` | string or null | `fax`, `String[26]` |
| `email` | string or null | `email`, `String[61]` |
| `website` | string or null | `website`, `String[61]` |

### `ItemDto`, `RetailItemDto`, `TenderItemDto`

`DTOItem` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L155-172], `DTORetailItem` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L173-177], `DTOTenderItem` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L178-184].

| Field | JSON type | JADE field and length |
|---|---|---|
| `type` | string | Discriminator: `retailItem` or `tenderItem`; absent on a base `ItemDto` |
| `closureDate` | string or null | `DTOTenderItem.closureDate`, `Date`; tender items only |
| `guid` | string or null | `guid`, `String[39]` |
| `number` | number | `number`, `Integer` (the item's code number) |
| `categoryPrefix` | string or null | `categoryPrefix`, `String[5]` |
| `description` | string or null | `description`, `String` (unbounded) |
| `listedDate` | string or null | `listedDate`, `Date` |
| `agent` | `AgentDto` or null | `agent`, `DTOAgent` (the item's agent) |
| `region` | string or null | `region`, `String[81]` (the region's name) |
| `photo` | string (base64) or null | `photo`, `Binary` |
| `price` | number | `price`, `Decimal[12,2]` (the reserve price of a tender item) |
| `name` | string or null | `name`, `String[31]` |
| `itemCategory` | string or null | `itemCategory`, `String[41]` (the category's name) |
| `sale` | `SaleDetailDto` or null | `sale`, `DTOSaleDetail` |

`RetailItemDto` adds no field.

### `SaleDto`, `RetailSaleDto`, `TenderSaleDto`

`DTOSale` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L202-211], `DTORetailSale` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L212-218], `DTOTenderSale` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L219-227].

| Field | JSON type | JADE field and length |
|---|---|---|
| `type` | string | Discriminator: `retailSale` or `tenderSale` |
| `timestamp` | string or null | `DTORetailSale.timestamp`, `TimeStamp` (date sold); retail sales only |
| `offer` | number | `DTOTenderSale.offer`, `Decimal[12,2]`; tender sales only |
| `tender` | `TenderDto` or null | `DTOTenderSale.tender`, `DTOTender`; tender sales only |
| `guid` | string or null | `guid`, `String[39]` |
| `agentCommission` | number | `agentCommission`, `Decimal[12,2]` |
| `client` | `ClientDto` or null | `client`, `DTOClient` (the buying client) |
| `item` | `ItemDto` or null | `item`, `DTOItem` (the sold item) |

### `SaleDetailDto`, `RetailSaleDetailDto`, `TenderSaleDetailDto`

`DTOSaleDetail` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L228-241], `DTORetailSaleDetail` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L242-249], `DTOTenderSaleDetail` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L250-257].

| Field | JSON type | JADE field and length |
|---|---|---|
| `type` | string | Discriminator: `retailSaleDetail` (or `tenderSaleDetail`, which no operation produces); absent on a base `SaleDetailDto` |
| `soldDate` | string or null | `DTORetailSaleDetail.soldDate`, `TimeStamp`; retail sale details only |
| `price` | number | `DTORetailSaleDetail.price`, `Decimal[12,2]` (the sale price); retail sale details only |
| `offer` | number | `DTOTenderSaleDetail.offer`, `Decimal[12,2]`; tender sale details only |
| `tenderDate` | string or null | `DTOTenderSaleDetail.tenderDate`, `TimeStamp`; tender sale details only |
| `guid` | string or null | `guid`, `String[39]` (the sale's GUID) |
| `agentCommission` | number | `agentCommission`, `Decimal[12,2]` |
| `client` | `ClientDto` or null | `client`, `DTOClient` (the buying client) |

### `TenderDto`

`DTOTender` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L258-268].

| Field | JSON type | JADE field and length |
|---|---|---|
| `guid` | string or null | `guid`, `String[39]` |
| `client` | `ClientDto` or null | `client`, `DTOClient` (the tendering client) |
| `tenderItem` | `TenderItemDto` or null | `tenderItem`, `DTOTenderItem`; written without `type` |
| `offer` | number | `offer`, `Decimal[12,2]` |
| `timestamp` | string or null | `timestamp`, `TimeStamp` (date tendered) |

### `ResultDto`

`DTOResult` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L195-201] with `guid` from `DTOObject`. Returned by the four POST operations for 201, 400 and 404.

| Field | JSON type | JADE field and length |
|---|---|---|
| `statusCode` | number | `statusCode`, `Integer`; always equal to the HTTP status |
| `allErrors` | array of strings | `allErrors`, `StringArray`; message texts in the order the checks raised them; empty on success |
| `allWarnings` | array of strings | `allWarnings`, `StringArray`; always empty |
| `guid` | string or null | `guid`, `String[39]`; the GUID of the created agent, client, retail sale or tender on 201, otherwise `null` |

### Request bodies

`RetailSaleRequest` (`postCreateRetailSale`) and `TenderRequest` (`postCreateTender`) carry the scalar parameters of the JADE methods [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2732-2860] as JSON fields (D-03). The string fields are unbounded, as the JADE `String` parameters are.

| Field | JSON type | JADE parameter | In |
|---|---|---|---|
| `client` | string or null | `pClient : String` | both |
| `codePrefix` | string or null | `pCodePrefix : String`; read by neither operation | both |
| `codeNumber` | number (32-bit integer) | `pCodeNumber : Integer` | both |
| `offer` | number | `pOffer : Decimal` | `TenderRequest` only |

`postCreateAgent` takes an `AgentDto` and `postCreateClient` a `ClientDto` as their bodies.

## Response population

Responses are filled as the JADE `DTO*.populateFromObject` methods fill them [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2087-2324]; `src/Erewhon.Api/Rest/RestDtoMapper.cs` ports them.

- **Agent, client, company.** `guid`, `name` and the seven address fields of the object [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2098-2154].
- **Item.** `guid`, `agent` (the item's agent), `number`, `description`, `listedDate`, `price`, `region` (region name), `itemCategory` (category name) and `name` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2158-2177]. `categoryPrefix` and `photo` are declared but never populated, so they are `null` in every item response and in every item nested in a sale or tender (Q-32).
- **Retail item.** When the item is sold, `sale` is a `RetailSaleDetailDto` (`type` `retailSaleDetail`) with the sale's `guid`, `agentCommission`, `client`, `soldDate` (date sold) and `price` (sale price); otherwise `sale` is `null` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2181-2194, L2263-2289].
- **Tender item.** `closureDate`, and when the item is sold, `sale` is a base `SaleDetailDto` with only the sale's `guid`, `agentCommission` and `client`, and no `type`; JADE creates the declared type `DTOSaleDetail` there. `TenderSaleDetailDto` is never produced, so `offer` and `tenderDate` never appear in an item's `sale` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2198-2213] (Q-33).
- **Sale.** `guid`, `agentCommission`, `client` (the buying client) and `item`. The `item` is a base `ItemDto`: no `type`, no `closureDate`, and `sale` `null` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2217-2232] (Q-33).
- **Retail sale.** Adds `timestamp`, the date sold [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2236-2245].
- **Tender sale.** Adds `tender`, the winning tender. The tender sale's own `offer` is never populated and is always `0`; the winning offer is `tender.offer` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2249-2259] (Q-33).
- **Tender.** `guid`, `client` (the tendering client), `offer`, `timestamp` (date tendered) and `tenderItem`, a full `TenderItemDto` populated as a tender item above, its `sale` included [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2307-2324].
- **Missing agent or region.** An item whose agent or region is unset (deleting an agent or region clears the reference) is not populated: `RestDtoMapper` raises `NoCompanyException`, the JADE null-reference error 1090 that dereferencing the unset `myAgent` or `myRegion` raises in `DTOItem.populateFromObject`, and the operation that returns the item answers 500 with an empty body (D-40, D-63).

Proving tests: `RestCatalogueTests.Q32_ItemCategoryPrefixAndPhotoNotPopulated`, `RestCatalogueTests.Q33_TenderSaleDetailShapes`.

## Operations

The examples use the data of the JADE REST test fixture `createTestData` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L3048-3100] (T-19): company "Jade Software Test Company", agent "Test Agent", client "Test Client", category "Test Category 1" (`CAT1`), region "Region Name", retail item 1 at 999.99 sold to the client, and tender item 2 with reserve 888.88 won by the client's tender of 889.88. GUIDs, dates and times are illustrative. Status tables list the outcomes in the order the operation checks them; the first matching row applies. The model-binding 400 of [Request binding](#conventions) precedes every row.

### 1. `getAgent` — `GET /api/rest/agents/by-name?name=`

JADE: `getAgent(pName : String) : DTOAgent` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2328-2355] (B-52).

| Parameter | In | Type | Use |
|---|---|---|---|
| `name` | query | string | Agent name, used as given: not trimmed, case-sensitive; absent is empty |

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists (read before the lookup) |
| 404 | empty | The bound company has no agent with exactly this name; an absent, empty, padded or overlong name is an ordinary miss |
| 200 | `AgentDto` | The agent |

```json
{
  "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
  "name": "Test Agent",
  "street": "Street",
  "city": "City",
  "country": "Country",
  "phone": "Phone",
  "fax": "Fax",
  "email": "agent@email.com",
  "website": "www.website.com"
}
```

Proving tests: `RestTestGetAgent.testValidAgent`, `RestTestGetAgent.testInvalidAgent`, `RestCatalogueTests.B52_RestLookupTrimming`, `RestCatalogueTests.B22_LongLookupNameIs404`.

### 2. `getAllAgents` — `GET /api/rest/agents`

JADE: `getAllAgents() : DTOAgentArray` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2356-2377] (B-53).

No parameters.

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists |
| 200 | array of `AgentDto` | Every agent of the bound company in name order (`AgentsByNameDict`); `[]` when it has none |

```json
[
  {
    "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
    "name": "Test Agent",
    "street": "Street",
    "city": "City",
    "country": "Country",
    "phone": "Phone",
    "fax": "Fax",
    "email": "agent@email.com",
    "website": "www.website.com"
  }
]
```

Proving tests: `RestTestGetAllAgents.testGetAllAgents`, `RestTestGetAllAgents.AgentsListedInNameOrder`.

### 3. `getAllClients` — `GET /api/rest/clients`

JADE: `getAllClients() : DTOClientArray` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2378-2399] (B-53).

No parameters.

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists |
| 200 | array of `ClientDto` | Every client of the bound company in name order (`ClientsByNameDict`); `[]` when it has none |

```json
[
  {
    "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
    "name": "Test Client",
    "street": "Street",
    "city": "City",
    "country": "Country",
    "phone": "Phone",
    "fax": "Fax",
    "email": "client@email.com",
    "website": "www.website.com"
  }
]
```

Proving tests: `RestTestGetAllClients.testGetAllClients`, `RestTestGetAllClients.ClientsListedInNameOrder`.

### 4. `getAllSales` — `GET /api/rest/sales`

JADE: `getAllSales() : DTOSaleArray` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2400-2434] (B-54).

No parameters.

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists |
| 200 | array of `SaleDto` | Every sale of the bound company in `SalesByItemDict` order (the sold item's category prefix, then its code number), each typed `retailSale` or `tenderSale`; `[]` when it has none |

```json
[
  {
    "type": "retailSale",
    "timestamp": "2026-10-15T09:41:27.318",
    "guid": "{f19b2d4e-8a7c-4e3b-b5d6-1c2e3f4a5b96}",
    "agentCommission": 0.00,
    "client": {
      "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
      "name": "Test Client",
      "street": "Street",
      "city": "City",
      "country": "Country",
      "phone": "Phone",
      "fax": "Fax",
      "email": "client@email.com",
      "website": "www.website.com"
    },
    "item": {
      "guid": "{c41d7e9a-3b2f-4e6d-a1c8-5f7e9d0b2a33}",
      "number": 1,
      "categoryPrefix": null,
      "description": "A retail item for testing",
      "listedDate": "2026-10-15",
      "agent": {
        "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
        "name": "Test Agent",
        "street": "Street",
        "city": "City",
        "country": "Country",
        "phone": "Phone",
        "fax": "Fax",
        "email": "agent@email.com",
        "website": "www.website.com"
      },
      "region": "Region Name",
      "photo": null,
      "price": 999.99,
      "name": "Retail Item",
      "itemCategory": "Test Category 1",
      "sale": null
    }
  },
  {
    "type": "tenderSale",
    "offer": 0,
    "tender": {
      "guid": "{d2c8e4a6-9b1f-4c7d-a3e5-6f7a8b9c0d12}",
      "client": {
        "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
        "name": "Test Client",
        "street": "Street",
        "city": "City",
        "country": "Country",
        "phone": "Phone",
        "fax": "Fax",
        "email": "client@email.com",
        "website": "www.website.com"
      },
      "tenderItem": {
        "closureDate": "2026-10-22",
        "guid": "{a7e3c9b1-4d5f-4a2e-8c6b-0d1e2f3a4b85}",
        "number": 2,
        "categoryPrefix": null,
        "description": "A tender item for testing",
        "listedDate": "2026-10-15",
        "agent": {
          "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
          "name": "Test Agent",
          "street": "Street",
          "city": "City",
          "country": "Country",
          "phone": "Phone",
          "fax": "Fax",
          "email": "agent@email.com",
          "website": "www.website.com"
        },
        "region": "Region Name",
        "photo": null,
        "price": 888.88,
        "name": "Tender Item",
        "itemCategory": "Test Category 1",
        "sale": {
          "guid": "{b6a4c2e8-1d3f-4b5a-9c7e-2f4a6b8c0e24}",
          "agentCommission": 0.00,
          "client": {
            "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
            "name": "Test Client",
            "street": "Street",
            "city": "City",
            "country": "Country",
            "phone": "Phone",
            "fax": "Fax",
            "email": "client@email.com",
            "website": "www.website.com"
          }
        }
      },
      "offer": 889.88,
      "timestamp": "2026-10-15T09:41:27.305"
    },
    "guid": "{b6a4c2e8-1d3f-4b5a-9c7e-2f4a6b8c0e24}",
    "agentCommission": 0.00,
    "client": {
      "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
      "name": "Test Client",
      "street": "Street",
      "city": "City",
      "country": "Country",
      "phone": "Phone",
      "fax": "Fax",
      "email": "client@email.com",
      "website": "www.website.com"
    },
    "item": {
      "guid": "{a7e3c9b1-4d5f-4a2e-8c6b-0d1e2f3a4b85}",
      "number": 2,
      "categoryPrefix": null,
      "description": "A tender item for testing",
      "listedDate": "2026-10-15",
      "agent": {
        "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
        "name": "Test Agent",
        "street": "Street",
        "city": "City",
        "country": "Country",
        "phone": "Phone",
        "fax": "Fax",
        "email": "agent@email.com",
        "website": "www.website.com"
      },
      "region": "Region Name",
      "photo": null,
      "price": 888.88,
      "name": "Tender Item",
      "itemCategory": "Test Category 1",
      "sale": null
    }
  }
]
```

Proving tests: `RestTestGetAllSales.testGetAllSales`, `RestTestGetAllSales.SalesListedByItemPrefixThenCodeNumber`.

### 5. `getClient` — `GET /api/rest/clients/by-name?name=`

JADE: `getClient(pName : String) : DTOClient` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2435-2458] (B-55).

| Parameter | In | Type | Use |
|---|---|---|---|
| `name` | query | string | Client name; leading and trailing spaces are removed (`trimBlanks`), then matched case-sensitively; absent is empty |

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists (read before the lookup) |
| 404 | empty | The bound company has no client with the trimmed name |
| 200 | `ClientDto` | The client |

```json
{
  "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
  "name": "Test Client",
  "street": "Street",
  "city": "City",
  "country": "Country",
  "phone": "Phone",
  "fax": "Fax",
  "email": "client@email.com",
  "website": "www.website.com"
}
```

Proving tests: `RestTestGetClient.testValidClient`, `RestTestGetClient.testInvalidClient`, `RestCatalogueTests.B52_RestLookupTrimming`.

### 6. `getCompany` — `GET /api/rest/company`

JADE: `getCompany() : DTOCompany` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2459-2482] (B-56).

No parameters.

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists (the operation tests for it explicitly) |
| 200 | `CompanyDto` | The bound company (the lowest `companies.id`, D-43) |

```json
{
  "guid": "{5c0e9a7b-2f41-4d8e-b3a6-7e19c4d20a51}",
  "name": "Jade Software Test Company",
  "street": "5 Sir Gill Simpson Drive",
  "city": "Christchurch",
  "country": "New Zealand",
  "phone": "+64 3 365 2500",
  "fax": "+64 3 358 7276",
  "email": "info@jadeworld.com",
  "website": "www.jadeworld.com"
}
```

Proving tests: `RestTestGetCompany.testValidCompany`, `RestTestGetCompany.testInvalidCompany`.

### 7. `getItemByCode` — `GET /api/rest/items/by-code?codePrefix=&codeNumber=`

JADE: `getItemByCode(pCodePrefix : String; pCodeNumber : Integer) : DTOItem` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2483-2535] (B-57).

| Parameter | In | Type | Use |
|---|---|---|---|
| `codePrefix` | query | string | Checked only: leading and trailing spaces removed, then it must be 1 to 4 characters; it plays no part in the lookup (Q-11) |
| `codeNumber` | query | 32-bit integer | The item's code number; absent is 0 |

| Status | Body | Condition |
|---|---|---|
| 400 | empty | The trimmed prefix is empty or longer than 4 characters; a 5-character category prefix, valid in the catalogue, is rejected here (B-09) |
| 400 | empty | `codeNumber` is 0 or absent |
| 500 | empty | No company exists; the two checks above run first (D-40) |
| 404 | empty | The bound company has no item with the code number |
| 200 | `ItemDto` typed `retailItem` or `tenderItem` | The item, populated as in [Response population](#response-population) |

`GET /api/rest/items/by-code?codePrefix=CAT1&codeNumber=1`:

```json
{
  "type": "retailItem",
  "guid": "{c41d7e9a-3b2f-4e6d-a1c8-5f7e9d0b2a33}",
  "number": 1,
  "categoryPrefix": null,
  "description": "A retail item for testing",
  "listedDate": "2026-10-15",
  "agent": {
    "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
    "name": "Test Agent",
    "street": "Street",
    "city": "City",
    "country": "Country",
    "phone": "Phone",
    "fax": "Fax",
    "email": "agent@email.com",
    "website": "www.website.com"
  },
  "region": "Region Name",
  "photo": null,
  "price": 999.99,
  "name": "Retail Item",
  "itemCategory": "Test Category 1",
  "sale": {
    "type": "retailSaleDetail",
    "soldDate": "2026-10-15T09:41:27.318",
    "price": 999.99,
    "guid": "{f19b2d4e-8a7c-4e3b-b5d6-1c2e3f4a5b96}",
    "agentCommission": 0.00,
    "client": {
      "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
      "name": "Test Client",
      "street": "Street",
      "city": "City",
      "country": "Country",
      "phone": "Phone",
      "fax": "Fax",
      "email": "client@email.com",
      "website": "www.website.com"
    }
  }
}
```

A tender item has the same shape with `"type": "tenderItem"` followed by `closureDate`, and a sold tender item's `sale` holds only `guid`, `agentCommission` and `client` (Q-33), as in the `tenderItem` of the [`getAllSales`](#4-getallsales--get-apirestsales) example.

Proving tests: `RestTestGetItemByCode.testGetRetailItem`, `RestTestGetItemByCode.testGetTenderItem`, `RestTestGetItemByCode.testInvalidCodeNumber`, `RestTestGetItemByCode.testInvalidPrefix`, `RestTestGetItemByCode.testItemNotFound`, `BehaviourCatalogueTests.B09_FiveCharacterPrefix`.

### 8. `getItemsByPrice` — `GET /api/rest/items?minPrice=&maxPrice=`

JADE: `getItemsByPrice(pMinPrice : Integer; pMaxPrice : Integer) : DTOItemsArray` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2536-2597] (B-58).

| Parameter | In | Type | Use |
|---|---|---|---|
| `minPrice` | query | 32-bit integer | Lowest price included; absent is 0 |
| `maxPrice` | query | 32-bit integer | Highest price included; absent is 0 |

| Status | Body | Condition |
|---|---|---|
| 400 | empty | `minPrice` < 0 |
| 400 | empty | `maxPrice` < 0 |
| 400 | empty | `maxPrice` < `minPrice` |
| 500 | empty | No company exists; the three checks above run first (D-40) |
| 200 | `[]` | The bound company has no items |
| 200 | array of `ItemDto`, each typed `retailItem` or `tenderItem` | Every item with `minPrice` ≤ `price` ≤ `maxPrice` (both bounds inclusive, a tender item compared by its reserve price) in code-number order (`ItemsByNumberDict`); `[]` when none is in the band. A bound of 0 is an ordinary bound, so `minPrice=0&maxPrice=0` and a request without parameters match no item, item prices being above 0 (B-07) |

`GET /api/rest/items?minPrice=900&maxPrice=1000`:

```json
[
  {
    "type": "retailItem",
    "guid": "{c41d7e9a-3b2f-4e6d-a1c8-5f7e9d0b2a33}",
    "number": 1,
    "categoryPrefix": null,
    "description": "A retail item for testing",
    "listedDate": "2026-10-15",
    "agent": {
      "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
      "name": "Test Agent",
      "street": "Street",
      "city": "City",
      "country": "Country",
      "phone": "Phone",
      "fax": "Fax",
      "email": "agent@email.com",
      "website": "www.website.com"
    },
    "region": "Region Name",
    "photo": null,
    "price": 999.99,
    "name": "Retail Item",
    "itemCategory": "Test Category 1",
    "sale": {
      "type": "retailSaleDetail",
      "soldDate": "2026-10-15T09:41:27.318",
      "price": 999.99,
      "guid": "{f19b2d4e-8a7c-4e3b-b5d6-1c2e3f4a5b96}",
      "agentCommission": 0.00,
      "client": {
        "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
        "name": "Test Client",
        "street": "Street",
        "city": "City",
        "country": "Country",
        "phone": "Phone",
        "fax": "Fax",
        "email": "client@email.com",
        "website": "www.website.com"
      }
    }
  }
]
```

With the same data, `minPrice=800&maxPrice=900` returns the tender item alone.

Proving tests: `RestTestGetItemsByPrice.testInvalidMinPrice`, `RestTestGetItemsByPrice.testInvalidMaxPrice`, `RestTestGetItemsByPrice.testMaxPriceHigherThanMinPrice`, `RestTestGetItemsByPrice.testNoItems`, `RestTestGetItemsByPrice.testPriceBetween800And900`, `RestTestGetItemsByPrice.testPriceBetween900And1000`, `RestTestGetItemsByPrice.PriceBandBoundsAreInclusive`.

### 9. `getSale` — `GET /api/rest/sales/by-code?codePrefix=&codeNumber=`

JADE: `getSale(pCodePrefix : String; pCodeNumber : Integer) : DTOSale` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2598-2651] (B-59).

| Parameter | In | Type | Use |
|---|---|---|---|
| `codePrefix` | query | string | Checked with leading and trailing spaces removed, then looked up as given: untrimmed and case-sensitive (Q-11) |
| `codeNumber` | query | 32-bit integer | The sold item's code number; absent is 0 |

| Status | Body | Condition |
|---|---|---|
| 400 | empty | The trimmed prefix is empty or longer than 4 characters |
| 400 | empty | `codeNumber` is 0 or absent |
| 500 | empty | No company exists; the two checks above run first (D-40) |
| 404 | empty | The bound company has no sale of an item whose category prefix equals `codePrefix` as given and whose code number is `codeNumber`; a lower-case or space-padded prefix passes the checks and misses here |
| 200 | `SaleDto` typed `retailSale` or `tenderSale` | The sale, populated as in [Response population](#response-population) |

`GET /api/rest/sales/by-code?codePrefix=CAT1&codeNumber=1`:

```json
{
  "type": "retailSale",
  "timestamp": "2026-10-15T09:41:27.318",
  "guid": "{f19b2d4e-8a7c-4e3b-b5d6-1c2e3f4a5b96}",
  "agentCommission": 0.00,
  "client": {
    "guid": "{2e9f1a6c-7d4b-4c3a-8e52-9f0a1b2c3d47}",
    "name": "Test Client",
    "street": "Street",
    "city": "City",
    "country": "Country",
    "phone": "Phone",
    "fax": "Fax",
    "email": "client@email.com",
    "website": "www.website.com"
  },
  "item": {
    "guid": "{c41d7e9a-3b2f-4e6d-a1c8-5f7e9d0b2a33}",
    "number": 1,
    "categoryPrefix": null,
    "description": "A retail item for testing",
    "listedDate": "2026-10-15",
    "agent": {
      "guid": "{8b7d4f20-6c3e-4a19-9f05-1e2d3c4b5a60}",
      "name": "Test Agent",
      "street": "Street",
      "city": "City",
      "country": "Country",
      "phone": "Phone",
      "fax": "Fax",
      "email": "agent@email.com",
      "website": "www.website.com"
    },
    "region": "Region Name",
    "photo": null,
    "price": 999.99,
    "name": "Retail Item",
    "itemCategory": "Test Category 1",
    "sale": null
  }
}
```

`codePrefix=CAT1&codeNumber=2` returns the tender sale shown as the second element of the [`getAllSales`](#4-getallsales--get-apirestsales) example.

Proving tests: `RestTestGetSale.testGetRetailSale`, `RestTestGetSale.testGetTenderSale`, `RestTestGetSale.testInvalidCodeNumber`, `RestTestGetSale.testInvalidPrefix`, `RestTestGetSale.testSaleNotFound`.

### 10. `postCreateAgent` — `POST /api/rest/agents`

JADE: `postCreateAgent(pAgent : DTOAgent) : DTOResult` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2652-2691] (B-60).

Request body: an `AgentDto`. `guid` is length-checked and not stored; `name` and the seven address fields are copied untrimmed into the agent create operation. The agent is created in its own transaction. The create operation runs these steps in order; the first step that fails ends it with 400, and the steps after it do not run (B-10, B-11):

1. Pre-validation removes leading and trailing spaces from `name` (`trimBlanks`); the address fields are left as given.
2. Validation checks the name, then the address fields as given (`fax` and `website` are not checked): an absent or empty value is missing, a value of only white space counts as present, and `email` is matched against the e-mail format as given, without trimming.
3. The agents lock is taken.
4. The seven address fields are trimmed of leading and trailing CR, LF, tab, space and NUL characters (`trimWhiteSpace`) and the address is validated again. On success the agent is stored with the trimmed name and the trimmed address fields.

So:

- A padded `email`, such as `" agent@email.com "`, fails step 2 with `Email must be in a valid format e.g. user@domain.com` instead of being trimmed; an `email` of only white space fails the same way.
- A `street`, `city`, `country` or `phone` of only white space passes step 2 and fails step 4 with its required error, for example `Street is required`.
- When step 2 fails, the errors that only step 4 raises are not reported: an empty `name` with a `street` of only white space gives `Agent name is required` alone.

Example from `RestTestPostCreateAgent.testValidAgent`:

```json
{
  "name": "Test Agent Name",
  "street": "Street",
  "city": "City",
  "country": "Country",
  "phone": "Phone",
  "fax": "Fax",
  "email": "agent@email.com",
  "website": "www.website.com"
}
```

| Status | Body | Condition |
|---|---|---|
| 500 | empty | The body is absent, empty or JSON `null` (D-40) |
| 500 | empty | A field is longer than its declared length (`guid` 39; `name` 81; `street`, `city`, `country` 51; `phone`, `fax` 26; `email`, `website` 61), measured before any trimming, so a padded value fails even when its trimmed form would fit (D-41, B-22) |
| 500 | empty | No company exists |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` | The agent create operation fails; `allErrors` holds every error of the step that failed, in check order: `Agent name is required`, or `Agent already exists` for a name, after trimming, that another agent of the company holds; then the address errors of the fields as given, `Street is required`, `City is required`, `Country is required`, `Phone number is required`, `Email is required`, `Email must be in a valid format e.g. user@domain.com`; or `Create conflicted with another user's activity.  Try again shortly.` when the agents lock is not obtained; or, from the trimmed address, `Street is required`, `City is required`, `Country is required`, `Phone number is required` for each of those fields that holds only white space. A duplicate name is 400, not 409 (D-04, C-05, Q-10) |
| 201 | `ResultDto`, `statusCode` 201, `guid` | The agent is created; `guid` is the new agent's GUID |

201 body:

```json
{
  "statusCode": 201,
  "allErrors": [],
  "allWarnings": [],
  "guid": "{4d6b8f02-7e1a-4c3d-9b5e-0a2c4e6f8a13}"
}
```

400 body for a name already in use:

```json
{
  "statusCode": 400,
  "allErrors": [
    "Agent already exists"
  ],
  "allWarnings": [],
  "guid": null
}
```

Proving tests: `RestTestPostCreateAgent.testValidAgent`, `RestTestPostCreateAgent.testInvalidAgent`, `RestCatalogueTests.B60_RestDuplicateAgentIs400`, `RestCatalogueTests.B22_PaddedRestNameRejectedBeforeTrim`.

### 11. `postCreateClient` — `POST /api/rest/clients`

JADE: `postCreateClient(pClient : DTOClient) : DTOResult` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2692-2731] (B-60).

Request body: a `ClientDto`, handled as [`postCreateAgent`](#10-postcreateagent--post-apirestagents) handles an `AgentDto`: the client is created in its own transaction by the same four steps in the same order, except that step 1 removes leading and trailing CR, LF, tab, space and NUL characters from `name` (`trimWhiteSpace`) and step 2 checks the client name (B-10, B-12). Step 1 leaves the address fields as given; step 2 validates them as given; step 4 trims them with `trimWhiteSpace`, validates them again and, on success, stores the client with the trimmed name and the trimmed address fields. So a padded `email`, such as `" client@email.com "`, is 400 with `Email must be in a valid format e.g. user@domain.com`; a `street`, `city`, `country` or `phone` of only white space is 400 with its required error from step 4; and when step 2 fails, the errors that only step 4 raises are not reported, so an empty `name` with a `street` of only white space gives `Client name is required` alone. Example from `RestTestPostCreateClient.testValidClient`:

```json
{
  "name": "Test Client Name",
  "street": "Street",
  "city": "City",
  "country": "Country",
  "phone": "Phone",
  "fax": "Fax",
  "email": "client@email.com",
  "website": "www.website.com"
}
```

| Status | Body | Condition |
|---|---|---|
| 500 | empty | The body is absent, empty or JSON `null` (D-40) |
| 500 | empty | A field is longer than its declared length, measured before any trimming (lengths as for `postCreateAgent`) (D-41, B-22) |
| 500 | empty | No company exists |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` | The client create operation fails; `allErrors` holds every error of the step that failed, in check order: `Client name is required`, or `Client already exists` for a name, after trimming, that another client of the company holds; then the address errors of the fields as given, as for `postCreateAgent`; or `Create conflicted with another user's activity.  Try again shortly.` when the clients lock is not obtained; or, from the trimmed address, the required errors as for `postCreateAgent`. A duplicate name is 400, not 409 (D-04) |
| 201 | `ResultDto`, `statusCode` 201, `guid` | The client is created; `guid` is the new client's GUID |

201 body:

```json
{
  "statusCode": 201,
  "allErrors": [],
  "allWarnings": [],
  "guid": "{9e1c3a5f-2b4d-4f6a-8c0e-1d3f5a7b9c25}"
}
```

Proving tests: `RestTestPostCreateClient.testValidClient`, `RestTestPostCreateClient.testInvalidClient`.

### 12. `postCreateRetailSale` — `POST /api/rest/retail-sales`

JADE: `postCreateRetailSale(pClient : String; pCodePrefix : String; pCodeNumber : Integer) : DTOResult` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2732-2796] (B-61).

Request body: a `RetailSaleRequest`. An absent or empty body, or `{}`, is read as a request whose fields are unset. `codePrefix` is not read. Example for the unsold retail item that `RestTestPostCreateRetailSale.testCreateRetailSale` adds (code number 3, price 555.00):

```json
{
  "client": "Test Client",
  "codePrefix": "CAT1",
  "codeNumber": 3
}
```

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists |
| 404 | `ResultDto`, `statusCode` 404, `allErrors` `["Client not found"]` | The bound company has no client whose name equals `client` exactly (untrimmed, case-sensitive); an absent `client` included |
| 404 | `ResultDto`, `statusCode` 404, `allErrors` `["Item was not found"]` | The bound company has no item with code number `codeNumber`; 0 or absent included |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` `["Item is not a Retail Item"]` | The item is a tender item (B-32) |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` | The retail sale create operation fails: its validation errors in check order, for example `The Item has already been sold`, or `Create conflicted with another user's activity.  Try again shortly.` when a sale lock is not obtained (B-19, B-21) |
| 201 | `ResultDto`, `statusCode` 201, `guid` | The retail sale is created in its own transaction: sale price = the item's price, date sold = the database server's current timestamp (D-15), agent commission computed at creation (B-29); `guid` is the new sale's GUID |

201 body:

```json
{
  "statusCode": 201,
  "allErrors": [],
  "allWarnings": [],
  "guid": "{1a3c5e7b-6d8f-4b0a-9c2e-3f5b7d9e1a37}"
}
```

404 body for an unknown client:

```json
{
  "statusCode": 404,
  "allErrors": [
    "Client not found"
  ],
  "allWarnings": [],
  "guid": null
}
```

Proving tests: `RestTestPostCreateRetailSale.testCreateRetailSale`, `RestTestPostCreateRetailSale.testClientNotValid`, `RestTestPostCreateRetailSale.testItemNotFound`, `RestTestPostCreateRetailSale.testItemNotRetailSale`.

### 13. `postCreateTender` — `POST /api/rest/tenders`

JADE: `postCreateTender(pClient : String; pCodePrefix : String; pCodeNumber : Integer; pOffer : Decimal) : DTOResult` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2797-2860] (B-62).

Request body: a `TenderRequest`. An absent or empty body, or `{}`, is read as a request whose fields are unset. `codePrefix` is not read. `offer` is rounded to 2 decimal places, ties away from zero, when it is assigned (`250.505` becomes `250.51`), and is not range-checked before the create (D-16, D-110). Example for the open tender item that `RestTestPostCreateTender.testCreateTender` adds (code number 3, reserve 111.00, closing in 7 days):

```json
{
  "client": "Test Client",
  "codePrefix": "CAT1",
  "codeNumber": 3,
  "offer": 121.00
}
```

| Status | Body | Condition |
|---|---|---|
| 500 | empty | No company exists |
| 404 | `ResultDto`, `statusCode` 404, `allErrors` `["Client not found"]` | The bound company has no client whose name equals `client` exactly (untrimmed, case-sensitive); an absent `client` included |
| 404 | `ResultDto`, `statusCode` 404, `allErrors` `["Item was not found"]` | The bound company has no item with code number `codeNumber`; 0 or absent included |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` `["Item is not a Tender Item"]` | The item is a retail item (B-32) |
| 400 | `ResultDto`, `statusCode` 400, `allErrors` | The tender create operation fails: its errors in check order (B-30): `The Tender item has already been sold` (no further check), or `The Tender Item has already closed` when the closure date is before the server date (no further check); `The offer price must be greater than 0` for an offer of 0 or less, otherwise `The offer price cannot be less than the reserve price` below the reserve; `A tender has already been placed on this item` once for each tender of this client on the item with an equal offer (Q-14); or `Create conflicted with another user's activity.  Try again shortly.` when a tender lock is not obtained |
| 500 | `ErrorOutcome` | The create fails in the database, for example an offer beyond `numeric(12,2)` (SQLSTATE `22003`), or a (timestamp, offer) pair already held by another tender of the company (`23505`, Q-17) ([Unexpected and database-level failures](#unexpected-and-database-level-failures), EE-01) |
| 201 | `ResultDto`, `statusCode` 201, `guid` | The tender is created in its own transaction with the database server's current timestamp (D-15); `guid` is the new tender's GUID |

201 body:

```json
{
  "statusCode": 201,
  "allErrors": [],
  "allWarnings": [],
  "guid": "{7f9b1d3a-5c7e-4a9b-8d1f-4b6d8f0a2c49}"
}
```

400 body for an offer below the reserve:

```json
{
  "statusCode": 400,
  "allErrors": [
    "The offer price cannot be less than the reserve price"
  ],
  "allWarnings": [],
  "guid": null
}
```

Proving tests: `RestTestPostCreateTender.testCreateTender`, `RestTestPostCreateTender.testClientNotValid`, `RestTestPostCreateTender.testItemNotFound`, `RestTestPostCreateTender.testItemNotTenderItem`, `RestTestPostCreateTender.testOfferLowerThanReserve`.

## Errors common to every operation

### No company

With no company in `companies`, each operation fails at the point where its JADE method first reads `app.myCompany` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2368, L2468-2473, L2494-2508, L2552-2569, L2609-2624]: `ICompanyContext` raises `NoCompanyException` (JADE error 1090), and `RestExceptionFilter` answers 500 with an empty body and writes no log event; `getCompany` tests for the company and answers the same 500 (D-40, D-63, B-69).

| Operation | Outcome with no company |
|---|---|
| `getAgent`, `getAllAgents`, `getAllClients`, `getAllSales`, `getClient`, `getCompany` | 500, empty body, whatever the input |
| `postCreateAgent`, `postCreateClient` | 500, empty body, whatever the input (a missing body and an overlong field also give 500 with an empty body) |
| `postCreateRetailSale`, `postCreateTender` | 500, empty body, whatever the input |
| `getItemByCode`, `getSale` | 400, empty body, for an empty or over-long trimmed prefix or a code number of 0; 500, empty body, for valid input |
| `getItemsByPrice` | 400, empty body, for a negative bound or `maxPrice` < `minPrice`; 500, empty body, for a valid band |

The model-binding 400 of [Request binding](#conventions) precedes the company read in every operation (D-109, D-110).

Proving tests: `RestTestGetCompany.testInvalidCompany`, `RestCatalogueTests.B69_ListOperationsWithoutCompanyAre500`, `RestCatalogueTests.B69_SingleLookupsWithoutCompanyAre500`, `RestCatalogueTests.B69_InputChecksPrecedeCompanyAccess`.

### String too long

A bounded `DTO*` field longer than its declared length raises JADE error 1035 "String too long" on entry, before any trimming: this applies to the `AgentDto` body of `postCreateAgent` and the `ClientDto` body of `postCreateClient` (lengths in [Shared shapes](#agentdto-clientdto-companydto)). The answer is 500 with an empty body and no log event (D-41, D-63, B-22, EE-17). Scalar parameters, the query values and the `RetailSaleRequest` and `TenderRequest` fields, are unbounded and never raise it: an overlong lookup name is an ordinary miss (404), an overlong query `codePrefix` fails the 4-character check (400), and the `codePrefix` of a request body is not read.

Proving tests: `RestCatalogueTests.B22_PaddedRestNameRejectedBeforeTrim`, `RestCatalogueTests.B22_LongLookupNameIs404`.

### Unexpected and database-level failures

An exception that `RestExceptionFilter` does not handle reaches `ErrorOutcomeMiddleware`, which writes one Serilog error event, except for the `lock` kind (D-64, D-36). Before the response has started, the middleware answers 500 with `Content-Type: application/json` and the `ErrorOutcome` body `{kind, heading, body}`. Once the response has started, it does not rewrite the response: the status and any content already sent stay and nothing more is written; the exception is still translated and logged once, the `lock` kind again excepted, and is then rethrown unchanged, so the server aborts the response and the client can receive a truncated response with no `ErrorOutcome` body (D-64). Database failures are translated by `PostgresErrorMapper` (D-07, D-62); their error code and text are the PostgreSQL SQLSTATE and message in place of the JADE error number and text (EE-01).

| Cause | `kind` | `heading` | `body` |
|---|---|---|---|
| A database failure: for example a unique violation `23505` from an agent or client name or a sale of the same item (B-28) that another request committed after this one validated, or from a (timestamp, offer) pair already held by another tender of the company (Q-17); numeric overflow `22003`; connection loss, reported as `08006` | `exception` | `Error <SQLSTATE>` | `$BaseExceptionBodyWeb` with the SQLSTATE and the PostgreSQL message |
| Any other unexpected exception | `exception` | `Error <code>` | `$BaseExceptionBodyWeb` with the JADE error number, or the .NET `HResult`, and the exception message |
| Deadlock `40P01` | `deadlock` | `Resource Locked` (`$BaseDeadlockHeading`) | `The current operation cannot proceed because a resource is locked.\r\n\r\nSelect OK to abort the operation.` (`$BaseDeadlockBodyWeb`) |
| Lock timeout `55P03` outside a create operation's lock step | `lock` | `Resource Locked` (`$BaseLockMessageHeading`) | `Operation cannot proceed because a resource is locked.\r\n\r\nClick OK to cancel the current operation.` (`$BaseLockMessageBodyWeb`) |

`$BaseExceptionBodyWeb` is `An unexpected error has occurred. Details have been logged.\r\n\r\nError: {1} ({0})\r\n\r\nClick OK to abort the current operation.`, with `{0}` the error code and `{1}` the error text. A lock that a create operation does not obtain is not an error outcome: it is the `Create conflicted with another user's activity.  Try again shortly.` error of a 400 `ResultDto` (B-38). Example, `POST /api/rest/tenders` with an offer of `100000000000`:

```json
{
  "kind": "exception",
  "heading": "Error 22003",
  "body": "An unexpected error has occurred. Details have been logged.\r\n\r\nError: numeric field overflow (22003)\r\n\r\nClick OK to abort the current operation."
}
```

### Status codes declared but not returned

`ErewhonRestService` declares `HTTP_Unauthorized` (401), `HTTP_Forbidden` (403) and `HTTP_Conflict` (409) [Erewhon/ErehwhonExternalInterfacesSchema.scm:L290-300]; no REST operation returns them, and 409 for an existing name is the SOAP Admin outcome only (D-04, C-05, Q-10). The statuses the operations return are 200, 201, 400, 404 and 500, plus the framework's 400, 405 and 415 of [Conventions](#conventions).

## Mapping to the JADE operations

The 13 JADE operations, in the order of the sections above, with the controller action and the verb and route template that serve each; the target column is written as the "Operations" rows of `docs/migration/traceability-matrix.md` write it. `processRequest` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2861-2867] is not an operation: ASP.NET Core routing, configured in `src/Erewhon.Api/ErewhonHost.cs`, dispatches requests to the actions (D-03).

| Matrix key | JADE operation | Target (`Controller.Action VERB route`) | Catalogue |
|---|---|---|---|
| `OP:ErewhonRestService.getAgent` | `getAgent(pName)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2328-2355] | `AgentsController.GetAgent GET api/rest/agents/by-name` | B-52 |
| `OP:ErewhonRestService.getAllAgents` | `getAllAgents()` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2356-2377] | `AgentsController.GetAllAgents GET api/rest/agents` | B-53 |
| `OP:ErewhonRestService.getAllClients` | `getAllClients()` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2378-2399] | `ClientsController.GetAllClients GET api/rest/clients` | B-53 |
| `OP:ErewhonRestService.getAllSales` | `getAllSales()` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2400-2434] | `SalesController.GetAllSales GET api/rest/sales` | B-54 |
| `OP:ErewhonRestService.getClient` | `getClient(pName)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2435-2458] | `ClientsController.GetClient GET api/rest/clients/by-name` | B-55 |
| `OP:ErewhonRestService.getCompany` | `getCompany()` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2459-2482] | `CompanyController.GetCompany GET api/rest/company` | B-56 |
| `OP:ErewhonRestService.getItemByCode` | `getItemByCode(pCodePrefix, pCodeNumber)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2483-2535] | `ItemsController.GetItemByCode GET api/rest/items/by-code` | B-57 |
| `OP:ErewhonRestService.getItemsByPrice` | `getItemsByPrice(pMinPrice, pMaxPrice)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2536-2597] | `ItemsController.GetItemsByPrice GET api/rest/items` | B-58 |
| `OP:ErewhonRestService.getSale` | `getSale(pCodePrefix, pCodeNumber)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2598-2651] | `SalesController.GetSale GET api/rest/sales/by-code` | B-59 |
| `OP:ErewhonRestService.postCreateAgent` | `postCreateAgent(pAgent)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2652-2691] | `AgentsController.PostCreateAgent POST api/rest/agents` | B-60 |
| `OP:ErewhonRestService.postCreateClient` | `postCreateClient(pClient)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2692-2731] | `ClientsController.PostCreateClient POST api/rest/clients` | B-60 |
| `OP:ErewhonRestService.postCreateRetailSale` | `postCreateRetailSale(pClient, pCodePrefix, pCodeNumber)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2732-2796] | `RetailSalesController.PostCreateRetailSale POST api/rest/retail-sales` | B-61 |
| `OP:ErewhonRestService.postCreateTender` | `postCreateTender(pClient, pCodePrefix, pCodeNumber, pOffer)` [Erewhon/ErehwhonExternalInterfacesSchema.scm:L2797-2860] | `TendersController.PostCreateTender POST api/rest/tenders` | B-62 |
