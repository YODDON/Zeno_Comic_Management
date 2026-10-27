import { expect, test } from "@playwright/test";

const adminId = "a713606c-d008-4f76-a6e7-d8382930a001";

test("configures Mission and VIP and version-controls a User through real ASP.NET Core and PostgreSQL", async ({ page }) => {
  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": adminId });

  await page.goto("/admin/missions");
  await page.getByText("Tạo MissionDefinition").click();
  await page.getByLabel("Mã").fill("A03_E2E_LOGIN");
  await page.getByLabel("Tên").fill("A03 đăng nhập");
  const createResponse = page.waitForResponse((response) => response.request().method() === "POST" && new URL(response.url()).pathname === "/api/v1/admin/missions");
  await page.getByRole("button", { name: "Tạo Mission" }).click();
  expect((await createResponse).status()).toBe(201);
  await page.getByRole("button", { name: "A03_E2E_LOGIN" }).click();
  await page.getByLabel("Hiệu lực từ").fill("2026-10-27T00:00");
  const revisionResponse = page.waitForResponse((response) => response.url().includes("/revisions") && response.request().method() === "POST");
  await page.getByRole("button", { name: "Thêm revision" }).click();
  expect((await revisionResponse).status()).toBe(201);
  const activateResponse = page.waitForResponse((response) => response.url().endsWith("/activate") && response.request().method() === "POST");
  await page.getByRole("button", { name: "Kích hoạt" }).click();
  expect((await activateResponse).status()).toBe(200);
  await expect(page.getByRole("button", { name: "Đang active" })).toBeVisible();

  await page.goto("/admin/vip");
  await expect(page.getByText("720 giờ")).toBeVisible();
  await page.getByLabel("Giá cho đơn VIP tương lai (VND)").fill("30000");
  const vipResponse = page.waitForResponse((response) => response.url().endsWith("/api/v1/admin/vip-plan") && response.request().method() === "PATCH");
  await page.getByRole("button", { name: "Lưu giá tương lai" }).click();
  expect((await vipResponse).status()).toBe(200);
  await page.reload();
  await expect(page.getByLabel("Giá cho đơn VIP tương lai (VND)")).toHaveValue("30000");

  await page.goto("/admin/users");
  await page.getByRole("button", { name: "R03 E2E Reader" }).click();
  const disableResponse = page.waitForResponse((response) => response.url().endsWith("/disable") && response.request().method() === "POST");
  page.once("dialog", (dialog) => void dialog.accept());
  await page.getByRole("button", { name: "Tắt tài khoản" }).click();
  expect((await disableResponse).status()).toBe(200);
  await expect(page.getByRole("button", { name: "Bật tài khoản" })).toBeVisible();
  const enableResponse = page.waitForResponse((response) => response.url().endsWith("/enable") && response.request().method() === "POST");
  page.once("dialog", (dialog) => void dialog.accept());
  await page.getByRole("button", { name: "Bật tài khoản" }).click();
  expect((await enableResponse).status()).toBe(200);
  await page.reload();
  await page.getByRole("button", { name: "R03 E2E Reader" }).click();
  await expect(page.getByRole("button", { name: "Tắt tài khoản" })).toBeVisible();
});
