// UX3-02 (E-UX3-4): Explain in words. The codes the Explain document carries (journal type, event type, command, posting rule,
// integrity status, document kind) in Spanish; an unknown code is shown as it comes. Pure, unit-tested.

const JOURNAL_TYPES: Readonly<Record<string, string>> = {
  AUTO: "Automático",
  REVERSAL: "Reversa",
  VALUATION_REALLOCATION: "Reasignación de valuación",
  MANUAL_ADJUSTMENT: "Ajuste manual",
};

const EVENT_TYPES: Readonly<Record<string, string>> = {
  GoodsReceiptPosted: "Recepción de mercancía contabilizada",
  GoodsReceiptReversed: "Recepción de mercancía reversada",
  ReceiptCorrectionPosted: "Corrección de recepción contabilizada",
  SupplierInvoicePosted: "Factura de proveedor contabilizada",
  SupplierInvoiceReversed: "Factura de proveedor reversada",
  SupplierInvoiceVoided: "Factura de proveedor anulada",
  ValuationResidualAdjusted: "Residuo de valuación ajustado",
  SupplierPaymentReleased: "Pago a proveedor liberado",
  SupplierPaymentVoided: "Pago a proveedor anulado",
  PaymentReversed: "Pago reversado",
  PaymentCleared: "Pago compensado",
  BankChargeRecognized: "Cargo bancario registrado",
  OpeningInventoryPosted: "Inventario de apertura contabilizado",
  OpeningInventoryReversed: "Inventario de apertura reversado",
  GoodsIssued: "Salida de mercancía (despacho)",
  GoodsReturnedFromTransit: "Mercancía devuelta del tránsito",
  ControlTransferred: "Control transferido al cliente",
  TransitLossRecognized: "Pérdida en tránsito registrada",
  InvoiceIssued: "Factura emitida",
  InvoiceVoided: "Factura anulada",
  CreditNoteIssued: "Nota de crédito emitida",
  ReceiptRecorded: "Recibo de cobro registrado",
  ReceiptReversed: "Recibo de cobro reversado",
  ReceiptBounced: "Cheque devuelto",
  ReceiptApplied: "Cobro aplicado a facturas",
  WithholdingByCustomer: "Retención hecha por el cliente",
  CustomerWithholdingReversed: "Retención del cliente reversada",
  CashInTransitDeposited: "Efectivo en tránsito depositado",
  StandardCostRevalued: "Costo estándar revaluado",
  MaterialConsumed: "Consumo de materiales",
  ProductionReceived: "Producción recibida en curado",
  ScrapRecorded: "Desecho registrado",
  ShiftSummaryReversed: "Resumen de turno reversado",
  CostCollectorSettled: "Costos de producción liquidados",
  ManualJournalApproved: "Ajuste manual aprobado",
  ManualJournalReversed: "Ajuste manual reversado",
  JournalReposted: "Asiento recontabilizado (corrección de mapeo)",
};

const COMMANDS: Readonly<Record<string, string>> = {
  "Procurement.PostGoodsReceipt": "Registrar recepción de mercancía",
  "Procurement.ReverseGoodsReceipt": "Reversar recepción",
  "Procurement.ApproveReceiptCorrection": "Aprobar corrección de recepción",
  "Procurement.PostSupplierInvoice": "Contabilizar factura de proveedor",
  "Procurement.ReverseSupplierInvoice": "Reversar factura de proveedor",
  "Procurement.VoidSupplierInvoice": "Anular factura de proveedor",
  "Inventory.ApproveValuationResidualAdjustment": "Aprobar ajuste de residuo de valuación",
  "Treasury.ReleaseSupplierPayment": "Liberar pago a proveedor",
  "Treasury.VoidPayment": "Anular pago",
  "Treasury.ReversePayment": "Reversar pago",
  "Treasury.RecognizeBankCharge": "Registrar cargo bancario",
  "Treasury.MatchBankLine": "Conciliar línea del extracto",
  "Treasury.MatchBankLineToReceipt": "Conciliar línea del extracto con un cobro",
  "Sales.PostOpeningInventory": "Contabilizar inventario de apertura",
  "Sales.ReverseOpeningInventory": "Reversar inventario de apertura",
  "Sales.RecordGateOut": "Registrar salida por portería",
  "Sales.RecordPod": "Registrar prueba de entrega",
  "Sales.RecordReturnTrip": "Registrar viaje de regreso",
  "Sales.IssueInvoice": "Emitir factura",
  "Sales.VoidUnfiscalizedInvoice": "Anular factura sin e-CF",
  "Sales.IssueCreditNote": "Emitir nota de crédito",
  "Sales.RecordReceipt": "Registrar recibo de cobro",
  "Sales.ReverseReceipt": "Reversar recibo de cobro",
  "Sales.MarkReceiptBounced": "Marcar cheque devuelto",
  "Sales.ApplyReceipt": "Aplicar cobro",
  "Sales.UnapplyReceipt": "Desaplicar cobro",
  "Sales.RecordCustomerWithholding": "Registrar retención del cliente",
  "Sales.ReverseCustomerWithholding": "Reversar retención del cliente",
  "Sales.DepositReceipts": "Depositar cobros",
  "Sales.ApproveStandardCost": "Aprobar costo estándar",
  "Manufacturing.PostShiftSummary": "Contabilizar resumen de turno",
  "Manufacturing.ReverseShiftSummary": "Reversar resumen de turno",
  "Manufacturing.ScrapLot": "Desechar lote",
  "Manufacturing.SettleCostCollector": "Liquidar costos de producción",
  "Finance.ApproveManualJournal": "Aprobar ajuste manual",
  "Finance.ReverseManualJournal": "Reversar ajuste manual",
  "Finance.RepostEvent": "Recontabilizar evento",
};

