import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

const userId = "a713606c-d008-4f76-a6e7-d83829303001";
const comicId = "4ee1fdae-79bb-4839-a271-68cac1630301";

test("@q03-responsive-security guest reading and authenticated security stay fail-closed", async ({ page }, testInfo) => {
  await page.setExtraHTTPHeaders({});
  const sessionRequests: string[] = [];
  page.on("request", (request) => {
    if (request.url().endsWith("/api/v1/reading/sessions")) sessionRequests.push(request.url());
  });

  await page.goto("/comics");
  const gridColumns = await page.locator(".comic-grid").evaluate((element) =>
    getComputedStyle(element).gridTemplateColumns.split(" ").filter(Boolean).length,
  );
  const viewportWidth = page.viewportSize()!.width;
  const expectedColumns = viewportWidth < 720 ? 2 : viewportWidth < 1_100 ? 4 : 5;
  expect(gridColumns, `${viewportWidth}px Catalog grid`).toBe(expectedColumns);
  await expect(page.getByRole("link", { name: /Mở Hành trình R03 thật/ })).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  await page.getByRole("link", { name: /Mở Hành trình R03 thật/ }).click();
  await expect(page.getByRole("heading", { name: "Hành trình R03 thật" })).toBeVisible();
  await expect(page.getByRole("link", { name: /Đăng nhập với Google/ }).first()).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  const contentResponsePromise = page.waitForResponse((response) =>
    response.url().includes("/pages/1/content") && response.request().method() === "GET",
  );
  await page.getByRole("link", { name: "Đọc từ đầu" }).click();
  const contentResponse = await contentResponsePromise;
  expect(contentResponse.status()).toBe(200);
  expect(contentResponse.headers()["cache-control"]).toContain("no-store");
  expect(contentResponse.headers()["x-content-type-options"]).toBe("nosniff");
  await expect(page.getByRole("img", { name: "Trang 1" })).toBeVisible();
  expect(sessionRequests).toHaveLength(0);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  if (testInfo.project.name === "integration-mobile-chrome") {
    const toolbar = page.locator(".reader-toolbar");
    const backControl = page.locator(".reader-back");
    await expect(toolbar).toHaveClass(/reader-toolbar-visible/);

    await page.evaluate(() => window.scrollTo({
      top: 450,
      behavior: "auto",
    }));
    await expect(toolbar).toHaveClass(/reader-toolbar-hidden/);
    await expect(toolbar).toHaveCSS("pointer-events", "none");
    await expect(page.getByRole("img", { name: "Trang 2" })).toBeVisible();

    await page.touchscreen.tap(195, 500);
    await expect(toolbar).toHaveClass(/reader-toolbar-visible/);
    await backControl.focus();
    await expect(backControl).toBeFocused();
    const toolbarBox = await toolbar.boundingBox();
    expect(toolbarBox).not.toBeNull();
    expect(toolbarBox!.y).toBeGreaterThanOrEqual(0);
    expect(toolbarBox!.y + toolbarBox!.height).toBeLessThanOrEqual(page.viewportSize()!.height);
  }

  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": userId });
  await page.goto("/admin");
  await expect(page.getByRole("alert")).toContainText("Không thể xác minh quyền quản trị");
  await expect(page.locator(".admin-shell")).toHaveCount(0);

  const forbidden = await page.evaluate(async () => {
    const response = await fetch("/api/v1/admin/dashboard");
    return response.status;
  });
  // The deterministic E2E authentication handler deliberately has no cookie
  // challenge body. Standardized production 403 payloads are covered by the
  // Q03 API profile; this browser assertion proves route-level role denial.
  expect(forbidden).toBe(403);

  const csrfRejected = await page.evaluate(async (id) => {
    const response = await fetch(`/api/v1/comics/${id}/follow`, {
      method: "PUT",
      credentials: "include",
    });
    return { status: response.status, body: await response.json() as { errorCode: string } };
  }, comicId);
  expect(csrfRejected).toEqual({
    status: 400,
    body: expect.objectContaining({ errorCode: "VALIDATION_FAILED" }),
  });
});

test("@q03-reduced-motion mobile Reader honors the reduced-motion preference", async ({ page }) => {
  await page.setExtraHTTPHeaders({});
  await page.goto("/comics");
  await page.locator(".comic-card a").first().click();
  await page.getByRole("link", { name: "Đọc từ đầu" }).click();
  await expect(page.getByRole("img", { name: "Trang 1" })).toBeVisible();

  expect(await page.evaluate(() => matchMedia("(prefers-reduced-motion: reduce)").matches)).toBe(true);
  const motion = await page.locator(".reader-toolbar").evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      animationDuration: style.animationDuration,
      transitionDuration: style.transitionDuration,
    };
  });
  const isReducedDuration = (duration: string) =>
    duration.split(", ").every((value) => Number.parseFloat(value) <= 0.000_02);
  expect(isReducedDuration(motion.animationDuration)).toBe(true);
  expect(isReducedDuration(motion.transitionDuration)).toBe(true);

  await page.evaluate(() => window.scrollTo({
    top: 450,
    behavior: "auto",
  }));
  await expect(page.locator(".reader-toolbar")).toHaveClass(/reader-toolbar-hidden/);
  await page.touchscreen.tap(195, 500);
  await expect(page.locator(".reader-toolbar")).toHaveClass(/reader-toolbar-visible/);
});
