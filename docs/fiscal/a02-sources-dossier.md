# A-02 — Official DGII sources dossier

Backs E-A02-1 (the owner approved A-02 on 2026-09-28 on the DGII's published sources; errors are fixed during testing; real data
still waits for B-02 or a zero-difference parallel run, E-VS1-2). Research date: **2026-09-28**. It is the reference the Analista
fiscal uses to register each source (`RegisterFiscalSource`: title, version, publication date, URL, SHA-256 of the PDF) and the
Especialista fiscal uses to activate each rule version. **The accountant must confirm every value before a PRODUCTION source is
registered**; the open questions are in §5.

Confidence: **V** — read in the official text; **D** — official DGII summary page, brochure or DGII help answer; **S** —
secondary sources only (must be confirmed). The DGII site refuses non-browser downloads (HTTP 403); nothing here was obtained by
disguising a client. Four official PDFs are scanned images without a text layer (Decreto 293-11, Decreto 587-24, Aviso 14-26, and
the Ley 32-23 PDF was too large to fetch): their content is marked S until read by a person.

## 1. Changes in 2026 that the earlier documents did not know

| Change | Effect on the system |
| --- | --- |
| **Ley 30-26** (promulgated 2026-06-18), art. 17: new ISR withholding rates in Código Tributario art. 309 from 2026-07-01 (DGII Aviso 10-26, V); art. 40: tax on cheques and transfers **0.20 %** from 2026-07-03 (V via Aviso 10-26). The DGII's copy of the Code still shows the old 10 % rates. | Supplier ISR withholding rates must come from Ley 30-26, not from the Code's DGII copy. The bank tax is a bank charge line (R-10), not a rule: no code change. |
| **Norma General 02-2026** (2026-09-16, V): the NG 02-05 ITBIS withholdings do not apply when the payee company is an authorised electronic issuer and the operation is invoiced with an e-CF. | A PURCHASE_WITHHOLDING rule for companies must not apply to e-CF invoices of authorised issuers (see §4, G-2). Customers who are companies stop withholding 30 % ITBIS from our e-CF invoices. |
| **e-CF mandatory:** Grandes Locales and Medianos issue **only e-CF from 2026-11-01**, B-series NCF valid to 2026-10-31 (Aviso 14-26, S); Pequeños, Micro and No Clasificados by **2026-11-15** (Ley 32-23 art. 37, Aviso 14-25 V, Aviso 06-26 V). | VS#4 (e-CF gateway, blocked on X-1) becomes date-critical: until it exists, invoices are issued in the provider's portal and recorded with `RecordExternalFiscalDocument` (E-VS3-05). |

## 2. Sources for the rules the system has

