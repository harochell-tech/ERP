# Fixed assets (AF-1)

Baseline: `docs/architecture/af1/frozen-baseline-af1.md` (E-AF-1…12). Book depreciation only; tax depreciation (DGII art. 287) is out of
scope until the accountant defines it (X-1).

## AF1-01 — schema (migration 0092, E-AF1-01-1…9)

Schema `fa`:

| Table | What it holds |
| --- | --- |
| `fa.asset_class` | A fixed-asset expense category (ASSET account, 606 type 04) completed with `useful_life_months`, `residual_pct`, the accumulated depreciation account (ASSET) and the depreciation expense account (EXPENSE or COST). DRAFT → ACTIVE (four eyes) → SUPERSEDED by the next version; one ACTIVE and one DRAFT per category. An approved version never changes (E-AF1-01-3). |
| `fa.asset` | The card `AF-YYYY-NNNNNN`: born AWAITING_SERVICE from a posted invoice line (one live card per line), or IN_SERVICE from the initial load. On service it copies its class version, life and residual, which never change afterwards (E-AF1-01-2). Keeps `cost`, `accumulated` and `months_depreciated`. AWAITING_SERVICE → IN_SERVICE → DISPOSED; CANCELLED with its invoice only while it has no depreciation (E-AF1-01-5). |
| `fa.asset_movement` | Append-only history of a card: acquisition, cost added / removed, service, transfer, depreciation (and its undo), disposal, opening, cancellation. |
| `fa.depreciation_run` / `_line` | One POSTED run per month with a line per card and plant; UNDONE to redo it. |
| `fa.asset_disposal` | SCRAP or SALE (with price), prepared by the Contador, approved and posted by the Controller (four eyes); one live disposal per card. |
| `fa.asset_load` / `_line` | The initial load from a CSV: code, description, category, plant, purchase date, cost, accumulated at the cut-off; posted on approval (four eyes). |

Every status change has its `core.state_history` row (aggregates `AssetClass`, `FixedAsset`, `DepreciationRun`, `AssetDisposal`, `AssetLoad`).

Accounting (rules seeded DRAFT, close component **FA-REC**):

| Rule | Event | Lines |
| --- | --- | --- |
| P-44 | `DepreciationPosted` | Dr FIXED_ASSET_DEPRECIATION (plant) / Cr FIXED_ASSET_ACCUMULATED |
| P-45 | `AssetDisposalPosted` | Dr FIXED_ASSET_ACCUMULATED, Dr ASSET_SALE_RECEIVABLE (price), Dr ASSET_DISPOSAL_LOSS / Cr FIXED_ASSET_COST, Cr ASSET_DISPOSAL_GAIN |
| P-46 | `AssetLoadPosted` | Dr FIXED_ASSET_COST / Cr FIXED_ASSET_ACCUMULATED, Cr MIGRATION_CLEARING (book value) |

`FIXED_ASSET_COST`, `FIXED_ASSET_ACCUMULATED` and `FIXED_ASSET_DEPRECIATION` are technical roles like `PURCHASE_EXPENSE`: the account comes
from the card's category or class and they are never mapped (E-AF1-01-7). `ASSET_SALE_RECEIVABLE`, `ASSET_DISPOSAL_GAIN` and
`ASSET_DISPOSAL_LOSS` are mapped by the Controller (A-01).

Permissions: `fixed_asset:manage` (CONTADOR) and `fixed_asset:approve` (CONTROLLER), an SoD pair (E-AF-11); reading with `ledger:read`.
FA-REC is OPEN in every existing period and in the periods `rochell-migrate` creates.

The module `Rochell.FixedAssets` (E-AF1-01-1: uses Finance and MasterData; Procurement may use it to create cards when it posts) arrives
with its first commands in AF1-02.

## AF1-02 — module, classes, cards (E-AF1-02-1…10)

Module `Rochell.FixedAssets` (Finance and MasterData; Procurement uses it, E-AF1-01-1). API group `/fixed-assets`; 238 commands.

