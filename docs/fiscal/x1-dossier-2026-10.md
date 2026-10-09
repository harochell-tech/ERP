# X-1 — Official sources for the open fiscal questions (2026-10-09)

Backs E-X1-1…20 (owner's decisions of 2026-10-09). Same rules as the A-02 dossier (`a02-sources-dossier.md`): confidence **V** — read
in the official text; **D** — official DGII form, instructivo or help answer (ayuda.dgii.gov.do); **S** — secondary only. The DGII site
answers HTTP 403 to non-browser clients; nothing was obtained by disguising a client. Reglamento 293-11 (scanned) was read only on a
secondary site (S); the DGII answers that cite it are D.

## 1. Sales ITBIS

| # | Question | Finding | Source | Conf. |
| --- | --- | --- | --- | --- |
| 1 | Freight billed by the seller | Land transport of cargo is exempt (art. 344.3, as changed by Ley 253-12). But the base of goods is «el precio neto de la transferencia más las prestaciones accesorias que otorgue el vendedor, tales como: transporte, embalaje, fletes… se facturen o no por separado» (art. 339.1, Ley 495-06 art. 19); Reglamento 293-11 art. 10 num. 1 and Párrafo I. DGII answers «Facturación servicio de transporte», «Fletes en ventas locales», «Servicio de transporte por una empresa que no se dedica…»: accessory transport is part of the taxed base. **Owner's decision (E-X1-1): exempt, the company registering bulk-material transport as an activity.** | Título III (`fuentes/titulo3.pdf`); ayuda.dgii.gov.do | V law / D practice |
| 2 | Sand and gravel | Exempt «siempre y cuando se comercialicen en estado natural» (CA119); art. 336.1 «sometidos a algún proceso de transformación». Washed / crushed: no official answer. | ayuda.dgii.gov.do CA119 | D |
| 3 | Taxable event | «En el momento que se emita el documento que ampare la transferencia, o desde que se entregue o retire el bien» (art. 338.1); Reglamento art. 7 a) «lo que suceda primero». | Título III; DGII «Ventas a crédito» | V / D |
| 4 | Advances | Payment is a trigger only for services (art. 338.3); DGII: an advance is not invoiced until delivery. | Título III; DGII «Anticipos de clientes» | V / D |
| 5 | Credit notes > 30 days | Art. 338 Párrafo; Reglamento arts. 8, 28 (ITBIS lost after 30 days); e-CF format v1.0 field `IndicadorNotaCredito`: 0 ≤ 30, 1 > 30 calendar days from the credited e-CF. | Título III; e-CF format (Oct 2025) | V / D |
| 6 | Consumer e-CF 32 buyer | «Si el e-CF es tipo 32 y el monto total es ≥ DOP$250,000.00 se debe identificar RNC Comprador»; below, summary (RFCE, CA4419). | e-CF format v1.0; CA4419 | V / D |
| 7 | Sale of used assets | «La transmisión de bienes industrializados nuevos o usados» (art. 336.2); DGII: the sale of fixed assets is taxed, ITBIS on the full price. | Título III; ayuda.dgii.gov.do | V / D |

## 2. Withholdings and reports

| # | Question | Finding | Source | Conf. |
| --- | --- | --- | --- | --- |
| 8 | ISR withholding (art. 309, Ley 30-26) | IR-17-2026: alquileres 15 %, honorarios 15 %, premios 25 %, transferencia de título 2 %, proveedores del Estado 5 %, otras rentas 15 %, servicios técnicos (Reg. 139-98 art. 70) 3 %, otras retenciones 3 %, regalías / software a no residentes 15 %, remesas 27 % (30 % over RD$1,000 million). | DGII IR-17-2026 form (July 2026); Aviso 10-26 | D / V |
| 9 | ITBIS withholding after NG 02-2026 | Only NG 02-05 withholdings between companies stop when the payee is an e-CF issuer invoicing with e-CF; «no afecta las retenciones… previstas en otras disposiciones» (individuals 100 %, NG 05-19, NG 07-07, RST). Security services (NG 07-09): reading only. | Norma 02-26 | V |
| 10 | Payments abroad | Art. 305: 27 % «pago único y definitivo»; 15 % for royalties, software, advertising, data (Ley 30-26); e-CF 47 required (NG 05-19 art. 9); 609 only services with withholding. | Título II; NG 05-19; DGII answers | V / D |
| 11 | 606 / 607 / 608 | No 607 / 608 for an e-CF-only issuer; the 606 includes received e-CF; an NCF sent again in its payment month keeps its original date. | Instructivo 606 (2026-02); DGII answers | D |
| 12 | DUA ITBIS | Not in the 606; IT-1 Anexo A imports → «ITBIS pagado en importaciones». | DGII answers; IT-1 instructivo | D |
| 13 | Card 2 % | Acquirers withhold ITBIS at 2 % of the amount (NG 06-23 art. 4 a), an advance on the month's IT-1 (art. 6). | Norma 06-23 | V |
| 14 | Exchange differences | Unrealized differences at fiscal year-end are income or deduction (art. 293) at the DGII's rate (RES-DDG-AR1-2025-00001). | Título II | V |

## 3. Expenses, assets, CONFOTUR

| # | Question | Finding | Source | Conf. |
| --- | --- | --- | --- | --- |
| 15 | Telecommunications | ISC 10 % art. 381; CDT 2 % Ley 153-98 arts. 45.1, 47; ITBIS base «el valor total de los servicios prestados, excluyendo la propina obligatoria» (art. 339.3) — without ISC. | `fuentes/titulo4.pdf`, `fuentes/ley153-98.pdf`, Título III | V |
| 16 | Insurance | ISC 16 % on the premium (art. 383, Ley 495-06 art. 28); exempt of ITBIS (art. 344.1); life insurance 11 % 2027, 6 % 2028, then repealed (Ley 30-26 art. 48). | Título IV; Aviso 10-26 | V |
| 17 | Legal tip | 10 % (Ley 16-92 art. 228), outside the ITBIS base (art. 339.3); its base (before ITBIS) is practice, not norm. | `fuentes/ley16-92.pdf` | V / S |
| 18 | ITBIS on assets | «En ningún caso podrán deducirse… el ITBIS pagado en la adquisición de bienes… de Categoría 1» (art. 336 Párrafo); categories 2 / 3 deductible, out of the depreciable value (Reg. 139-98 art. 26 Párr. V). | Título III; Reglamento 139-98 | V |
| 19 | Tax depreciation | Art. 287 e): cat. 1 5 % per asset, cat. 2 25 % pooled, cat. 3 15 % pooled; Reg. 139-98: declining balance, half-year additions, repairs ≤ 5 %; separate book records allowed (art. 18). Ley 30-26 art. 24: double rates for new industrial machinery once listed by Norma General (not yet). | Título II; Reglamento 139-98 | V |
| 20 | CONFOTUR | Ley 158-01 / 195-13: 15 years from end of construction; e-CF 44 without ITBIS; authorization valid 180 days (CA3462, D) vs 60 days (Hacienda page, S). **Owner: 180 days (E-X1-20).** | DGII CA3462; Senate text of Ley 195-13 | D / S |

Corrections to earlier documents: ISC on telecommunications is art. **381** (not 375); ISC on insurance is art. **383** (not 382); the CDT
is Ley **153-98**.

## 4. Still for the accountant

Washed or crushed aggregates (row 2); security services after NG 02-2026 (row 9); the legal basis of the State's 100 % ITBIS withholding
on our sales (an IT-1 line exists); ITBIS on imported services (no official answer found).
