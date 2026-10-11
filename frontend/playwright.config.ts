import { defineConfig } from "@playwright/test";

const isCi = Boolean(process.env.CI);

export default defineConfig({
  testDir: "./src/test/e2e",
  testMatch: "**/*.e2e.ts",
  outputDir: "./test-results",
  fullyParallel: false,
  forbidOnly: true,
  retries: isCi ? 1 : 0,
  reporter: isCi ? [["line"], ["html", { open: "never" }]] : "list",
  workers: isCi ? 1 : undefined,
  use: {
    baseURL: "http://127.0.0.1:4173",
    channel: "chrome",
    locale: "vi-VN",
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "desktop-chrome",
      use: { viewport: { width: 1440, height: 900 } },
    },
    {
      name: "mobile-chrome",
      use: { viewport: { width: 390, height: 844 }, isMobile: true },
    },
  ],
  webServer: {
    command: "npm run dev -- --host 127.0.0.1 --port 4173",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: !isCi,
    timeout: 30_000,
  },
});
