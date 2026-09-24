import type { NextConfig } from "next";
import { PHASE_DEVELOPMENT_SERVER } from "next/constants";

// E-PR18b-2: a static export served by Rochell.Api from its own origin (no Node server in production, no CORS).
// Under `next dev` the API and the dev IdP pages are forwarded to the dev stack (tests/Rochell.DevStack).
const apiOrigin = process.env.ROCHELL_API_ORIGIN ?? "http://localhost:5080";

export default function config(phase: string): NextConfig {
  const base: NextConfig = {
    output: "export",
    trailingSlash: true,
    reactStrictMode: true,
    poweredByHeader: false,
    images: { unoptimized: true },
  };
  if (phase !== PHASE_DEVELOPMENT_SERVER) {
    return base;
  }

  return {
    ...base,
    output: undefined,
    // trailingSlash would redirect /api/v1/... to /api/v1/.../ before forwarding it to the API.
    skipTrailingSlashRedirect: true,
    async rewrites() {
      return [
        { source: "/api/:path*", destination: `${apiOrigin}/api/:path*` },
        { source: "/dev-idp/:path*", destination: `${apiOrigin}/dev-idp/:path*` },
      ];
    },
  };
}
