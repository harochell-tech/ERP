import { describe, expect, it } from "vitest";
import { alertsText, certifiableDates, certificateVoidText, parameterText, parseRackQr, qualityActions, specimenBody, todayIso, verdictBadge } from "@/lib/lab";

describe("lab helpers (LAB1-01)", () => {
  it("sends empty measures as null so the server uses the item's nominal ones, and never computes", () => {
    expect(specimenBody({ widthCm: "", heightCm: " ", lengthCm: "39.4", weightKg: "", loadKg: "75,187", blockCondition: "", failureType: "CONICA", notes: "  " })).toEqual({
      loadKg: "75187",
      widthCm: null,
      heightCm: null,
      lengthCm: "39.4",
      weightKg: null,
      blockCondition: null,
      failureType: "CONICA",
      notes: null,
    });
  });

  it("takes today from the Dominican clock, not the browser's", () => {
    // 02:30 UTC on the 9th is still the 8th in Santo Domingo (UTC−4).
    expect(todayIso(new Date("2026-10-09T02:30:00Z"))).toBe("2026-10-08");
    expect(todayIso(new Date("2026-10-09T04:00:00Z"))).toBe("2026-10-09");
  });

  it("shows a parameter without trailing zeros", () => {
    expect([
      parameterText({ kind: "NUMBER", number: "0.15000000", text: null }),
      parameterText({ kind: "NUMBER", number: "26.00000000", text: null }),
      parameterText({ kind: "NUMBER", number: "0.09806650", text: null }),
      parameterText({ kind: "TEXT", number: null, text: "TEST MARK" }),
    ]).toEqual(["0.15", "26", "0.0980665", "TEST MARK"]);
  });

  it("words the verdict with its basis and the alerts (LAB1-02)", () => {
    expect([verdictBadge("COMPLIES", "REAL").label, verdictBadge("COMPLIES", "ESTIMATED").label, verdictBadge("FAILS", "ESTIMATED").label, verdictBadge("NO_SPEC", "ESTIMATED").label]).toEqual([
      "Cumple",
      "Cumple (estimado)",
      "No cumple (estimado)",
      "Sin requisito",
    ]);
    expect([verdictBadge(null, null).label, verdictBadge("NO_DATA", null).label, verdictBadge("COMPLIES", "ESTIMATED").tone]).toEqual(["Sin ensayos", "Sin dato", "tone-attention"]);
    expect(alertsText(["FEW_SPECIMENS", "HIGH_CV"])).toBe("Pocas probetas · CV alto");
  });

  it("offers Calidad the final release only on a real CUMPLE, and block or unblock by status (LAB1-02)", () => {
    const calidad = (p: string) => ["fg_lot:final_release", "fg_lot:release", "lab_spec:manage"].includes(p);
    const lab = (p: string) => p === "lab_test:record";
    expect(qualityActions({ status: "RELEASED", readyForFinalRelease: true }, calidad)).toEqual(["finalRelease", "block", "reevaluate"]);
    expect(qualityActions({ status: "RELEASED", readyForFinalRelease: false }, calidad)).toEqual(["block", "reevaluate"]);
    expect(qualityActions({ status: "BLOCKED", readyForFinalRelease: false }, calidad)).toEqual(["unblock", "reevaluate"]);
    expect(qualityActions({ status: "FINAL_RELEASED", readyForFinalRelease: false }, calidad)).toEqual(["block", "reevaluate"]);
    expect(qualityActions({ status: "RELEASED", readyForFinalRelease: true }, lab)).toEqual([]);
  });
});

describe("LAB1-03 helpers", () => {
  it("reads a rack label's QR, with or without the host, and nothing else", () => {
    const id = "01a12659-d89a-7389-9aa3-c9dffc6f621d";
    expect(parseRackQr(`https://staging.industriasrochell.com.do/calidad/lotes/?lote=${id}&rack=2&codigo=8070325P1-T2`)).toEqual({ lotId: id, rackNo: 2, code: "8070325P1-T2" });
    expect(parseRackQr(`/calidad/lotes/?lote=${id.toUpperCase()}`)).toEqual({ lotId: id, rackNo: null, code: null });
    expect(parseRackQr("https://ecf.dgii.gov.do/ConsultaTimbre?RncEmisor=1")).toBeNull();
    expect(parseRackQr("/calidad/lotes/?lote=nope&rack=1")).toBeNull();
    expect(parseRackQr("")).toBeNull();
  });

  it("offers the break dates with a valid specimen, newest first", () => {
    expect(
      certifiableDates([
        { breakDate: "2026-10-01", status: "RECORDED" },
        { breakDate: "2026-10-28", status: "RECORDED" },
        { breakDate: "2026-10-01", status: "RECORDED" },
        { breakDate: "2026-10-05", status: "VOIDED" },
      ]),
    ).toEqual(["2026-10-28", "2026-10-01"]);
  });

  it("says why a certificate is void", () => {
    expect(certificateVoidText("SPECIMEN_VOIDED")).toBe("anulado porque se anuló una de sus probetas");
    expect(certificateVoidText("MANUAL")).toBe("anulado por Calidad");
    expect(certificateVoidText(null)).toBe("");
  });
});
