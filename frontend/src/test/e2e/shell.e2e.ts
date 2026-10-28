import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

async function expectNoAccessibilityViolations(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations).toEqual([]);
}

test("user shell supports navigation and a persisted theme", async ({ page }, testInfo) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Đọc theo nhịp của bạn." })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expectNoAccessibilityViolations(page);
  await page.screenshot({ path: `artifacts/f05-home-${testInfo.project.name}.png`, fullPage: true });

  const themeToggle = page.getByRole("button", { name: "Chuyển sang giao diện sáng" });
  await themeToggle.focus(); await page.keyboard.press("Enter");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  if (testInfo.project.name === "mobile-chrome") {
    const menuButton = page.getByRole("button", { name: "Mở menu" });
    await menuButton.focus(); await page.keyboard.press("Enter");
    await expect(page.getByRole("navigation", { name: "Điều hướng di động" })).toBeVisible();
  }
  await expectNoAccessibilityViolations(page);
  await page.getByRole("link", { name: "Khám phá kho truyện" }).click();
  await expect(page).toHaveURL("/comics");
  await expect(page.getByRole("heading", { name: "Tìm nhịp đọc tiếp theo." })).toBeVisible();
});

test("admin shell remains light, accessible and keyboard navigable", async ({ page }, testInfo) => {
  await page.route("**/api/v1/me", (route) => route.fulfill({
    headers: { "X-CSRF-TOKEN": "shell-csrf" },
    json: { id: "11111111-1111-4111-8111-111111111111", displayName: "Admin", email: "admin@example.test", avatarUrl: null, role: "Admin", status: "Active" },
  }));
  await page.route("**/api/v1/admin/dashboard**", (route) => route.fulfill({ json: {
    range: { from: "2026-10-01", to: "2026-10-30", startsAtUtc: "2026-09-30T17:00:00Z", endsAtUtc: "2026-10-30T17:00:00Z" },
    revenue: { topUpGrossVnd: 0, vipGrossVnd: 0 }, refunds: { completedCount: 0, originalAmountVnd: 0, feeVnd: 0, netRefundVnd: 0 },
    coinsIssued: { paidCoins: 0, bonusCoins: 0 }, reading: { qualifiedSessionCount: 0, distinctReaderCount: 0 },
    missions: { actionReceiptCount: 0, grantedRewardCount: 0, deniedDailyCapCount: 0, grantedBonusCoins: 0 }, dailySeries: [],
  } }));
  await page.goto("/");
  await page.getByRole("button", { name: "Chuyển sang giao diện sáng" }).click();
  await page.getByRole("button", { name: "Chuyển sang giao diện tối" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.goto("/admin");
  await expect(page.getByRole("heading", { name: "Tổng quan" })).toBeVisible();
  await expect(page.getByText("Báo cáo quản trị")).toBeVisible();
  await expect(page.locator(".admin-shell")).toHaveCSS("color-scheme", "light");
  if (testInfo.project.name === "mobile-chrome") {
    const menuButton = page.getByRole("button", { name: "Mở menu quản trị" });
    await menuButton.focus(); await page.keyboard.press("Enter");
    await expect(page.getByRole("navigation", { name: "Điều hướng quản trị di động" })).toBeVisible();
    await expectNoAccessibilityViolations(page);
    await page.keyboard.press("Escape");
    await expect(page.getByRole("navigation", { name: "Điều hướng quản trị di động" })).toBeHidden();
    await expect(menuButton).toBeFocused();
  } else await expectNoAccessibilityViolations(page);
});
