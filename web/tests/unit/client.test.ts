import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, buildUrl, runCommand, stepUpUrl } from "@/api/client";

afterEach(() => vi.unstubAllGlobals());

describe("API client (E-PR18-3)", () => {
  it("fills path parameters and drops empty query values", () => {
    expect(buildUrl("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId: "c 1" }, query: { status: "", plantId: undefined, limit: 50 } })).toBe(
      "/api/v1/companies/c%201/procurement/purchase-orders?limit=50",
    );
    expect(() => buildUrl("/x/{id}")).toThrow("Missing path parameter id");
  });

  it("sends the idempotency key and the anti-CSRF header, and turns problems into ApiError", async () => {
    const fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ code: "STEP_UP_REQUIRED", detail: "Re-authenticate.", correlationId: "corr-1" }), {
        status: 403,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    vi.stubGlobal("fetch", fetch);

    const error = await runCommand("/api/v1/companies/{companyId}/procurement/approve-purchase-order", "c1", { plantId: "p", purchaseOrderId: "o", expectedVersion: 2 }, "key-1").catch(
      (e: unknown) => e,
    );

    const [url, init] = fetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/companies/c1/procurement/approve-purchase-order");
    expect(init.headers).toMatchObject({ "Idempotency-Key": "key-1", "X-Rochell-Csrf": "1" });
    expect(JSON.parse(init.body as string)).toEqual({ plantId: "p", purchaseOrderId: "o", expectedVersion: 2 });
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 403, code: "STEP_UP_REQUIRED", correlationId: "corr-1" });
  });

  it("returns to the same page after a step-up", () => {
    expect(stepUpUrl("/compras/orden/?id=1")).toBe("/api/v1/auth/step-up?returnUrl=%2Fcompras%2Forden%2F%3Fid%3D1");
  });
});
