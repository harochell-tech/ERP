import { describe, expect, it } from "vitest";
import {
  accountingStatusWorthShowing,
  correctionsEmptyText,
  correctionsFilterLabel,
  invoiceStatusLabel,
  isReceivableLocation,
  itemLabel,
  pendingMyApproval,
  previewableLines,
  previewKey,
  printedTotalNotice,
  taxEffectLabel,
  uomLabel,
} from "@/lib/ux4a-compras";

// UX4-02: the pure helpers of Compras, Almacén and CxP (C-07…C-24).

describe("units and items (C-08)", () => {
  it("names the catalogue units and keeps unknown codes", () => {
    expect(uomLabel("t")).toBe("tonelada (t)");
    expect(uomLabel("l")).toBe("litro (L)");
    expect(uomLabel("kg")).toBe("kilogramo (kg)");
    expect(uomLabel("saco")).toBe("saco");
    expect(uomLabel(null)).toBe("—");
  });

  it("does not repeat a description equal to the code", () => {
    expect(itemLabel("ARENA-LAVADA", "ARENA-LAVADA")).toBe("ARENA-LAVADA");
    expect(itemLabel("ARENA-LAVADA", "arena-lavada ")).toBe("ARENA-LAVADA");
    expect(itemLabel("CEM-GU", "Cemento gris")).toBe("CEM-GU — Cemento gris");
    expect(itemLabel("X", "")).toBe("X");
  });
});

describe("supplier invoice status (C-07)", () => {
  it("reads MATCHED as compared with the order and the receipt", () => {
    expect(invoiceStatusLabel("MATCHED")).toBe("Cotejada con OC y recepción");
    expect(invoiceStatusLabel("POSTED")).toBe("Contabilizada");
    expect(invoiceStatusLabel("CLOSED")).toBe("Cerrado");
  });

  it("shows the accounting status only when it adds something", () => {
    expect(accountingStatusWorthShowing("DRAFT", "NOT_POSTED")).toBe(false);
    expect(accountingStatusWorthShowing("MATCHED", "NOT_POSTED")).toBe(false);
    expect(accountingStatusWorthShowing("POSTED", "POSTED")).toBe(false);
    expect(accountingStatusWorthShowing("MATCHED", "POSTING_BLOCKED")).toBe(true);
    expect(accountingStatusWorthShowing("VOIDED", "NOT_POSTED")).toBe(true);
    expect(accountingStatusWorthShowing("MATCHED", "POSTED")).toBe(true);
  });
});

describe("taxes and printed total (C-20, C-23)", () => {
  it("names the tax effects", () => {
    expect(taxEffectLabel("RECOVERABLE_INPUT")).toBe("Crédito fiscal (deducible)");
    expect(taxEffectLabel("NON_RECOVERABLE_INPUT")).toBe("No deducible (va al costo)");
    expect(taxEffectLabel("OTHER")).toBe("OTHER");
  });

  it("says whether the printed total matches, by the sign of the server's difference", () => {
    expect(printedTotalNotice(null, null)).toBeNull();
    expect(printedTotalNotice("11800.00", null)?.text).toMatch(/al contabilizar/);
    expect(printedTotalNotice("11800.00", "0.00")).toEqual({ tone: "done", text: "Coincide con el total calculado por el sistema." });
    expect(printedTotalNotice("11800.00", "-0.50")?.text).toMatch(/menos/);
    expect(printedTotalNotice("11800.00", "0.50")?.text).toMatch(/más/);
  });
});

describe("purchase order preview (C-09)", () => {
  const line = { itemId: "i", uom: "t", quantity: "40", unitPrice: "1,000.00" };

  it("asks only for complete, valid lines, normalized", () => {
    expect(previewableLines([line])).toEqual([{ itemId: "i", uom: "t", quantity: "40", unitPrice: "1000.00" }]);
    expect(previewableLines([line, { ...line, quantity: "" }])).toBeNull();
    expect(previewableLines([{ ...line, unitPrice: "0" }])).toBeNull();
    expect(previewableLines([])).toBeNull();
  });

  it("needs plant, supplier and date", () => {
    expect(previewKey({ plantId: "", partyId: "s", orderDate: "2026-09-30" }, [line])).toBeNull();
    const key = previewKey({ plantId: "p", partyId: "s", orderDate: "2026-09-30" }, [line]);
    expect(JSON.parse(key ?? "null")).toEqual({ plantId: "p", partyId: "s", orderDate: "2026-09-30", lines: [{ itemId: "i", uom: "t", quantity: "40", unitPrice: "1000.00" }] });
  });
});

describe("approvals, locations and corrections (C-10, C-15, C-18)", () => {
  it("counts pending orders not created by the viewer", () => {
    const orders = [
      { status: "PENDING_APPROVAL", createdBy: "Ana" },
      { status: "PENDING_APPROVAL", createdBy: "Luis" },
      { status: "PENDING_APPROVAL" },
      { status: "APPROVED", createdBy: "Luis" },
    ];
    expect(pendingMyApproval(orders, (a) => a === "Ana")).toHaveLength(2);
  });

  it("refuses CURADO and TRANSITO", () => {
    expect(isReceivableLocation("RECEPCION")).toBe(true);
    expect(isReceivableLocation("CURADO")).toBe(false);
    expect(isReceivableLocation("TRANSITO")).toBe(false);
  });

  it("names the corrections filter and explains an empty list", () => {
    expect(correctionsFilterLabel("")).toBe("Todas");
    expect(correctionsFilterLabel("PENDING_APPROVAL")).toBe("Pendientes de aprobación");
    expect(correctionsEmptyText("PENDING_APPROVAL")).toMatch(/nada que decidir/);
    expect(correctionsEmptyText("")).toMatch(/Corregir cantidad/);
  });
});
