import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";
const chapterId = "2744b2b6-f009-4122-b574-1dad21741dc9";
const sessionId = "70acb9d4-d02c-4a74-b607-9309eb5bdc31";

test("reader can follow, open a Free chapter, and navigate the Library", async ({ page }, testInfo) => {
  let followed = false;
  const heartbeatSequences: number[] = [];
  await page.clock.install();
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;

    if (path === "/api/v1/me") {
      await route.fulfill({
        json: {
          id: "a7bff851-a613-42ab-a089-82c399c7810e",
          displayName: "Bạn đọc R03",
          email: "reader@example.com",
          avatarUrl: null,
          role: "User",
          status: "Active",
        },
        headers: { "X-CSRF-TOKEN": "r03-csrf" },
      });
      return;
    }
    if (path === "/api/v1/comics" && request.method() === "GET") {
      await route.fulfill({ json: {
        items: [{
          id: comicId,
          slug: "hanh-trinh-r03",
          title: "Hành trình R03",
          coverAssetId: null,
          seriesStatus: "Ongoing",
          updateSortAt: "2026-10-14T02:00:00+00:00",
          genres: [{ id: "2b22fe02-2d14-4c88-966a-e4192955b9bf", name: "Phiêu lưu", slug: "phieu-luu" }],
        }],
        nextCursor: null,
      } });
      return;
    }
    if (path === "/api/v1/comics/hanh-trinh-r03") {
      await route.fulfill({ json: {
        id: comicId,
        slug: "hanh-trinh-r03",
        title: "Hành trình R03",
        description: "Một chương truyện miễn phí để kiểm thử hành trình đọc.",
        coverAssetId: null,
        seriesStatus: "Ongoing",
        firstPublishedAt: "2026-10-14T01:00:00+00:00",
        updatedAt: "2026-10-14T02:00:00+00:00",
        genres: [{ id: "2b22fe02-2d14-4c88-966a-e4192955b9bf", name: "Phiêu lưu", slug: "phieu-luu" }],
        authors: [],
        translationGroups: [],
      } });
      return;
    }
    if (path === `/api/v1/comics/${comicId}/chapters`) {
      await route.fulfill({ json: {
        items: [{
          id: chapterId,
          chapterLabel: "Chương 1",
          sortOrder: 1,
          publicationStatus: "Published",
          accessSummary: { accessType: "Free", canRead: true, finalPriceCoins: 0 },
        }],
        page: 1,
        pageSize: 30,
        totalItems: 1,
        totalPages: 1,
      } });
      return;
    }
    if (path === `/api/v1/comics/${comicId}/follow` && request.method() === "POST") {
      expect(request.headers()["x-csrf-token"]).toBe("r03-csrf");
      followed = true;
      await route.fulfill({
        status: 201,
        json: { comicId, followed: true, followedAt: "2026-10-14T02:00:00+00:00", created: true },
      });
      return;
    }
    if (path === `/api/v1/chapters/${chapterId}/access`) {
      await route.fulfill({ json: { chapterId, accessType: "Free", canRead: true, priceCoins: 0, needsLogin: false } });
      return;
    }
    if (path === `/api/v1/chapters/${chapterId}/pages`) {
      await route.fulfill({ json: {
        chapterId,
        pages: [{ order: 1, width: 800, height: 1200, deliveryUrl: `/api/v1/chapters/${chapterId}/pages/1/content` }],
        manifestIssuedAt: "2026-10-14T02:00:00+00:00",
        refreshHint: "retry-manifest",
      } });
      return;
    }
    if (path === `/api/v1/chapters/${chapterId}/pages/1/content`) {
      await route.fulfill({
        body: '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="1200"><rect width="800" height="1200" fill="#e7efed"/><path d="M80 200h640v80H80zm0 180h480v80H80zm0 180h600v80H80z" fill="#c73b4b"/></svg>',
        contentType: "image/svg+xml",
      });
      return;
    }
    if (path === "/api/v1/reading/sessions" && request.method() === "POST") {
      await route.fulfill({ status: 201, json: {
        sessionId,
        startedAt: "2026-10-14T02:00:00+00:00",
        expiresAt: "2026-10-14T02:30:00+00:00",
        policy: { revision: "r02-v1", minimumProgressBasisPoints: 9000, minimumActiveDurationMs: 15000 },
        manifestRevision: "manifest-one",
        manifestPageCount: 1,
        version: 1,
      } });
      return;
    }
    if (path === `/api/v1/reading/sessions/${sessionId}` && request.method() === "PATCH") {
      const heartbeat = request.postDataJSON() as {
        currentPageOrder: number;
        currentPageOffsetRatio: number;
        sequenceNumber: number;
      };
      heartbeatSequences.push(heartbeat.sequenceNumber);
      await route.fulfill({ json: {
        sessionId,
        acceptedPosition: {
          pageOrder: heartbeat.currentPageOrder,
          pageOffsetRatio: heartbeat.currentPageOffsetRatio,
        },
        acceptedMaxProgressBasisPoints: heartbeat.sequenceNumber === 1 ? 5000 : 9500,
        activeDurationMs: heartbeat.sequenceNumber === 1 ? 0 : 15000,
        manifestRevision: "manifest-one",
        manifestPageCount: 1,
        lastAcceptedSequence: heartbeat.sequenceNumber,
        expiresAt: "2026-10-14T02:30:00+00:00",
        version: heartbeat.sequenceNumber + 1,
      } });
      return;
    }
    if (path === `/api/v1/reading/sessions/${sessionId}/qualify` && request.method() === "POST") {
      await route.fulfill({ json: {
        qualified: true,
        qualifiedAt: "2026-10-14T02:00:20+00:00",
        reasonCode: null,
        version: 4,
      } });
      return;
    }
    if (path === "/api/v1/reading/continue") {
      await route.fulfill({ json: { items: [], nextCursor: null } });
      return;
    }
    if (path === "/api/v1/library/follows") {
      await route.fulfill({ json: {
        items: followed ? [{
          comicId,
          availability: "Available",
          slug: "hanh-trinh-r03",
          title: "Hành trình R03",
          coverAssetId: null,
          seriesStatus: "Ongoing",
          followedAt: "2026-10-14T02:00:00+00:00",
        }] : [],
        nextCursor: null,
      } });
      return;
    }
    await route.abort("failed");
  });

  await page.goto("/comics");
  await page.getByRole("link", { name: "Mở Hành trình R03" }).click();
  await page.getByRole("button", { name: "Theo dõi truyện" }).click();
  await expect(page.getByRole("button", { name: "Đã theo dõi" })).toBeVisible();
  await page.getByRole("link", { name: "Đọc từ đầu" }).click();
  await expect(page.getByRole("img", { name: "Trang 1" })).toBeVisible();
  await expect(page.getByText(/Đã đồng bộ/)).toBeVisible();
  await page.clock.fastForward(10_001);
  await expect.poll(() => heartbeatSequences).toEqual([1]);
  await page.clock.fastForward(10_001);
  await expect.poll(() => heartbeatSequences).toEqual([1, 2]);
  await expect(page.getByText("Đã xác nhận hoàn thành")).toBeVisible();

  const readerAccessibility = await new AxeBuilder({ page }).analyze();
  expect(readerAccessibility.violations).toEqual([]);
  await page.screenshot({
    path: `artifacts/r03-reader-${testInfo.project.name}.png`,
    fullPage: true,
  });

  await page.goto("/library");
  const continueTab = page.getByRole("tab", { name: "Đọc tiếp" });
  await continueTab.focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: "Đang theo dõi" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("heading", { name: "Hành trình R03" })).toBeVisible();
  const libraryAccessibility = await new AxeBuilder({ page }).analyze();
  expect(libraryAccessibility.violations).toEqual([]);
});
