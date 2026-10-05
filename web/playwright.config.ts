import { defineConfig, devices } from "@playwright/test";

// E-PR18b-10: the journey runs against the real API host (tests/Rochell.DevStack: PostgreSQL in Docker, seeded company,
// simulated IdP) serving this export from the same origin. Build first: `dotnet build -c Release` and `npm run build`.
// E-UX1-01-11: the main journeys also run on a 390 × 844 phone ("mobile"), against a second dev stack of their own (the journeys
// expect the seeded data, e.g. the first payment is PAG-000001, so the two runs never share a database).
const port = Number(process.env.ROCHELL_E2E_PORT ?? 5190);
const mobilePort = Number(process.env.ROCHELL_E2E_MOBILE_PORT ?? port + 1);

const devStack = (listen: number) => ({
  command: `dotnet run --project ../tests/Rochell.DevStack -c Release --no-build -- --port ${listen} --web-root out`,
  url: `http://localhost:${listen}/`,
  timeout: 240_000,
  reuseExistingServer: !process.env.CI,
  stdout: "pipe" as const,
});

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
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    {
      name: "mobile",
      testMatch: /(sales|purchase|production|treasury|quote|configuration|home|proforma|mail|cash-sale|expense|price)-journey\.spec\.ts/,
      use: {
        ...devices["Desktop Chrome"],
        baseURL: `http://localhost:${mobilePort}`,
        viewport: { width: 390, height: 844 },
        deviceScaleFactor: 3,
        isMobile: true,
        hasTouch: true,
      },
    },
  ],
  webServer: [devStack(port), devStack(mobilePort)],
});
