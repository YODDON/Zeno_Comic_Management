/// <reference types="node" />
import { expect, test } from "@playwright/test";

const comicId = "11111111-1111-1111-1111-111111111111";
const chapterId = "22222222-2222-2222-2222-222222222222";
const pageA = "33333333-3333-3333-3333-333333333333";
const pageB = "44444444-4444-4444-4444-444444444444";

test("admin completes comic, chapter-page and taxonomy editor workflows", async ({ page }) => {
  let comicVersion = 3;
  let chapterVersion = 3;
  let pages = [{ id: pageA, pageOrder: 1, assetStatus: "Ready", width: 800, height: 1200 }];
  let taxonomyName = "Phiêu lưu";

  await page.route("**/api/v1/me", (route) => route.fulfill({
    headers: { "X-CSRF-TOKEN": "a01-csrf" },
    json: { id: "11111111-1111-4111-8111-111111111111", displayName: "Admin", email: "admin@example.test", avatarUrl: null, role: "Admin", status: "Active" },
  }));

  await page.route("**/api/v1/admin/**", async (route) => {
    const request = route.request(); const path = new URL(request.url()).pathname; const method = request.method();
    if (path === "/api/v1/admin/comics" && method === "GET") return route.fulfill({ json: { items: [{
      id: comicId, slug: "comic-a", title: "Comic A", description: "Mô tả", seriesStatus: "Ongoing",
      publicationStatus: "Draft", coverAssetId: null, defaultPriceCoins: 0, defaultVipEligible: false,
      version: comicVersion, genreIds: [], authorIds: [], translationGroupIds: [],
    }], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === `/api/v1/admin/comics/${comicId}` && method === "PATCH") {
      const body: unknown = request.postDataJSON(); comicVersion++;
      if (typeof body !== "object" || body === null) throw new Error("Invalid comic payload");
      return route.fulfill({ json: { ...body, id: comicId, publicationStatus: "Draft", version: comicVersion } });
    }
    if (path === `/api/v1/admin/comics/${comicId}/publish`) {
      expect(request.postDataJSON()).toEqual({ expectedVersion: comicVersion }); comicVersion++;
      return route.fulfill({ json: { id: comicId, publicationStatus: "Published", version: comicVersion, firstPublishedAt: "2026-10-26T00:00:00Z" } });
    }
    if (path === "/api/v1/admin/chapters" && method === "GET") return route.fulfill({ json: { items: [{
      id: chapterId, comicId, chapterLabel: "Chương 1", sortOrder: 1, priceOverrideCoins: null,
      vipOverride: null, publicationStatus: "Draft", version: chapterVersion,
    }], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === `/api/v1/admin/chapters/${chapterId}/pages` && method === "GET")
      return route.fulfill({ json: { chapterId, version: chapterVersion, pages } });
    if (path === `/api/v1/admin/chapters/${chapterId}/pages` && method === "POST") {
      expect(request.postData()).toContain(`\r\n\r\n${chapterVersion}\r\n`);
      chapterVersion++; pages = [...pages, { id: pageB, pageOrder: 2, assetStatus: "Ready", width: 900, height: 1400 }];
      return route.fulfill({ json: { chapterId, version: chapterVersion, pages } });
    }
    if (path === `/api/v1/admin/chapters/${chapterId}/pages/order` && method === "PUT") {
      const body = request.postDataJSON() as { expectedVersion: number; pageIds: string[] };
      expect(body.expectedVersion).toBe(chapterVersion); chapterVersion++;
      pages = body.pageIds.map((id, index) => ({ ...(pages.find((item) => item.id === id)!), pageOrder: index + 1 }));
      return route.fulfill({ json: { chapterId, version: chapterVersion, pages } });
    }
    if (path.includes(`/api/v1/admin/chapters/${chapterId}/pages/`) && path.endsWith("/content") && method === "GET")
      return route.fulfill({ contentType: "image/png", body: Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "base64") });
    if (path.includes(`/api/v1/admin/chapters/${chapterId}/pages/`) && path.endsWith("/content") && method === "PUT") {
      chapterVersion++;
      return route.fulfill({ json: { chapterId, version: chapterVersion, pages } });
    }
    if (path.includes(`/api/v1/admin/chapters/${chapterId}/pages/`) && method === "DELETE") {
      const pageId = path.split("/").at(-1); pages = pages.filter((item) => item.id !== pageId); chapterVersion++;
      return route.fulfill({ json: { chapterId, version: chapterVersion, pages } });
    }
    if (path === "/api/v1/admin/taxonomy/genres" && method === "GET")
      return route.fulfill({ json: [{ id: "g", name: taxonomyName, slug: "phieu-luu" }] });
    if (path === "/api/v1/admin/taxonomy/authors" || path === "/api/v1/admin/taxonomy/translation-groups")
      return route.fulfill({ json: [] });
    if (path === "/api/v1/admin/taxonomy/genres/g" && method === "PATCH") {
      taxonomyName = (request.postDataJSON() as { name: string }).name;
      return route.fulfill({ json: { id: "g", name: taxonomyName, slug: "phieu-luu" } });
    }
    if (path === "/api/v1/admin/taxonomy/genres/g" && method === "DELETE")
      return route.fulfill({ json: { id: "g", name: taxonomyName, slug: "phieu-luu" } });
    return route.fulfill({ status: 404, json: { errorCode: "RESOURCE_NOT_FOUND", message: "missing", traceId: "e2e" } });
  });

  await page.goto("/admin/comics");
  await page.getByRole("button", { name: /Comic A/ }).click();
  await page.getByLabel(/Ảnh bìa \/ Asset ID/).fill("cover-a");
  await page.getByLabel(/Phiêu lưu/).check();
  await page.getByRole("button", { name: "Lưu truyện" }).click();
  await page.getByRole("button", { name: "Xuất bản truyện" }).click();
  await expect(page.getByRole("button", { name: "Ẩn truyện" })).toBeVisible();

  await page.goto("/admin/chapters");
  await page.getByRole("button", { name: /Chương 1/ }).click();
  await page.getByLabel("Thêm ảnh").setInputFiles({ name: "second.png", mimeType: "image/png", buffer: Buffer.from([1, 2, 3]) });
  await expect(page.getByText(/second.png: đã tải/)).toBeVisible();
  await expect(page.getByRole("img", { name: "Trang 2" })).toBeVisible();
  await page.getByRole("button", { name: "Lên" }).last().click();
  await expect(page.getByRole("img", { name: "Trang 1" })).toBeVisible();
  await page.getByLabel("Thay ảnh").first().setInputFiles({ name: "replacement.png", mimeType: "image/png", buffer: Buffer.from([4]) });
  await page.getByRole("button", { name: "Xóa" }).last().click();
  await expect(page.getByRole("img")).toHaveCount(1);

  await page.goto("/admin/genres");
  await page.getByRole("button", { name: /Phiêu lưu/ }).click();
  await page.getByLabel("Tên").fill("Hành động");
  await page.getByRole("button", { name: "Cập nhật" }).click();
  await expect.poll(() => taxonomyName).toBe("Hành động");
});
