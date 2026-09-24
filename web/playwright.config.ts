import { defineConfig, devices } from "@playwright/test";

// E-PR18b-10: the journey runs against the real API host (tests/Rochell.DevStack: PostgreSQL in Docker, seeded company,
// simulated IdP) serving this export from the same origin. Build first: `dotnet build -c Release` and `npm run build`.
const port = Number(process.env.ROCHELL_E2E_PORT ?? 5190);

export default defineConfig({
  testDir: "e2e",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: `http://localhost:${port}`,
    locale: "es-DO",
    timezoneId: "America/Santo_Domingo",
    trace: "retain-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `dotnet run --project ../tests/Rochell.DevStack -c Release --no-build -- --port ${port} --web-root out`,
    url: `http://localhost:${port}/`,
    timeout: 240_000,
    reuseExistingServer: !process.env.CI,
    stdout: "pipe",
  },
});
