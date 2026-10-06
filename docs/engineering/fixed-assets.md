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
