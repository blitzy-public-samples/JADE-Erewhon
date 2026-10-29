# Erewhon strangler facade — results

> Backend used: JADE stand-in (`jade-standin`), not live JADE. Baseline and stand-in responses are stand-in output.

## Run facts

| Fact | Value |
| --- | --- |
| Date (UTC) | 2026-10-29T21:40:11Z |
| Execution mode | `docker` |
| .NET (facade runtime) | .NET 10.0.14 |
| SDK pin (`harness/global.json`) | `10.0.403` |
| Harness version | `1.0.0` |
| Target REST URL | `http://facade:65000` |
| Target SOAP URL | `http://facade:21001` |
| Backend REST URL | `http://standin:65000` |
| Backend SOAP URL | `http://standin:21001` |
| Backend admin URL | `http://standin:9090` |
| Starting commit | `19a5189e9075349aca54de1fb5fdf2d8727403e7` |
| Baseline commit | `243bb787db7bb29f52068435958332e28e300cb5` |
| First facade commit | `ccf809181b2780e292374896f7f0477aba3fa81e` |

## Counts

| Area | Measure | Value |
| --- | --- | --- |
| Baseline | operations (planned) | 22 |
| Baseline | cases (planned) | 56 |
| Baseline | operations captured | 22 |
| Baseline | cases captured | 56 |
| Baseline | cases not captured | 0 |
| Baseline | WSDL record | captured |
| Baseline | capture execution | `local-processes` |
| Equivalence | cases compared | 56 |
| Equivalence | cases equivalent | 56 |
| Equivalence | cases with documented difference | 0 |
| Equivalence | cases with undocumented difference | 0 |
| Equivalence | cases not compared | 0 |
| Equivalence | operations equivalent | 22 |
| Equivalence | operations with documented difference | 0 |
| Equivalence | operations with undocumented difference | 0 |
| Equivalence | operations not compared | 0 |
| Stand-in conformance | members checked | 216 |
| Stand-in conformance | members passed | 117 |
| Stand-in conformance | gaps: `null-only` | 0 |
| Stand-in conformance | gaps: `unobserved` | 0 |
| Stand-in conformance | gaps: `unreachable-in-source` | 99 |
| Stand-in conformance | gaps: `wrong-type` | 0 |
| Stand-in conformance | outcome | pass — 99 of 99 failing items documented by the register; 2 of 2 seed-token assertions hold |
| Routing | cases | 2 |
| Routing | passed | 2 |
| Routing | outcome | pass |
| Failure (unreachable and timeout) | cases | 6 |
| Failure (unreachable and timeout) | passed | 6 |
| Failure (unreachable and timeout) | outcome | pass |
| Failure (unreachable and timeout) | unreachable / timeout | unreachable 4/4 passed; timeout 2/2 passed |
| Listener isolation | cases | 14 |
| Listener isolation | passed | 14 |
| Listener isolation | outcome | pass |
| Facade test results (TRX) | result | read |
| Contracts | OpenAPI drift | pass |
| Contracts | WSDL contract | pass |
| JADE unchanged | paths checked | 227 |
| JADE unchanged | paths modified | 0 |
| Ordering evidence | result | fail — baseline commit 243bb787db7b is not an ancestor of the first facade commit ccf809181b27 |

## Per-operation results