| Command | Permission | What it does |
| --- | --- | --- |
| `PrepareAssetClass` | `fixed_asset:manage` | DRAFT class for a fixed-asset category: life 1…600 months, residual 0 to under 100 %, accumulated depreciation (ASSET) and depreciation (EXPENSE or COST) accounts; the next `class_version`; one draft per category. |
| `ApproveAssetClass` | `fixed_asset:approve` (step-up) | Someone other than the preparer; the category's ACTIVE class becomes SUPERSEDED, the draft ACTIVE (E-AF1-01-3). |
| `DiscardAssetClass` | `fixed_asset:manage` | DRAFT → DISCARDED. |
| `PutFixedAssetInService` | `fixed_asset:manage` | Date not before the purchase, plant, who is in charge (1–120 characters); copies the ACTIVE class's life and residual (E-AF1-01-2/4, E-AF1-02-6). |
| `TransferFixedAsset` | `fixed_asset:manage` | Another plant from a date after the last depreciated month, not before the last move and not after today; no journal (E-AF-9, E-AF1-02-5). |
| `UpdateFixedAsset` | `fixed_asset:manage` | Description and who is in charge of a live card (E-AF1-02-7). |
| `CreateCardsForPostedInvoices` | `fixed_asset:manage` | Cards for fixed-asset lines of invoices posted before AF-1, with their posted settlements' cost; idempotent (E-AF1-02-8). |

`FixedAssetCards` is what Procurement calls inside its own transactions:

- posting an expense invoice (P-37 or P-38) creates an AWAITING_SERVICE card `AF-YYYY-NNNNNN` per line whose category is a fixed asset
  (606 type 04 on an asset account): cost = the line's peso net as posted (in USD, converted with the rounding cent), description from the
  line, the invoice's plant, purchase date = the invoice date (E-AF-3, E-AF1-02-1/2);
- reversing the invoice cancels its live cards, refused with `FIXED_ASSET_DEPRECIATED` / `FIXED_ASSET_DISPOSED` (E-AF1-02-4);
- approving a settlement adds each goods line's share to its card (`COST_ADDED`); refused with `FIXED_ASSET_DISPOSED` for a disposed card
  and `FIXED_ASSET_IN_SETTLEMENT` when a fixed-asset line is one of the settled costs (E-AF1-02-3/10);
- reversing a settlement takes the cost back (`COST_REMOVED`), refused with `FIXED_ASSET_COST_BELOW_DEPRECIATION` when the card would
  fall below what it already depreciated (E-AF1-02-4).

Every card change is its own `FixedAsset` event caused by the document's event, with an `fa.asset_movement` row.

Queries (`ledger:read`, E-AF1-02-9): `GET /fixed-assets/classes` (`ListAssetClasses`), `GET /fixed-assets/assets` (`ListFixedAssets`, filters
status / plant / category), `GET /fixed-assets/assets/{assetId}` (`GetFixedAsset`: source invoice and supplier, class, residual value, what is
left to depreciate, months remaining, movements, history).

Tests: `tests/Rochell.Procurement.Tests/FixedAssetCardTests.cs` (AF-01…04, AF-10).

## AF1-03 — depreciation and disposals (E-AF1-03-1…11)

| Command | Permission | What it does |
| --- | --- | --- |
| `PostDepreciation` | `fixed_asset:manage` (step-up) | Month (any day of it), from its last day, period and FA-REC open; months in order (`DEPRECIATION_MONTH_SKIPPED`); every IN_SERVICE card whose next month is this one: (cost − residual − accumulated) ÷ months left, 2 decimals, the last month exact. One P-44 journal on the month's last day with a line pair per card: depreciation of the class (plant in force on the last day) / accumulated of the class. |
| `UndoDepreciation` | `fixed_asset:manage` (step-up) | Only the latest POSTED month, while its period is open and none of its cards was disposed of; reverses the journal on its date and restores the cards. |
| `PrepareAssetDisposal` | `fixed_asset:manage` | SCRAP or SALE (price) of a whole card in service, dated between its purchase and today, depreciated up to the month before (`FIXED_ASSET_DEPRECIATION_PENDING`); one live disposal per card. |
| `CancelAssetDisposal` | `fixed_asset:manage` | DRAFT → CANCELLED. |
| `ApproveAssetDisposal` | `fixed_asset:approve` (step-up) | Not the preparer; checks the card again and posts P-45 on the disposal date: Dr accumulated (class), Dr «Venta de activos por cobrar» (price), Dr «Pérdida en baja de activos» / Cr cost (category), Cr «Ganancia en venta de activos»; the card is DISPOSED. |

