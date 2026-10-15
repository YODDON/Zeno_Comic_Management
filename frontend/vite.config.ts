import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

const apiProxyTarget = process.env.R03_E2E_API_URL?.trim();

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "src"),
    },
  },
  test: {
    environment: "jsdom",
    exclude: ["**/*.e2e.ts", "**/node_modules/**", "**/dist/**"],
    globals: true,
    setupFiles: "./src/test/setup.ts",
    css: true,
  },
  server: apiProxyTarget ? {
    proxy: {
      "/api": {
        target: apiProxyTarget,
        changeOrigin: false,
      },
    },
  } : undefined,
});
