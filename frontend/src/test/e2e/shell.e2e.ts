import { expect, test } from "@playwright/test";

test("user shell supports navigation and a persisted theme", async ({ page }, testInfo) => {
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Đọc theo nhịp của bạn." })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  await page.screenshot({
    path: `artifacts/f05-home-${testInfo.project.name}.png`,
    fullPage: true,
  });

  await page.getByRole("button", { name: "Chuyển sang giao diện sáng" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");

  if (testInfo.project.name === "mobile-chrome") {
    await page.getByRole("button", { name: "Mở menu" }).click();
    await expect(page.getByRole("navigation", { name: "Điều hướng di động" })).toBeVisible();
  }

  await page.getByRole("link", { name: "Khám phá kho truyện" }).click();
  await expect(page).toHaveURL("/comics");
  await expect(page.getByRole("heading", { name: "Danh mục sẽ xuất hiện ở đây" })).toBeVisible();
});

test("admin shell remains light and shows no fabricated data", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Chuyển sang giao diện sáng" }).click();
  await page.getByRole("button", { name: "Chuyển sang giao diện tối" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  await page.goto("/admin");

  await expect(page.getByRole("heading", { name: "Tổng quan" })).toBeVisible();
  await expect(page.getByText("Chưa kết nối dữ liệu quản trị")).toBeVisible();
  await expect(page.locator(".admin-shell")).toHaveCSS("color-scheme", "light");
});