A card's next month to depreciate is the month after its service plus the months it already depreciated. Disposal is for cards in
service only (E-AF1-03-11): one awaiting service is put into service first. «Venta de activos por cobrar» is cleared with a manual adjustment
until Sales invoices assets (E-AF1-03-9, X-1). 243 commands.

Queries (`ledger:read`): `GET /fixed-assets/depreciation-runs` (`ListDepreciationRuns`), `GET /fixed-assets/disposals` (`ListAssetDisposals`).

The posting engine takes the account of `FIXED_ASSET_COST` / `_ACCUMULATED` / `_DEPRECIATION` lines from the command, like
`PURCHASE_EXPENSE` (`PostingEngine.TakesDocumentAccount`, E-AF1-01-7 — part of the B-02 review); the role-map screens and the setup status
leave them out.

Tests: `tests/Rochell.Procurement.Tests/FixedAssetDepreciationTests.cs` (AF-05…08, with a `FakeClock` moved month by month).

## AF1-04 — initial load and FA-GL (migration 0093, E-AF1-04-1…9)

| Command / query | Permission | What it does |
| --- | --- | --- |
| `POST /fixed-assets/loads/preview` (`PreviewAssetLoad`) | `fixed_asset:manage` | CSV or Excel (the import reader) with Código, Descripción, Categoría, Planta, Fecha de compra, Costo, Depreciación acumulada, at a cut-off that is a month's last day: each row with months depreciated, life, residual and monthly amount, or its error. |
| `PrepareAssetLoad` | `fixed_asset:manage` | DRAFT load; refused whole when one row has an error (`ASSET_LOAD_ROWS_INVALID`). |
| `DiscardAssetLoad` | `fixed_asset:manage` | DRAFT → DISCARDED. |
| `ApproveAssetLoad` | `fixed_asset:approve` (step-up) | Not the preparer; the rows judged again; a card IN_SERVICE per row (`OPENING`, AF-YYYY of the cut-off, the old code kept), P-46 on the cut-off: Dr cost (category) / Cr accumulated (class), Cr MIGRATION_CLEARING (book value). |
| `ReverseAssetLoad` | `fixed_asset:approve` (step-up) | While no card was depreciated or disposed of since: the journal reversed, the load REVERSED, its cards CANCELLED. |
| `GET /fixed-assets/loads` (`ListAssetLoads`) | `ledger:read` | The loads with rows, cost and accumulated. |

Row rules (E-AF1-04-3/4): months depreciated = whole months from the month after the purchase to the cut-off (capped at the life); the
purchase date is the service date, so depreciation continues the month after the cut-off. A row is refused when its category is not a
fixed asset or has no approved class, the plant does not exist, the date is missing or after the cut-off, the accumulated exceeds cost −
residual, the life ended with something left, or its code repeats in the file or on a live card. Dates: `AAAA-MM-DD`, `DD/MM/AAAA` or an
Excel date.

**FA-GL** (blocks FA-REC): per fixed-asset account, Σ cost of the cards awaiting service or in service = its balance
(`FA_COST_DIFFERENCE`); per accumulated-depreciation account of a class, Σ their accumulated = its balance (`FA_ACCUMULATED_DIFFERENCE`);
every ended month a card in service still had to depreciate (`FA_DEPRECIATION_MISSING`): a warning, an error (blocking FA-REC) for the
cutoff's own month (E-AF1-04-8). The FA-REC close snapshot holds the cards by status and the fixed-asset accounts. 35 reconciliations;
247 commands.

Tests: `tests/Rochell.Procurement.Tests/FixedAssetLoadTests.cs` (AF-09, AF-11).
