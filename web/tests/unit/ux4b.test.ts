import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/api/client";
import { screenTitle } from "@/components/Shell";
import {
  authorizationAlertDays,
  codeAndName,
  countDraftsToApprove,
  countExpiringAuthorizations,
  countFiscalRulesToActivate,
  countPolicyDraftsToApprove,
  ecfTypeLabel,
  foldText,
  groupParametersByTheme,
  groupPoliciesByTheme,
  matchesSearch,
  previewQuery,
} from "@/lib/ux4b";

// UX4-03: the shared helpers of part 2 of UX wave 4 (search, Inicio's configuration tasks, policy themes, POST previews).

const mine = (actor: string | null | undefined) => actor === "Ana Pérez";

describe("search (C-32, V-40, A-19)", () => {
  it("ignores case and accents and needs every word in some field", () => {
    expect(foldText("Adoquín HOLANDÉS")).toBe("adoquin holandes");
    expect(matchesSearch("adoquin", "ADOQUIN-H", "Adoquín holandés")).toBe(true);
    expect(matchesSearch("holandes adoq", "ADOQUIN-H", "Adoquín holandés")).toBe(true);
    expect(matchesSearch("bloque", "ADOQUIN-H", "Adoquín holandés")).toBe(false);
    expect(matchesSearch("  ", "x")).toBe(true);
    expect(matchesSearch("ana", null, undefined, "ana@rochell.do")).toBe(true);
  });

  it("names a code once when the description repeats it", () => {
    expect(codeAndName("ADITIVO-P", "ADITIVO-P")).toBe("ADITIVO-P");
    expect(codeAndName("ADITIVO-P", "aditivo-p ")).toBe("ADITIVO-P");
    expect(codeAndName("ADOQUIN-H", "Adoquín holandés")).toBe("ADOQUIN-H — Adoquín holandés");
    expect(codeAndName("X", null)).toBe("X");
  });
});

describe("Inicio configuration tasks (G-15)", () => {
  it("counts the drafts someone else prepared", () => {
    const items = [
      { status: "DRAFT", preparedBy: "Ana Pérez" },
      { status: "DRAFT", preparedBy: "Luis Gómez" },
      { status: "DRAFT", preparedBy: null },
      { status: "ACTIVE", preparedBy: "Luis Gómez" },
    ];
    expect(countDraftsToApprove(items, mine)).toBe(2);
    expect(countPolicyDraftsToApprove([{ versions: items }, { versions: [{ status: "DRAFT", preparedBy: "Luis Gómez" }] }], mine)).toBe(3);
  });

  it("counts the READY fiscal rule versions not configured by the reader", () => {
    const rules = [
      { versions: [{ status: "READY", configuredBy: "Luis Gómez" }, { status: "ACTIVE", configuredBy: "Luis Gómez" }] },
      { versions: [{ status: "READY", configuredBy: "Ana Pérez" }, { status: "BLOCKED_PENDING_SOURCE", configuredBy: null }] },
    ];
    expect(countFiscalRulesToActivate(rules, mine)).toBe(1);
  });

  it("reads the alert window from the REVENUE_ACCOUNTING version in force (end date exclusive)", () => {
    const policies = [
      { policyCode: "CREDIT", versions: [] },
      {
        policyCode: "REVENUE_ACCOUNTING",
        versions: [
          { status: "ACTIVE", effectiveFrom: "2026-01-01", effectiveTo: "2026-09-30", parameters: { authorization_expiry_alert_days: "15" } },
          { status: "ACTIVE", effectiveFrom: "2026-09-30", effectiveTo: null, parameters: { authorization_expiry_alert_days: "30" } },
          { status: "DRAFT", effectiveFrom: "2026-10-01", effectiveTo: null, parameters: { authorization_expiry_alert_days: "60" } },
        ],
      },
    ];
    expect(authorizationAlertDays(policies, "2026-09-29")).toBe(15);
    expect(authorizationAlertDays(policies, "2026-09-30")).toBe(30);
    expect(authorizationAlertDays(policies, "2025-12-31")).toBeNull();
    expect(authorizationAlertDays([], "2026-09-30")).toBeNull();
  });

  it("counts ACTIVE authorizations expiring within the window, none without a window", () => {
    const items = [
      { status: "ACTIVE", daysToExpiry: 0 },
      { status: "ACTIVE", daysToExpiry: 30 },
      { status: "ACTIVE", daysToExpiry: 31 },
      { status: "ACTIVE", daysToExpiry: -2 },
      { status: "ACTIVE", daysToExpiry: null },
      { status: "SUSPENDED", daysToExpiry: 3 },
    ];
    expect(countExpiringAuthorizations(items, 30)).toBe(3);
    expect(countExpiringAuthorizations(items, null)).toBe(0);
  });
});

