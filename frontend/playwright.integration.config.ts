import { defineConfig } from "@playwright/test";

const frontendPort = 4174;
const apiPort = 5180;
const apiUrl = `http://127.0.0.1:${apiPort}`;
const dotnetHost = process.env.DOTNET_HOST_PATH?.trim() || "dotnet";

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
    locale: "vi-VN",
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "integration-desktop-chrome",
      grepInvert: /@q03-reduced-motion/,
      use: { viewport: { width: 1_440, height: 900 } },
    },
    {
      name: "integration-mobile-chrome",
      grep: /@q03-responsive-security/,
      use: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true },
    },
    {
      name: "integration-tablet-chrome",
      grep: /@q03-responsive-security/,
      use: { viewport: { width: 720, height: 900 }, isMobile: true, hasTouch: true },
    },
    {
      name: "integration-mobile-reduced-motion-chrome",
      grep: /@q03-reduced-motion/,
      use: {
        viewport: { width: 390, height: 844 },
        isMobile: true,
        hasTouch: true,
        reducedMotion: "reduce",
      },
    },
  ],
  webServer: [
    {
      command: `"${dotnetHost}" run --project ../backend/tests/ComicManagement.R03.E2EHost/ComicManagement.R03.E2EHost.csproj -- --port ${apiPort}`,
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
