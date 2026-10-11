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

  await page.screenshot({
    path: `artifacts/f05-home-${testInfo.project.name}.png`,
    fullPage: true,
  });

  const themeToggle = page.getByRole("button", { name: "Chuyển sang giao diện sáng" });
  await themeToggle.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");

  if (testInfo.project.name === "mobile-chrome") {
    const menuButton = page.getByRole("button", { name: "Mở menu" });
    await menuButton.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("navigation", { name: "Điều hướng di động" })).toBeVisible();
  }

  await expectNoAccessibilityViolations(page);

  await page.getByRole("link", { name: "Khám phá kho truyện" }).click();
  await expect(page).toHaveURL("/comics");
  await expect(page.getByRole("heading", { name: "Danh mục sẽ xuất hiện ở đây" })).toBeVisible();
});

test("admin shell remains light, accessible and keyboard navigable", async ({ page }, testInfo) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Chuyển sang giao diện sáng" }).click();
  await page.getByRole("button", { name: "Chuyển sang giao diện tối" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  await page.goto("/admin");

  await expect(page.getByRole("heading", { name: "Tổng quan" })).toBeVisible();
  await expect(page.getByText("Chưa kết nối dữ liệu quản trị")).toBeVisible();
  await expect(page.locator(".admin-shell")).toHaveCSS("color-scheme", "light");

  if (testInfo.project.name === "mobile-chrome") {
    const menuButton = page.getByRole("button", { name: "Mở menu quản trị" });
    await menuButton.focus();
    await page.keyboard.press("Enter");
    await expect(
      page.getByRole("navigation", { name: "Điều hướng quản trị di động" }),
    ).toBeVisible();
    await expectNoAccessibilityViolations(page);

    await page.keyboard.press("Escape");
    await expect(
      page.getByRole("navigation", { name: "Điều hướng quản trị di động" }),
    ).toBeHidden();
    await expect(menuButton).toBeFocused();
  } else {
    await expectNoAccessibilityViolations(page);
  }
});
