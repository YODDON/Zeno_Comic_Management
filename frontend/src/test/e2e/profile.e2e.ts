import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

test("profile display name is accessible, responsive, and keyboard editable", async ({ page }, testInfo) => {
  let profile = {
    id: "4d29db54-02f1-4e99-9ba3-ccbd6bf5c03f",
    displayName: "Tên ban đầu",
    email: "reader@example.com",
    avatarUrl: null,
    role: "User",
    status: "Active",
  };

  await page.route("**/api/v1/me", async (route) => {
    if (route.request().method() === "PATCH") {
      expect(route.request().headers()["x-csrf-token"]).toBe("profile-csrf-one");
      const request = route.request().postDataJSON() as { displayName: string };
      profile = { ...profile, displayName: request.displayName };
      await route.fulfill({
        json: profile,
        headers: { "X-CSRF-TOKEN": "profile-csrf-two" },
      });
      return;
    }

    await route.fulfill({
      json: profile,
      headers: { "X-CSRF-TOKEN": "profile-csrf-one" },
    });
  });

  await page.goto("/profile");
  await expect(page.getByRole("heading", { name: "Hồ sơ của bạn" })).toBeVisible();
  await expect(page.getByText("reader@example.com")).toBeVisible();
  await expect(page.getByRole("link", { name: /Xem ví/ })).toHaveAttribute("href", "/wallet");
  await expect(page.getByRole("link", { name: /Quyền lợi VIP/ })).toHaveAttribute("href", "/vip");

  await page.getByRole("button", { name: "Sáng", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.getByRole("button", { name: "Tối", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);

  const input = page.getByRole("textbox", { name: "Tên hiển thị" });
  await input.focus();
  await page.keyboard.press("ControlOrMeta+A");
  await page.keyboard.type("But danh ban phim");
  await page.keyboard.press("Tab");
  const saveButton = page.getByRole("button", { name: "Lưu tên hiển thị" });
  await expect(saveButton).toBeFocused();
  await page.keyboard.press("Enter");

  await expect(page.getByText("Tên hiển thị đã được cập nhật.")).toBeVisible();
  await expect(input).toHaveValue("But danh ban phim");

  if (testInfo.project.name === "mobile-chrome") {
    await page.goto("/");
    const menuButton = page.getByRole("button", { name: "Mở menu" });
    await menuButton.click();
    await expect(page.getByRole("link", { name: "But danh ban phim" })).toBeVisible();
    await page.getByRole("link", { name: "But danh ban phim" }).click();
    await expect(page.getByRole("button", { name: "Mở menu" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Hồ sơ của bạn" })).toBeVisible();
  } else {
    await expect(
      page.getByRole("navigation", { name: "Điều hướng chính" })
        .getByRole("link", { name: "But danh ban phim" }),
    ).toBeVisible();
  }

  await page.evaluate(() => window.scrollTo(0, 0));

  await page.screenshot({
    path: `artifacts/i03-profile-${testInfo.project.name}.png`,
    fullPage: true,
  });
});