/** Posting rules by code (§13, VS#2, VS#3, MFG-1, FIN-1). */
const RULES: Readonly<Record<string, string>> = {
  "R-01": "Recepción de mercancía",
  "R-02B": "Reversa de recepción",
  "R-03A": "Corrección de recepción (cantidad)",
  "R-03B": "Corrección de recepción (precio)",
  "R-04": "Factura de proveedor (cuenta por pagar e ITBIS)",
  "R-05": "Factura de proveedor (diferencia de precio)",
  "R-06": "Ajuste de residuo de valuación",
  "R-07B": "Reversa de factura de proveedor",
  "R-09": "Pago a proveedor",
  "R-10": "Cargo bancario",
  "OPEN-INV": "Inventario de apertura",
  "P-08": "Consumo de materiales",
  "P-10": "Producción recibida en curado",
  "P-12": "Desecho de producto terminado",
  "P-13": "Liquidación de costos de producción",
  "P-15": "Salida de mercancía en tránsito",
  "P-15R": "Devolución del tránsito",
  "P-16": "Transferencia de control al cliente",
  "P-18": "Factura de venta",
  "P-22": "Nota de crédito",
  "P-23": "Recibo de cobro",
  "P-24": "Cheque devuelto",
  "P-25": "Aplicación de cobro",
  "P-27": "Retención del cliente",
  "P-29": "Depósito de efectivo en tránsito",
  "P-30": "Pérdida en tránsito",
  "P-34": "Ajuste manual",
  REVAL: "Revaluación de costo estándar",
};

const INTEGRITY: Readonly<Record<string, string>> = {
  PENDING_SEAL: "Pendiente de sellar",
  SEALED: "Sellado en la cadena de integridad",
  SEAL_ERROR: "Error al sellar",
};

const DOCUMENT_KINDS: Readonly<Record<string, string>> = {
  GOODS_RECEIPT: "Recepción",
  GOODS_RECEIPT_REVERSAL: "Reversa de la recepción",
  RECEIPT_CORRECTION: "Corrección de la recepción",
  SUPPLIER_INVOICE: "Factura de proveedor (NCF)",
};

function known(map: Readonly<Record<string, string>>, code: string | null | undefined): string {
  return code ? (map[code] ?? code) : "—";
}

export const journalTypeLabel = (code: string | null | undefined) => known(JOURNAL_TYPES, code);
export const eventTypeLabel = (code: string | null | undefined) => known(EVENT_TYPES, code);
export const integrityLabel = (code: string | null | undefined) => known(INTEGRITY, code);
export const documentKindLabel = (code: string | null | undefined) => known(DOCUMENT_KINDS, code);

/** A command type ("Procurement.PostGoodsReceipt") in Spanish; an unknown one as it comes. */
export function commandLabel(code: string | null | undefined): string {
  if (!code) {
    return "—";
  }
  return COMMANDS[code] ?? code;
}

/** "R-01 · Recepción de mercancía" (the code alone when unknown). */
export function ruleLabel(code: string): string {
  const name = RULES[code];
  return name ? `${code} · ${name}` : code;
}

/** The screen of the source document, by kind and id (null when there is none to open). */
export function documentHref(kind: string, id: string | null | undefined): string | null {
  switch (kind) {
    case "GOODS_RECEIPT":
      return id ? `/almacen/recepcion/?id=${id}` : null;
    case "SUPPLIER_INVOICE":
      return id ? `/cxp/factura/?id=${id}` : null;
    // The reversal and the correction have no detail screen of their own: their lists.
    case "GOODS_RECEIPT_REVERSAL":
      return "/almacen/recepciones/";
    case "RECEIPT_CORRECTION":
      return "/almacen/correcciones/";
    default:
      return null;
  }
}
