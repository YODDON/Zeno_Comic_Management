import { defineConfig } from "@playwright/test";

const frontendPort = 4174;
const apiPort = 5180;
const apiUrl = `http://127.0.0.1:${apiPort}`;

export default defineConfig({
  testDir: "./src/test/integration",
  testMatch: "**/*.integration.e2e.ts",
  outputDir: "./test-results-integration",
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  reporter: "list",
  workers: 1,
  use: {
    baseURL: `http://127.0.0.1:${frontendPort}`,
    channel: "chrome",
    extraHTTPHeaders: {
      "X-R03-E2E-User": "a713606c-d008-4f76-a6e7-d83829303001",
    },
    locale: "vi-VN",
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    viewport: { width: 1_440, height: 900 },
  },
  webServer: [
    {
      command: `dotnet run --project ../backend/tests/ComicManagement.R03.E2EHost/ComicManagement.R03.E2EHost.csproj -- --port ${apiPort}`,
      url: `${apiUrl}/__r03-e2e/ready`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: `npm run dev -- --host 127.0.0.1 --port ${frontendPort}`,
      env: {
        ...process.env,
        R03_E2E_API_URL: apiUrl,
      },
      url: `http://127.0.0.1:${frontendPort}`,
      reuseExistingServer: false,
      timeout: 30_000,
    },
  ],
});