| # | Topic | Document and article | Rule | URL | Conf. |
| --- | --- | --- | --- | --- | --- |
| 1 | ITBIS scope | Código Tributario, Ley 11-92 (1992-05-16), Título III, arts. 335, 336.1 | ITBIS taxes transfers of *bienes industrializados*, imports and services. | https://dgii.gov.do/legislacion/codigotributario/cdigo%20tributario/titulo3.pdf | V |
| 2 | ITBIS rate | Same, art. 345 as modified by Ley 253-12 (2012-11-09) | Text: 18 % for 2013–2014 and 16 % from 2015 subject to a tax-pressure condition (Párrafo I); **18 % is the rate applied**. | same | V text / D 18 % in practice |
| 3 | Our goods are taxed | Same, arts. 343 (exempt goods), 344 (exempt services) | No exemption for cement (heading 25.23) or concrete articles (68.10): **blocks, pavers and cement are taxed**. Land freight transport is an exempt service (344.3). | same | V |
| 4 | Input ITBIS | Same, arts. 346, 347, 349, 350; art. 336 Párrafo (Ley 495-06) | ITBIS paid to local suppliers and on imports is deductible in the same period with a valid NCF / e-CF; mixed use is prorated; excess carries forward; ITBIS on category-1 fixed assets is not deductible. | same | V |
| 5 | ITBIS regulation | Decreto 293-11 (2011), Reglamento del ITBIS | Scanned PDF, not read. | https://dgii.gov.do/legislacion/reglamentos/Documents/2011/293-11.pdf | S |
| 6 | ITBIS withholding (buyer) | Norma General 02-05 (2005-01-17), modified by NG 13-07 and NG 07-09, arts. 1–5, 9 | Companies withhold **30 % of the invoiced ITBIS** when paying companies for professional services or rental of movable goods; the withholding is a payment on account for the supplier. **Services only — not purchases of goods.** | https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Impuesto%20sobre%20Transferencias%20de%20Bienes%20Industrializados%20y%20Servicios%20(ITBIS)/norma02-05.pdf | V |
| 7 | Other ITBIS withholdings | NG 07-09 (security services, 100 %), NG 01-11 and DGII brochure (taxed services from personas físicas, 100 %), NG 05-19 (Comprobante de Compras B11 / e-CF 41, 100 %), NG 07-07 (construction: 100 % persona física, 30 % company) | As stated. | https://dgii.gov.do/publicacionesOficiales/bibliotecaVirtual/contribuyentes/retencionesRetribucionesComplementarias/Documents/Retenciones-ISR-ITBIS.pdf | D |
| 8 | End of NG 02-05 for e-CF | NG 02-2026 (2026-09-16), single article | See §1. | https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Impuesto%20sobre%20Transferencias%20de%20Bienes%20Industrializados%20y%20Servicios%20(ITBIS)/Norma02-26.pdf | V |
| 9 | ISR withholding (buyer) | Código Tributario art. 309 (Título II), as modified by **Ley 30-26 art. 17**; DGII Aviso 10-26 (2026-06-26) | New rates from 2026-07-01. Secondary sources: 15 % on fees to personas físicas (payment on account), 15 % on rentals to personas físicas (final), 5 % on payments by the State unchanged. DGII answer: technical services 15 % × 20 % presumed net income (Reglamento 139-98 art. 70) = 3 % effective. Old text (before Ley 30-26): 10 %. | https://dgii.gov.do/legislacion/codigotributario/cdigo%20tributario/titulo2.pdf ; https://dgii.gov.do/publicacionesOficiales/avisosInformativos/Documents/2026/10-26.pdf | V aviso / D 3 % / S 15 % |
| 10 | Withholding by customers | Código Tributario arts. 309 (e), 310, 311, 313; NG 02-05 art. 9; NG 07-18 art. 4; NG 06-23 | The State withholds 5 % ISR on its purchases of goods; the withholding agent gives proof of the withholding (art. 311); NG 02-05 withholdings are credited on the IT-1; customers' withholdings are reported in the 607; card acquirers withhold 2 % of the amount, credited on the IT-1 (NG 06-23). | titulo2.pdf; brochure (row 7) | V Code / D card 2 % |
| 11 | Taxable event of sales | Código Tributario **art. 338.1** | For goods, ITBIS arises **when the document covering the transfer is issued**, or at delivery / withdrawal if there is none. Services: invoice, completion or payment, whichever is first. | titulo3.pdf (row 1) | V |
| 12 | Credit notes | Código Tributario art. 338 Párrafo; Decreto 293-11 arts. 8 and 28 | A transfer annulled within **30 days** of the taxable event annuls the ITBIS; after 30 days only the price is refunded (credit note without ITBIS). DGII answer CA1064 cites art. 338 and art. 8. | titulo3.pdf; ayuda.dgii.gov.do CA1064 | V Code / D Reglamento |
| 13 | Electronic invoicing | Ley 32-23 (2023), art. 37 (calendar), arts. 26–29 (penalties); Decreto 587-24 (reglamento, 2024-10-10, S) | Calendar in §1. | https://dgii.gov.do/publicacionesOficiales/avisosInformativos/Documents/2025/14-25.pdf ; https://dgii.gov.do/publicacionesOficiales/avisosInformativos/Documents/2026/06-26.pdf | V avisos / S decree |
| 14 | e-CF types and e-NCF | DGII page "Tipo y estructura e-CF"; Aviso 24-19 | 31 crédito fiscal, 32 consumo, 33 nota de débito, 34 nota de crédito, 41 compras, 43 gastos menores, 44 regímenes especiales, 45 gubernamental, 46 exportación, 47 pagos al exterior. **e-NCF = E + 2-digit type + 10 digits** (13 characters). | https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/TipoyEstructurae-CF.aspx | D |
| 15 | B-series NCF | Decreto 254-06; NG 06-2018 (2018-02-01) | **NCF = B + 2-digit type + 8 digits** (11 characters); credit and debit notes cite the NCF they modify. | DGII NCF guide; Norma06-18.pdf | D structure / V NG |
| 16 | Reports | NG 07-2018 (2018-03-09), modified by NG 10-18, arts. 3–6, 8; Código Tributario art. 353 | 606 purchases, 607 sales, 608 voided, 609 payments abroad: first 15 days of the next month, also with zero values. IT-1 monthly by day 20; IR-17 by day 10. e-CF sales are not reported in the 607 (DGII help, D). | https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Comprobantes%20Fiscales/Norma07-18.pdf | V / D |
| 17 | Cheques and transfers | Código Tributario art. 382 restored by Ley 288-04 (2004-09-28); Ley 30-26 art. 40 | 0.15 % until 2026-07-02, **0.20 % from 2026-07-03**; excludes cash withdrawals, card spending, social security, pension funds, tax payments. | https://dgii.gov.do/legislacion/leyesTributarias/Documents/Codigo%20Tributario%20y%20Leyes%20que%20lo%20modifican%20y%20complementan/288-04.pdf ; Aviso 10-26 | V |
| 18 | RNC registry | DGII "RNC Contribuyentes", DGII_RNC.zip (weekly) | Imported by E-RNC-1…8. | https://dgii.gov.do/herramientas/consultas/Paginas/RNC.aspx | D |

