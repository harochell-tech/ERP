// Typed transport over the Rochell API (E-PR18-3, E-PR18b-3). Types come from schema.d.ts, generated from openapi.json.
import type { components, paths } from "./schema";

export type Schemas = components["schemas"];
export type CommandResponse = Schemas["CommandResponse"];

/** A problem+json answer (RFC 9457) with the domain code. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly correlationId?: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

const CSRF_HEADER = "X-Rochell-Csrf";

type CompanyPostPath = {
  [P in keyof paths]: P extends `/api/v1/companies/{companyId}/${string}` ? (paths[P] extends { post: object } ? P : never) : never;
}[keyof paths];

type GetPath = { [P in keyof paths]: paths[P] extends { get: object } ? P : never }[keyof paths];

/** Body of a command: the command without the fields the server fills (companyId, sessionId, idempotencyKey). */
export type CommandBody<P extends CompanyPostPath> = paths[P] extends {
  post: { requestBody?: { content: { "application/json": infer B } } };
}
  ? B
  : never;

export type QueryResult<P extends GetPath> = paths[P] extends {
  get: { responses: { 200: { content: { "application/json": infer R } } } };
}
  ? R
  : never;

export type QueryParams = {
  path?: Record<string, string>;
  query?: Record<string, string | number | null | undefined>;
};

export function buildUrl(template: string, params: QueryParams = {}): string {
  let url = template.replace(/\{(\w+)\}/g, (_, name: string) => {
    const value = params.path?.[name];
    if (value === undefined) {
      throw new Error(`Missing path parameter ${name} for ${template}`);
    }
    return encodeURIComponent(value);
  });
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params.query ?? {})) {
    if (value !== undefined && value !== null && value !== "") {
      search.set(key, String(value));
    }
  }
  const text = search.toString();
  if (text.length > 0) {
    url += `?${text}`;
  }
  return url;
}

async function problem(response: Response): Promise<ApiError> {
  let code = `HTTP_${response.status}`;
  let detail = response.statusText;
  let correlationId = response.headers.get("X-Correlation-Id") ?? undefined;
  if (response.headers.get("Content-Type")?.includes("json")) {
    const body = (await response.json()) as { code?: string; detail?: string; correlationId?: string };
    code = body.code ?? code;
    detail = body.detail ?? detail;
    correlationId = body.correlationId ?? correlationId;
  }
  return new ApiError(response.status, code, detail, correlationId);
}

/** GET on the read side (E-PR18-4). */
export async function query<P extends GetPath>(path: P, params: QueryParams = {}): Promise<QueryResult<P>> {
  const response = await fetch(buildUrl(path, params), { credentials: "same-origin", headers: { Accept: "application/json" } });
  if (!response.ok) {
    throw await problem(response);
  }
  return (await response.json()) as QueryResult<P>;
}

/** POST of a command with its idempotency key (created when the form opens, E-03) and the anti-CSRF header. */
export async function runCommand<P extends CompanyPostPath>(path: P, companyId: string, body: CommandBody<P>, idempotencyKey: string): Promise<CommandResponse> {
  const response = await fetch(buildUrl(path, { path: { companyId } }), {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", Accept: "application/json", [CSRF_HEADER]: "1", "Idempotency-Key": idempotencyKey },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw await problem(response);
  }
  return (await response.json()) as CommandResponse;
}

export async function logout(): Promise<void> {
  await fetch("/api/v1/auth/logout", { method: "POST", credentials: "same-origin", headers: { [CSRF_HEADER]: "1" } });
}

export function loginUrl(returnPath: string): string {
  return `/api/v1/auth/login?returnUrl=${encodeURIComponent(returnPath)}`;
}

export function stepUpUrl(returnPath: string): string {
  return `/api/v1/auth/step-up?returnUrl=${encodeURIComponent(returnPath)}`;
}

export type { CompanyPostPath, GetPath };