describe("policy themes (G-22)", () => {
  it("groups the policies by theme in a fixed order, unknown ones last", () => {
    const groups = groupPoliciesByTheme([{ policyCode: "POSTING" }, { policyCode: "CREDIT" }, { policyCode: "PURCHASING" }, { policyCode: "NEW_ONE" }]);
    expect(groups.map((g) => [g.theme, g.policies.map((p) => p.policyCode)])).toEqual([
      ["Compras e inventario", ["PURCHASING"]],
      ["Ventas y crédito", ["CREDIT"]],
      ["Contabilización y tesorería", ["POSTING"]],
      ["Otras", ["NEW_ONE"]],
    ]);
  });

  it("puts the authorization expiry alert under Fiscal and keeps a small policy in one group", () => {
    const revenue = groupParametersByTheme([
      { paramCode: "unbilled_delivery_presentation" },
      { paramCode: "authorization_expiry_alert_days" },
      { paramCode: "delivery_open_alert_hours" },
      { paramCode: "unbilled_aging_alert_days" },
    ]);
    expect(revenue.map((g) => [g.theme, g.definitions.map((d) => d.paramCode)])).toEqual([
      ["Presentación contable", ["unbilled_delivery_presentation"]],
      ["Fiscal", ["authorization_expiry_alert_days"]],
      ["Alertas de despacho y facturación", ["delivery_open_alert_hours", "unbilled_aging_alert_days"]],
    ]);
    expect(groupParametersByTheme([{ paramCode: "overdue_days_block" }])).toEqual([{ theme: null, definitions: [{ paramCode: "overdue_days_block" }] }]);
  });
});

describe("e-CF types (G-25)", () => {
  it("reads the type with its name, unknown codes as they come", () => {
    expect(ecfTypeLabel("31")).toBe("31 — Crédito fiscal");
    expect(ecfTypeLabel("44")).toBe("44 — Regímenes especiales");
    expect(ecfTypeLabel("99")).toBe("99");
    expect(ecfTypeLabel(null)).toBe("—");
  });
});

describe("menu wording (G-13, V-17, A-09)", () => {
  it("names the screens as the menu does", () => {
    expect(screenTitle("/auditoria/verificar/")).toBe("Verificar integridad");
    expect(screenTitle("/auditoria/digests/")).toBe("Respaldos diarios inalterables");
    expect(screenTitle("/contabilidad/mapas/")).toBe("Cuentas por rol");
    expect(screenTitle("/contabilidad/reglas/")).toBe("Reglas de contabilización");
    expect(screenTitle("/ventas/antiguedad/")).toBe("Cuentas por cobrar por antigüedad");
  });
});

describe("previewQuery (E-UX4-3)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("posts the body with the anti-CSRF header and no idempotency key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ total: "118.00" }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    const result = await previewQuery("/api/v1/companies/{companyId}/sales/orders/preview", "c-1", { plantId: "p-1", lines: [] } as never);
    expect(result).toEqual({ total: "118.00" });
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/companies/c-1/sales/orders/preview");
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["X-Rochell-Csrf"]).toBe("1");
    expect(headers["Idempotency-Key"]).toBeUndefined();
    expect(JSON.parse(init.body as string)).toEqual({ plantId: "p-1", lines: [] });
  });

  it("turns a problem answer into an ApiError with its code", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: "INVALID_REQUEST", detail: "bad" }), { status: 400, headers: { "Content-Type": "application/problem+json" } })),
    );
    await expect(previewQuery("/api/v1/companies/{companyId}/sales/quotes/preview", "c-1", { plantId: "p-1", lines: [] } as never)).rejects.toMatchObject({
      code: "INVALID_REQUEST",
      status: 400,
    });
    expect(ApiError).toBeDefined();
  });
});
