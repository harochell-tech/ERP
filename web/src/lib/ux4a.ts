// UX4-02 (E-UX4-1…17): helpers shared by the screens of Compras, Almacén, CxP, Tesorería, Contabilidad, Cierre, Auditoría and
// Fiscal. Pure (except `previewQuery`, the transport of the POST previews), unit-tested in tests/unit/ux4a.test.ts.
import { ApiError, buildUrl, type CompanyPostPath } from "@/api/client";
import type { paths } from "@/api/schema";

type PostBody<P extends CompanyPostPath> = paths[P] extends { post: { requestBody?: { content: { "application/json": infer B } } } } ? B : never;
type PostResult<P extends CompanyPostPath> = paths[P] extends { post: { responses: { 200: { content: { "application/json": infer R } } } } } ? R : never;

/**
 * E-UX4-3: a preview is a query whose lines travel in a JSON body, so it is a POST run READ ONLY by the query pipeline: the
 * anti-CSRF header as every POST, but no Idempotency-Key (nothing is written).
 */
export async function previewQuery<P extends CompanyPostPath>(path: P, companyId: string, body: PostBody<P>, signal?: AbortSignal): Promise<PostResult<P>> {
  const response = await fetch(buildUrl(path, { path: { companyId } }), {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-Rochell-Csrf": "1" },
    body: JSON.stringify(body),
    signal,
  });
  if (!response.ok) {
    let code = `HTTP_${response.status}`;
    let detail = response.statusText;
    let correlationId = response.headers.get("X-Correlation-Id") ?? undefined;
    if (response.headers.get("Content-Type")?.includes("json")) {
      const problem = (await response.json()) as { code?: string; detail?: string; correlationId?: string };
      code = problem.code ?? code;
      detail = problem.detail ?? detail;
      correlationId = problem.correlationId ?? correlationId;
    }
    throw new ApiError(response.status, code, detail, correlationId);
  }
  return (await response.json()) as PostResult<P>;
}

/** The last four digits of an account number, full ("0123456789") or already masked by the server ("••••6789"). */
export function lastFour(accountNumber: string | null | undefined): string {
  const digits = (accountNumber ?? "").replace(/[^0-9A-Za-z]/g, "");
  return digits.slice(-4);
}

/**
 * E-UX4-6 (C-27): a company bank account as "alias · BANCO ••••6789"; without an alias "BANCO ••••6789". Everywhere a screen of
 * UX4-02 names a company account.
 */
export function bankAccountLabel(account: { alias?: string | null; bankCode?: string | null; accountNumber?: string | null }): string {
  const four = lastFour(account.accountNumber);
  const bank = [account.bankCode?.trim() ?? "", four ? `••••${four}` : ""].filter((p) => p !== "").join(" ");
  const alias = account.alias?.trim() ?? "";
  if (alias === "") {
    return bank === "" ? "—" : bank;
  }
  return bank === "" ? alias : `${alias} · ${bank}`;
}
