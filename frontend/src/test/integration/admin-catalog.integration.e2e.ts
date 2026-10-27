import { expect, test } from "@playwright/test";

const adminId = "a713606c-d008-4f76-a6e7-d8382930a001";

test("admin editor persists comic taxonomy chapter and page through real API and PostgreSQL", async ({ page }) => {
  await page.setExtraHTTPHeaders({ "X-R03-E2E-User": adminId });

  await page.goto("/admin/genres");
  await expect(page.locator(".admin-shell")).toBeVisible();
  const taxonomyForm = page.locator("form");
  await taxonomyForm.locator("input").nth(0).fill("A01 E2E Genre");
  await taxonomyForm.locator("input").nth(1).fill("a01-e2e-genre");
  const taxonomyCreated = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/api/v1/admin/taxonomy/genres"));
  await taxonomyForm.locator('button[type="submit"]').click();
  expect((await taxonomyCreated).status()).toBe(201);
  await expect(page.getByRole("button", { name: /A01 E2E Genre/ })).toBeVisible();

  await page.goto("/admin/comics");
  const comicForm = page.locator("form");
  await comicForm.locator("input").nth(0).fill("A01 E2E Comic");
  await comicForm.locator("input").nth(1).fill("a01-e2e-comic");
  await comicForm.locator("textarea").fill("Persisted by the real A01 browser flow.");
  await page.getByLabel("A01 E2E Genre").check();
  const comicCreated = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/api/v1/admin/comics"));
  await comicForm.locator('button[type="submit"]').click();
  const comicResponse = await comicCreated;
  expect(comicResponse.status()).toBe(201);
  const comic = await comicResponse.json() as { id: string; title: string; genreIds: string[] };
  expect(comic.title).toBe("A01 E2E Comic");
  expect(comic.genreIds).toHaveLength(1);

  await page.goto("/admin/chapters");
  const chapterForm = page.locator("form");
  await chapterForm.locator("select").first().selectOption(comic.id);
  await chapterForm.locator("input").nth(0).fill("A01 E2E Chapter");
  const chapterCreated = page.waitForResponse((response) =>
    response.request().method() === "POST" && response.url().endsWith("/api/v1/admin/chapters"));
  await chapterForm.locator('button[type="submit"]').click();
  const chapterResponse = await chapterCreated;
  expect(chapterResponse.status()).toBe(201);
  const chapter = await chapterResponse.json() as { id: string };

  const upload = page.waitForResponse((response) =>
    response.request().method() === "POST" &&
    response.url().endsWith(`/api/v1/admin/chapters/${chapter.id}/pages`));
  await page.locator('input[type="file"]').first().setInputFiles({
    name: "a01-page.png",
    mimeType: "image/png",
    buffer: Buffer.from("a01-real-e2e-page"),
  });
  expect((await upload).status()).toBe(200);
  await expect(page.getByRole("img")).toHaveCount(1);

  await page.reload();
  await page.getByRole("button", { name: /A01 E2E Chapter/ }).click();
  await expect(page.getByRole("img")).toHaveCount(1);
  const persistedManifest = await page.request.get(
    `/api/v1/admin/chapters/${chapter.id}/pages`,
    { headers: { "X-R03-E2E-User": adminId } },
  );
  expect(persistedManifest.status()).toBe(200);
  const persisted = await persistedManifest.json() as { pages: unknown[] };
  expect(persisted.pages).toHaveLength(1);
});