| Operation | Surface | Catalogue id | Planned | Captured | Compared | Result |
| --- | --- | --- | --- | --- | --- | --- |
| `getAgent` | REST | `Rest.getAgent` | 2 | 2 | 2 | equivalent |
| `getAllAgents` | REST | `Rest.getAllAgents` | 1 | 1 | 1 | equivalent |
| `getAllClients` | REST | `Rest.getAllClients` | 1 | 1 | 1 | equivalent |
| `getAllSales` | REST | `Rest.getAllSales` | 1 | 1 | 1 | equivalent |
| `getClient` | REST | `Rest.getClient` | 3 | 3 | 3 | equivalent |
| `getCompany` | REST | `Rest.getCompany` | 3 | 3 | 3 | equivalent |
| `getItemByCode` | REST | `Rest.getItemByCode` | 4 | 4 | 4 | equivalent |
| `getItemsByPrice` | REST | `Rest.getItemsByPrice` | 4 | 4 | 4 | equivalent |
| `getSale` | REST | `Rest.getSale` | 4 | 4 | 4 | equivalent |
| `postCreateAgent` | REST | `Rest.postCreateAgent` | 2 | 2 | 2 | equivalent |
| `postCreateClient` | REST | `Rest.postCreateClient` | 2 | 2 | 2 | equivalent |
| `postCreateRetailSale` | REST | `Rest.postCreateRetailSale` | 5 | 5 | 5 | equivalent |
| `postCreateTender` | REST | `Rest.postCreateTender` | 4 | 4 | 4 | equivalent |
| `getAgent` | SOAP | `Soap.getAgent` | 2 | 2 | 2 | equivalent |
| `getAgentNames` | SOAP | `Soap.getAgentNames` | 1 | 1 | 1 | equivalent |
| `getClient` | SOAP | `Soap.getClient` | 3 | 3 | 3 | equivalent |
| `getClientNames` | SOAP | `Soap.getClientNames` | 1 | 1 | 1 | equivalent |
| `getPhoto` | SOAP | `Soap.getPhoto` | 4 | 4 | 4 | equivalent |
| `updateAgent` | SOAP | `Soap.updateAgent` | 3 | 3 | 3 | equivalent |
| `updateAgentWithDTO` | SOAP | `Soap.updateAgentWithDTO` | 2 | 2 | 2 | equivalent |
| `updateClient` | SOAP | `Soap.updateClient` | 2 | 2 | 2 | equivalent |
| `updateClientWithDTO` | SOAP | `Soap.updateClientWithDTO` | 2 | 2 | 2 | equivalent |

## Demo acceptance

Reported, not enforced: met when at least 20 of 22 operations are equivalent and every other operation returns the baseline status code with a well-formed payload.

Met: at least 20 of 22 operations equivalent (22 of 22), and every other operation returns the baseline status code with a well-formed payload.

## Differences

### Equivalence cases

None.

### Other failing items

