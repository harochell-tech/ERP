// E-UX1-01-10: the badge that tells a non-production screen apart. The API exposes no environment to the browser (the session
// description has none), so the host name decides: "staging." → STAGING, a local host → PRUEBA, anything else → no badge.

export type EnvironmentBadge = { label: string; tone: "staging" | "test" } | null;

export function environmentBadge(hostname: string): EnvironmentBadge {
  const host = hostname.toLowerCase();
  if (host.startsWith("staging.") || host.includes(".staging.")) {
    return { label: "STAGING", tone: "staging" };
  }
  if (host === "localhost" || host === "127.0.0.1" || host === "::1" || host === "[::1]" || host.endsWith(".localhost")) {
    return { label: "PRUEBA", tone: "test" };
  }
  return null;
}
