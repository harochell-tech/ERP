import { describe, expect, it } from "vitest";
import { lineStatusLabel, statusLabel, statusTone } from "@/lib/labels";

// E-UI-5: treasury labels and the colour tone that always travels with them.
describe("treasury labels", () => {
  it("names the payment and line statuses in Spanish", () => {
    expect(["PREPARED", "RELEASED", "CLEARED", "VERIFIED", "SUPERSEDED", "UNMATCHED", "CHARGE_RECOGNIZED"].map(statusLabel)).toEqual([
      "Preparado",
      "Liberado",
      "Compensado",
      "Verificada",
      "Reemplazada",
      "Sin conciliar",
      "Cargo registrado",
    ]);
  });

  it("calls a matched statement line 'Conciliada' and keeps 'Conciliado' for invoices", () => {
    expect(lineStatusLabel("MATCHED")).toBe("Conciliada");
    expect(statusLabel("MATCHED")).toBe("Conciliado");
    expect(lineStatusLabel("UNMATCHED")).toBe("Sin conciliar");
  });

  it("gives every status a tone, unknown ones neutral", () => {
    expect([statusTone("PREPARED"), statusTone("CLEARED"), statusTone("UNMATCHED"), statusTone("REJECTED"), statusTone("REVERSED"), statusTone("WHATEVER")]).toEqual([
      "progress",
      "done",
      "attention",
      "error",
      "reversed",
      "neutral",
    ]);
  });
});