## 3. What to register in the system (per rule kind)

| Rule kind | Version to configure (after the accountant confirms) | Sources to link |
| --- | --- | --- |
| PURCHASE_ITBIS | `ITBIS`, rate `0.18`, effect RECOVERABLE_INPUT; exempt categories only if the accountant rules that sand and gravel (AGREGADO) are not *bienes industrializados* (Q-2). | Rows 1–4 (and 5 once read) |
| PURCHASE_WITHHOLDING | Only for services (§4, G-1): ISR per Ley 30-26 by payee type; ITBIS 30 % (companies, services, B-series only after NG 02-2026) and 100 % (personas físicas, services). | Rows 6–9 |
| SALES_ITBIS | `ITBIS`, rate `0.18`, effect OUTPUT, no exempt categories (row 3). | Rows 1–3, 11 |

## 4. Gaps between the sources and the system

These are findings, not decisions: each becomes a numbered errata when its slice is built.

| # | Gap | Source | Present behaviour |
| --- | --- | --- | --- |
| G-1 | Withholdings apply to **services**, not to purchases of goods; PURCHASE_WITHHOLDING has no goods/services dimension, so a rule for INDIVIDUAL would also withhold on a persona física's sale of aggregates. | Rows 6, 7, 9 | Configure no PURCHASE_WITHHOLDING rule for raw-material suppliers until the dimension exists (VS#1 invoices are raw-material purchases). |
| G-2 | NG 02-2026: no NG 02-05 withholding on e-CF invoices of authorised electronic issuers. | Row 8 | Rules cannot see the supplier's document type (B vs E). |
| G-3 | Credit notes after 30 days carry no ITBIS. | Row 12 | The credit note always uses the invoice line's rate (E-VS3-06-2). |
| G-4 | Taxable event: art. 338.1 ties it to the document covering the transfer. If the delivery note (*conduce*) counts, ITBIS arises at delivery. | Row 11 | Invoice date (E-VS3-10), with the 30-day unbilled alert (E-VS3-08-3). Consistent while each delivery is invoiced in the same period. |
| G-5 | Customer withholdings are typed from the certificate, without a rule. | Row 10 | Acceptable: the certificate is the evidence (E-VS3-07-7). The 2 % card-acquirer withholding has no flow yet. |
| G-6 | e-CF only from 2026-11-01 / 2026-11-15. | Row 13 | Manual path with `RecordExternalFiscalDocument` until VS#4 (X-1: provider). |
| G-7 | Reports 606 / 607 / 608 and the IT-1, IR-17 figures. | Row 16 | Not built (deferred in the VS#2 / VS#3 baselines). |

## 5. Open questions for the accountant

1. ~~Block Rochell's DGII size class and electronic-issuer status~~ — **answered 2026-09-29 (E-A02-2): Mediano, already an authorised electronic issuer** → e-CF only from 2026-11-01.
2. Are sand and gravel *bienes industrializados* (taxed)?
3. Does the *conduce* count as the "document covering the transfer" (art. 338.1)?
4. The art. 309 rates after Ley 30-26 that apply to our suppliers, and their IR-17 boxes.
5. After NG 02-2026, which ITBIS withholdings do we still apply as buyer, and which can customers (especially State entities: 5 % ISR) still apply to us?
6. How to issue credit notes after 30 days as e-CF 34, and how they go to the IT-1 and 606 / 607.
7. Which of 606 / 607 / 608 we still file once we issue only e-CF.
8. Whether the 2 % card-acquirer withholding applies to our card sales.
