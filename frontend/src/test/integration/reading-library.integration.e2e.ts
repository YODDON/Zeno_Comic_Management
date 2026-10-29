import { expect, test } from "@playwright/test";

const comicTitle = "Hành trình R03 thật";
const chapterId = "2744b2b6-f009-4122-b574-1dad21740301";
const userId = "a713606c-d008-4f76-a6e7-d83829303001";

test("persists follow and reading progress through the real API and PostgreSQL", async ({ page }) => {
  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": userId });
  await page.goto("/comics");
  await page.getByRole("link", { name: `Mở ${comicTitle}` }).click();

  const followResponse = page.waitForResponse((response) =>
    response.request().method() === "PUT" && response.url().endsWith("/follow"));
  await page.getByRole("button", { name: "Theo dõi truyện" }).click();
  expect((await followResponse).status()).toBe(200);
  await expect(page.getByRole("button", { name: "Bỏ theo dõi" })).toBeVisible();

  const sessionResponse = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/api/v1/reading/sessions"));
  await page.getByRole("link", { name: "Đọc từ đầu" }).click();
  expect((await sessionResponse).status()).toBe(201);
  await expect(page.getByRole("img", { name: "Trang 2" })).toBeVisible();

  await page.locator('[data-page-order="2"]').evaluate((element) => {
    const pageElement = element as HTMLElement;
    window.scrollTo({
      top: pageElement.offsetTop + pageElement.offsetHeight * 0.4 - window.innerHeight * 0.45,
      behavior: "instant",
    });
  });
  await expect(page.getByText(/Trang 2 ·/)).toBeVisible();

  const heartbeatResponse = await page.waitForResponse(
    (response) => response.request().method() === "PATCH" &&
      response.url().includes("/api/v1/reading/sessions/"),
    { timeout: 15_000 },
  );
  expect(heartbeatResponse.status()).toBe(200);
  const heartbeat = await heartbeatResponse.json() as {
    acceptedPosition: { pageOrder: number; pageOffsetRatio: number };
    acceptedMaxProgressBasisPoints: number;
  };
  expect(heartbeat.acceptedPosition.pageOrder).toBe(2);
  expect(heartbeat.acceptedPosition.pageOffsetRatio).toBeGreaterThan(0.3);
  expect(heartbeat.acceptedMaxProgressBasisPoints).toBeGreaterThan(6_500);

  const continueResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "GET" && response.url().includes("/api/v1/library/continue"));
  await page.goto("/library");
  const continueResponse = await continueResponsePromise;
  expect(continueResponse.status()).toBe(200);
  const continueBody = await continueResponse.json() as {
    items: Array<{
      chapterId: string;
      pageOrder: number;
      pageOffsetRatio: number;
      acceptedMaxProgressBasisPoints: number;
    }>;
  };
  expect(continueBody.items).toHaveLength(1);
  expect(continueBody.items[0]).toMatchObject({
    chapterId,
    pageOrder: 2,
    acceptedMaxProgressBasisPoints: heartbeat.acceptedMaxProgressBasisPoints,
  });
  expect(continueBody.items[0]?.pageOffsetRatio).toBeCloseTo(
    heartbeat.acceptedPosition.pageOffsetRatio,
    3,
  );

  await expect(page.getByRole("heading", { name: comicTitle })).toBeVisible();
  await page.getByRole("tab", { name: "Đang theo dõi" }).click();
  await expect(page.getByRole("heading", { name: comicTitle })).toBeVisible();
  await page.getByRole("tab", { name: "Đọc tiếp" }).click();

  await page.getByRole("link", { name: "Đọc tiếp" }).click();
  await expect(page).toHaveURL(new RegExp(
    `/chapters/${chapterId}/read\\?page=2&offset=${heartbeat.acceptedPosition.pageOffsetRatio}`,
  ));
  await expect(page.getByText(/Trang 2 ·/)).toBeVisible();

  const restoredOffset = await page.locator('[data-page-order="2"]').evaluate((element) => {
    const rect = element.getBoundingClientRect();
    return (window.innerHeight * 0.45 - rect.top) / rect.height;
  });
  expect(restoredOffset).toBeCloseTo(heartbeat.acceptedPosition.pageOffsetRatio, 1);
});
