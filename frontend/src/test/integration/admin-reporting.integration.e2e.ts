import { expect, test } from "@playwright/test";

const adminId = "a713606c-d008-4f76-a6e7-d8382930a001";

test("reads authoritative dashboard and redacted audit through ASP.NET Core and PostgreSQL", async ({ page }) => {
  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": adminId });

  await page.goto("/admin");
  await expect(page.getByRole("heading", { name: "Tổng quan" })).toBeVisible();
  await page.getByLabel("Từ ngày").fill("2026-10-15");
  await page.getByLabel("Đến ngày").fill("2026-10-15");
  const dashboardResponse = page.waitForResponse((response) =>
    new URL(response.url()).pathname === "/api/v1/admin/dashboard" &&
    new URL(response.url()).searchParams.get("from") === "2026-10-15");
  await page.getByRole("button", { name: "Áp dụng" }).click();
  expect((await dashboardResponse).status()).toBe(200);
  await expect(page.getByText(/100\.000/).first()).toBeVisible();
  await expect(page.getByText("10.000", { exact: true })).toBeVisible();

  await page.goto("/admin/audit");
  await page.getByLabel("Từ ngày").fill("2026-10-15");
  await page.getByLabel("Đến ngày").fill("2026-10-15");
  await page.getByLabel("Action").fill("Dashboard.SeedVerified");
  const auditResponse = page.waitForResponse((response) =>
    new URL(response.url()).pathname === "/api/v1/admin/audit" &&
    new URL(response.url()).searchParams.get("action") === "Dashboard.SeedVerified");
  await page.getByRole("button", { name: "Lọc", exact: true }).click();
  expect((await auditResponse).status()).toBe(200);
  await expect(page.getByText("Dashboard.SeedVerified")).toBeVisible();
  await expect(page.getByText(/"status": "Succeeded"/)).toBeVisible();
  await expect(
    page.getByRole("table").getByText(/accountNumber|ciphertext|secret/i),
  ).toHaveCount(0);
});
