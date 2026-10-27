import { expect, test } from "@playwright/test";

const adminId = "a713606c-d008-4f76-a6e7-d8382930a001";

test("reads real finance projections and persists an RF02 review and reject workflow", async ({ page }) => {
  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": adminId });

  const paymentsResponse = page.waitForResponse((response) =>
    response.request().method() === "GET" && response.url().includes("/api/v1/admin/payments?"));
  await page.goto("/admin/payments");
  expect((await paymentsResponse).status()).toBe(200);
  await page.getByRole("button", { name: "A02-E2E-TOPUP" }).click();
  await expect(page.getByRole("heading", { name: "Chi tiết thanh toán" })).toBeVisible();

  const refundsResponse = page.waitForResponse((response) =>
    response.request().method() === "GET" && response.url().includes("/api/v1/admin/refunds?"));
  await page.goto("/admin/refunds");
  expect((await refundsResponse).status()).toBe(200);
  await page.getByRole("button", { name: "A02-E2E-TOPUP" }).click();
  await expect(page.getByText("******6789", { exact: false })).toBeVisible();

  const reviewResponse = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/review"));
  await page.getByRole("button", { name: "Bắt đầu xem xét" }).click();
  expect((await reviewResponse).status()).toBe(200);

  await page.getByLabel("Ghi chú quyết định").fill("Không đủ điều kiện sau đối chiếu");
  page.once("dialog", (dialog) => void dialog.accept());
  const rejectResponse = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/reject"));
  await page.getByRole("button", { name: "Từ chối" }).click();
  expect((await rejectResponse).status()).toBe(200);
  await expect(page.getByText("Rejected · v3")).toBeVisible();

  await page.reload();
  await page.getByRole("button", { name: "A02-E2E-TOPUP" }).click();
  await expect(page.getByText("Rejected · v3")).toBeVisible();
});