| Item id | Operation | Nature | Documented | Register entry | Attempts | Artifact | Detail |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `CONF:Rest:DTOError.guid` | — | `unreachable-in-source` | yes | `KL-CONF-DTOERROR` | 0 | — | — |
| `CONF:Rest:DTOError.allErrors` | — | `unreachable-in-source` | yes | `KL-CONF-DTOERROR` | 0 | — | — |
| `CONF:Rest:DTOError.statusCode` | — | `unreachable-in-source` | yes | `KL-CONF-DTOERROR` | 0 | — | — |
| `CONF:Rest:DTOItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTOItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTORetailItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTORetailItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTOTenderItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTOTenderItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO` | 0 | — | — |
| `CONF:Rest:DTOName.guid` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Rest:DTOName.name` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Rest:DTOPhoto.guid` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Rest:DTOPhoto.photo` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Rest:DTOResult.allWarnings` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Rest:DTOTenderSale.offer` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALE-OFFER` | 0 | — | — |
| `CONF:Rest:DTOTenderSaleDetail.guid` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALEDETAIL` | 0 | — | — |
| `CONF:Rest:DTOTenderSaleDetail.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALEDETAIL` | 0 | — | — |
| `CONF:Rest:DTOTenderSaleDetail.client` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALEDETAIL` | 0 | — | — |
| `CONF:Rest:DTOTenderSaleDetail.offer` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALEDETAIL` | 0 | — | — |
| `CONF:Rest:DTOTenderSaleDetail.tenderDate` | — | `unreachable-in-source` | yes | `KL-CONF-DTOTENDERSALEDETAIL` | 0 | — | — |
| `CONF:Rest:STATUS:HTTP_Conflict` | — | `unreachable-in-source` | yes | `KL-CONF-REST-STATUS` | 0 | — | — |
| `CONF:Rest:STATUS:HTTP_Forbidden` | — | `unreachable-in-source` | yes | `KL-CONF-REST-STATUS` | 0 | — | — |
| `CONF:Rest:STATUS:HTTP_Unauthorized` | — | `unreachable-in-source` | yes | `KL-CONF-REST-STATUS` | 0 | — | — |
| `CONF:Soap:DTOCompany.city` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.country` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.email` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.fax` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.name` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.phone` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.street` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOCompany.website` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOError.allErrors` | — | `unreachable-in-source` | yes | `KL-CONF-DTOERROR` | 0 | — | — |
| `CONF:Soap:DTOError.statusCode` | — | `unreachable-in-source` | yes | `KL-CONF-DTOERROR` | 0 | — | — |
| `CONF:Soap:DTOItem.agent` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.description` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.itemCategory` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.listedDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.name` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.number` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.price` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.region` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOItem.sale` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.agent` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.description` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.itemCategory` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.listedDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.name` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.number` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.price` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.region` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailItem.sale` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.agent` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.categoryPrefix` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.description` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.itemCategory` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.listedDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.name` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.number` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.photo` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.price` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.region` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.sale` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderItem.closureDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOResult.allWarnings` | — | `unreachable-in-source` | yes | `KL-CONF-OTHER-UNREACHABLE` | 0 | — | — |
| `CONF:Soap:DTOSale.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOSale.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOSale.item` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSale.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSale.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSale.item` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSale.timestamp` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSale.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSale.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSale.item` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSale.offer` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSale.tender` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOSaleDetail.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOSaleDetail.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSaleDetail.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSaleDetail.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSaleDetail.price` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTORetailSaleDetail.soldDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSaleDetail.agentCommission` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSaleDetail.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSaleDetail.offer` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTenderSaleDetail.tenderDate` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTender.client` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTender.offer` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTender.tenderItem` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:DTOTender.timestamp` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-UNRETURNED-CLASSES` | 0 | — | — |
| `CONF:Soap:STATUS:Error_Conflict` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-STATUS` | 0 | — | — |
| `CONF:Soap:STATUS:Error_Forbidden` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-STATUS` | 0 | — | — |
| `CONF:Soap:STATUS:Error_Server_Error` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-STATUS` | 0 | — | — |
| `CONF:Soap:STATUS:Error_Unauthorized` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-STATUS` | 0 | — | — |
| `CONF:Soap:STATUS:Status_Created` | — | `unreachable-in-source` | yes | `KL-CONF-SOAP-STATUS` | 0 | — | — |
| `CHK-ORDERING` | — | `not-ancestor` | yes | `KL-CHK-ORDERING` | 0 | — | baseline commit 243bb787db7b is not an ancestor of the first facade commit ccf809181b27 |

## Known limitations

- **KL-BASELINE-STANDIN** (substitution) — Not done / why: No live JADE 2022 runtime is used: the baseline under baseline/\*\* is stand-in output (jade-standin), ported from the reference method bodies \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868, 6526-6942\] and the model-schema transaction-agent rules \[Erewhon/ErewhonModelSchema.scm:9794-14146\]. Equivalence therefore proves facade = stand-in, not facade = JADE. Done instead: Stand-in output is labelled in every baseline record, the baseline manifest, the WSDL header comment, the report banner and the console (AAP 0.8.3). Later runs strengthen the stand-in from the in-repo sources before each cut-over. Attempts: 0. Documents 0 failing item(s).
- **KL-REST-PATHS** (assumption) — Not done / why: No REST path appears in the repository, so paths follow the JADE 2022 rule: the verb prefix is removed and the next letter lower-cased, primitive parameters become path segments in signature order, the single object parameter is a JSON body, and the direct form runs on port 65000 \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:82\]. The relay form /\<alias\>/jadehttp.dll/\<path\>?ErewhonREST, which the jadehttp.dll setting admits \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:145\], is not modelled. Done instead: Catalogue path templates follow the rule. The facade's REST catch-all forwards the relay form and every other unmatched request to the backend unchanged (AAP 0.4.5). Attempts: 0. Documents 0 failing item(s).
- **KL-SOAP-ACTIONS** (assumption) — Not done / why: The provider's soapAction properties are blank \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1157\], so the JADE default urn:JadeWebServices/WebServiceHTTP/\<op\> is applied. This disagrees with the request's statement that the repository records none: the generated consumer HTTP\_ErewhonWebService records exactly this form for all 9 operations \[Erewhon/ErewhonWebServiceConsumerSchema.scm:1740-1868\], but it was generated from a 2023 WSDL by runtime 22.0.01 and may lag the provider. Done instead: The catalogue, stand-in and WSDL use the default form. The facade resolves the operation by SOAPAction first, then by bounded body inspection, and forwards unresolved requests to the default target. Attempts: 0. Documents 0 failing item(s).
- **KL-SOAP-WSDL** (assumption) — Not done / why: The provider's wsdl class property is empty \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1135\], the consumer's WSDL path points outside the repository \[Erewhon/ErewhonWebServiceConsumerSchema.scm:1723\], and the \&wsdl query form is undocumented. The stand-in generates the WSDL from the catalogue: namespace urn:JadeWebServices/WebServiceHTTP/, service ErewhonWebService, document/literal wrapped, ArrayOf\* types. Done instead: contracts/wsdl/ErewhonWebService.wsdl is copied from the captured stand-in WSDL with a stand-in header comment. The facade rewrites only soap:address/@location, and WsdlContractCheck compares the facade and backend WSDLs. Attempts: 0. Documents 0 failing item(s).
- **KL-JADE-TESTS-NOT-RUN** (substitution) — Not done / why: The JADE unit tests in the reference schema \[Erewhon/ErehwhonExternalInterfacesSchema.scm:3284-6525\] and the model schema need a JADE runtime and were not run. Done instead: Their expected statuses and messages were traced into the stand-in and the case matrix (AAP 0.9.2), and JadeUnchangedCheck proves that no file under Erewhon/ changed. Attempts: 0. Documents 0 failing item(s).
- **KL-REST-FORMAT** (assumption) — Not done / why: \_restServicesOptions 2 is unexplained \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:87\], so JADE's default Microsoft JSON is assumed: \\/Date(ms)\\/ in UTC (\_webDefaultTimeZone Etc/UTC \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:92\]), \_\_type for subclasses, nulls included, Binary as a byte array, Decimal with two decimals, and the null string as "". XML and JSONN, whether selected by a .xml/.jsonn suffix or by Accept, are neither modelled nor characterised (the stand-in answers 501). Done instead: The characterised forms are unsuffixed requests with no Accept header, and the .json suffix. The facade forwards every form, suffix and header unchanged. Attempts: 0. Documents 0 failing item(s).
- **KL-SOAP-SHAPES** (assumption) — Not done / why: The \<op\>Response/\<op\>Result wrappers, xsi:nil for null results and the exposed-properties-only rule are assumed. The consumer marks results required, xmlNillable \[Erewhon/ErewhonWebServiceConsumerSchema.scm:499, 557\], but types them as HTTP\_DTOObject and uses older types (DTOSaleItem, tenderSaleItem) \[Erewhon/ErewhonWebServiceConsumerSchema.scm:405-466\]. Done instead: The stand-in follows the reference schema and the WebServiceHTTP exposure list \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1793-1932\], with DTOName and String array items \[Erewhon/ErewhonWebServiceConsumerSchema.scm:148-149\]. Attempts: 0. Documents 0 failing item(s).
- **KL-SOAP-DTORESULT** (assumption) — Not done / why: DTOResult is absent from the WebServiceHTTP exposure list \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1793-1932\], yet the four update operations return it \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1236, 1252, 1276, 1292\]. Done instead: The stand-in writes allErrors, allWarnings and statusCode (no guid), and conformance inventories DTOResult on SOAP with those members. Attempts: 0. Documents 0 failing item(s).
- **KL-STANDIN-DATE** (assumption) — Not done / why: The loader offsets every seed date by the days from 2000-01-01 to today \[Erewhon/ErewhonModelSchema.scm:8466-8470\]. The stand-in pins today to StandIn:ReferenceDate 2026-01-01, and its clock starts at 12:00:00 UTC and advances one second per state-changing request. Done instead: Date fields are reproducible. The set of closed tenders is unchanged because every date shifts equally, and TimeStamp values are normalised by N2. At load only PROP/70 closes into a tender sale, because the tenders on the items that close before the reference date are rejected (KL-STANDIN-TENDER-LOAD). Attempts: 0. Documents 0 failing item(s).
- **KL-STANDIN-PHOTO** (assumption; `Soap.getPhoto`) — Not done / why: Photos load through app.loadPicture \[Erewhon/ErewhonModelSchema.scm:8657\], whose stored byte form is not visible. The test constant TestPhoto begins FF D8 FF E0 (JPEG) \[Erewhon/ErehwhonExternalInterfacesSchema.scm:340\]. Done instead: The raw JPEG file bytes are returned base64-encoded and compared byte for byte. Attempts: 0. Documents 0 failing item(s).
- **KL-STANDIN-ROUNDING** (assumption) — Not done / why: calculateAgentCommission computes price x rate / 100 \[Erewhon/ErewhonModelSchema.scm:7527-7556\], and JADE's Decimal assignment rounding is not stated. Done instead: Values are rounded half away from zero to two decimals, which affects agentCommission in the sale and sale-detail DTOs. Attempts: 0. Documents 0 failing item(s).
- **KL-STANDIN-TENDER-LOAD** (assumption; `Rest.getAllSales`) — Not done / why: The seed facts (AAP 0.9.4, 0.7.3) state 160 tenders \[Erewhon/DataFiles/Tenders.txt:1-160\], tender sales closed at load for PROP/70, ANQU/80, ANQU/92 and DEST/106, and getAllSales returning 19 retail and 4 tender sales. That disagrees with the model schema: loadTenders persists every row through TenderTA \[Erewhon/ErewhonModelSchema.scm:8852-8927\], which rejects a tender whose item is already closed \[Erewhon/ErewhonModelSchema.scm:13910-13914\], that is whose closure date is before the current date \[Erewhon/ErewhonModelSchema.scm:7207-7222\]. Every seed date is offset equally, so item 80 (Settee, closure November 29 1999 \[Erewhon/DataFiles/TenderItems.txt:22\]), item 92 (Coffee Mill, November 19 1999 \[Erewhon/DataFiles/TenderItems.txt:34\]) and item 106 (Camel trekking, December 12 1999 \[Erewhon/DataFiles/TenderItems.txt:48\]) close before the reference date, while item 70 (Castle, January 1 2000 \[Erewhon/DataFiles/TenderItems.txt:12\]) closes on it and is still open when its tenders load. The 60 rows on items 80, 92 and 106 \[Erewhon/DataFiles/Tenders.txt:21-40, 81-100, 141-160\] are rejected with "The Tender Item has already closed" \[Erewhon/ErewhonModelSchema.scm:2872\]. Done instead: TenderTA is ported as written and never bypassed, so the stand-in loads 100 tenders and closes one tender sale at load: PROP/70, won by Philip Jenkins with 6500931.00 \[Erewhon/DataFiles/Tenders.txt:137\], the highest of the item's 20 tenders \[Erewhon/DataFiles/Tenders.txt:121-140\]. ANQU/80, ANQU/92 and DEST/106 stay unsold, so getSale returns 404 for each, and items 64, 86, 98 and 100 stay open. getAllSales returns the 19 retail sales and the 1 tender sale, which case REST-getAllSales-01 describes. The outcome does not depend on the reference date, because every seed date shifts equally. Attempts: 0. Documents 0 failing item(s).
- **KL-FACADE-REQUEST-HEAD** (assumption) — Not done / why: The facade forwards a request line of up to 1 MiB including its CRLF and a header block of up to 1 MiB less 3 bytes, counting each header line's CRLF, with any number of headers. A larger request head is refused by the facade's Kestrel with 414 or 431 and never reaches the backend. Kestrel parses a request head only inside its request buffer, which also bounds how much of a body the facade reads ahead of a slow backend, so the bound cannot be lifted without unbounded buffering per connection. The bound is assumed to exceed every backend's own head limit. The stand-in accepts request lines up to 8 KiB, header blocks up to 32 KiB and 100 headers (Kestrel defaults). The ErewhonREST and WebServiceHTTP applications declare JADE 22.0.01 \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:89, 164\], whose web-message limit MaxMessageSize defaults to 1,000,000 bytes for a single-part message. Where requests reach JADE through IIS and jadehttp.dll \[Erewhon/ErehwhonExternalInterfacesSchema.ddx:145, 223\], HTTP.sys limits the request line and headers together to 16,384 bytes by default (MaxRequestBytes), and IIS limits URLs to 4,096 bytes. Done instead: The facade raises Kestrel's request-line and header-size limits (8 KiB and 32 KiB by default) to its 1 MiB request buffer and lifts the 100-header count limit, so every head within the bound is forwarded unchanged and the backend's own limits decide. A backend that accepts larger heads would need only the RequestBufferSize constant in facade/src/Erewhon.Facade/Program.cs raised. Attempts: 0. Documents 0 failing item(s).
- **KL-CONF-DTOERROR** (conformance-gap) — Not done / why: No in-scope method body creates DTOError \[Erewhon/ErehwhonExternalInterfacesSchema.scm:149-153, 2326-2868, 6526-6942\], so none of its members is emitted on either surface. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 5 failing item(s).
- **KL-CONF-REST-STATUS** (conformance-gap) — Not done / why: HTTP\_Unauthorized 401, HTTP\_Forbidden 403 and HTTP\_Conflict 409 are declared \[Erewhon/ErehwhonExternalInterfacesSchema.scm:293-300\] but never assigned by a REST method body \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868\]. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 3 failing item(s).
- **KL-CONF-SOAP-STATUS** (conformance-gap) — Not done / why: Error\_Conflict, Error\_Forbidden, Error\_Unauthorized (declared 403), Error\_Server\_Error and Status\_Created are declared \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1139-1146\] but never assigned by the nine public methods \[Erewhon/ErehwhonExternalInterfacesSchema.scm:6528-6942\]. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 5 failing item(s).
- **KL-CONF-SOAP-UNRETURNED-CLASSES** (conformance-gap) — Not done / why: These classes are SOAP-exposed \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1793-1932\], but no SOAP method returns them: the nine return only DTOAgent, DTOClient, DTONameArray, DTOPhoto and DTOResult \[Erewhon/ErehwhonExternalInterfacesSchema.scm:1148-1307\]. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 68 failing item(s).
- **KL-CONF-DTOTENDERSALEDETAIL** (conformance-gap) — Not done / why: A sold tender item's sale is created as the declared plain DTOSaleDetail \[Erewhon/ErehwhonExternalInterfacesSchema.scm:169, 2208-2209\] and no other REST body creates DTOTenderSaleDetail, so none of its members, its own offer and tenderDate included, is ever emitted. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 5 failing item(s).
- **KL-CONF-DTOITEM-CATEGORYPREFIX-PHOTO** (conformance-gap) — Not done / why: The item population never assigns categoryPrefix or photo \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2156-2177\], so DTOItem, DTORetailItem and DTOTenderItem always carry JADE's null value for them. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 6 failing item(s).
- **KL-CONF-DTOTENDERSALE-OFFER** (conformance-gap) — Not done / why: The tender-sale population never sets DTOTenderSale's own offer; the offer is carried on the nested DTOTender \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2247-2259\]. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 1 failing item(s).
- **KL-CONF-OTHER-UNREACHABLE** (conformance-gap) — Not done / why: DTOName and DTOPhoto are created only by SOAP bodies \[Erewhon/ErehwhonExternalInterfacesSchema.scm:6579, 6638, 6671\], so no REST body \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868\] emits DTOName.guid, DTOName.name, DTOPhoto.guid or DTOPhoto.photo. DTOResult.allWarnings is declared \[Erewhon/ErehwhonExternalInterfacesSchema.scm:199\] but never assigned by any in-scope body on either surface \[Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868, 6528-6942\]. Done instead: registered at once; no correction may close an unreachable-in-source gap (AAP 0.7.7). Attempts: 0. Documents 6 failing item(s).
- **KL-CHK-ORDERING** (check) — Not done / why: The ordering check compares the commit that first added baseline/manifest.json (243bb787db7bb29f52068435958332e28e300cb5) with the oldest commit that added any facade/ path. That oldest commit is ccf809181b2780e292374896f7f0477aba3fa81e, followed by dbf83d0d705b9c94f64bbd64f6a4e60181ec38a5, both environment-setup commits made before the baseline commit. Between them they added only the two facade project files and their NuGet lock files (facade/src/Erewhon.Facade/Erewhon.Facade.csproj, facade/tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj and their packages.lock.json): no facade source, configuration, Dockerfile or test code. Capture never builds or reads facade/, and no baseline commit contains a facade/ path. Done instead: No correction attempt was possible: the check reads committed history, and only rewriting the shared branch's history could change which commit first added a facade/ path, which is not permitted. Every other commit that adds a facade/ path, starting with 455d191113aa38c8ce6c9f5e7ddd0442af2b2b38 (facade/global.json) and including every facade source, Dockerfile and test file from 18e528a5ad58cf10f73e6186abc69c6f74cddc2d onward, descends from the baseline commit (git merge-base --is-ancestor). The check still reports fail in RESULTS.md; this entry documents the observed pair. Attempts: 0. Documents 1 failing item(s).

## How to run

Start the stand-in, the facade and the harness, and replay the baseline through the facade:

```sh
docker compose up --build
```

Point the facade at another backend (no stand-in, so no state reset):

```sh
COMPOSE_PROFILES= BACKEND_REST_URL=http://<host>:65000 BACKEND_SOAP_URL=http://<host>:21001 BACKEND_ADMIN_URL= docker compose up --build
```

Without Docker, capture, export the OpenAPI description or replay with local processes:

```sh
harness/scripts/run-local.sh capture
harness/scripts/run-local.sh export-openapi
harness/scripts/run-local.sh replay
```

Run the facade and harness tests. Each `dotnet` command must run inside its component directory, so that the component's `global.json` SDK and test-runner pins apply:

```sh
(cd facade && dotnet test --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj)
(cd harness && dotnet test --project tests/Erewhon.Harness.Tests/Erewhon.Harness.Tests.csproj)
```

22 of 22 operations equivalent, 0 documented differences
