import { expect, test } from "@playwright/test";

test("admin configures Mission, future VIP price and user status without privileged scope expansion", async ({ page }) => {
  const missionId = "11111111-1111-4111-8111-111111111111";
  const revisionId = "22222222-2222-4222-8222-222222222222";
  const userId = "33333333-3333-4333-8333-333333333333";
  let missionStatus = "Draft"; let missionVersion = 1; let activeRevisionId: string | null = null;
  let vipPrice = 29_000; let vipVersion = 1; let userStatus = "Active"; let userVersion = 1;
  const mission = () => ({ id: missionId, code: "DAILY_LOGIN", name: "Đăng nhập hằng ngày", description: "", actionType: "DailyLogin", cycleType: "Daily", status: missionStatus, activeRevisionId, createdAt: "2026-10-27T00:00:00Z", version: missionVersion, revisions: [{ id: revisionId, missionId, revisionNumber: 1, targetCount: 1, rewardBonusCoins: 5, repeatPolicy: "OncePerVietnamDate", effectiveFrom: "2026-10-27T00:00:00Z", effectiveUntil: null, configurationSnapshot: {} }] });
  const user = () => ({ id: userId, displayName: "Reader A03", email: "reader@example.test", avatarUrl: null, role: "User", status: userStatus, createdAt: "2026-10-27T00:00:00Z", updatedAt: "2026-10-27T00:00:00Z", version: userVersion });
  const userDetail = () => ({ user: user(), wallet: { paidAvailable: 10, paidReserved: 0, bonusAvailable: 5, totalAvailable: 15, version: 1 }, membership: { isActive: false, expiresAt: null, version: null }, access: { followedComicCount: 1, unlockedChapterCount: 2 }, recentPayments: [] });

  await page.route("**/api/v1/me", (route) => route.fulfill({ headers: { "X-CSRF-TOKEN": "a03-csrf" }, json: { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", displayName: "Admin", email: null, avatarUrl: null, role: "Admin", status: "Active" } }));
  await page.route("**/api/v1/admin/**", async (route) => {
    const request = route.request(); const path = new URL(request.url()).pathname; const method = request.method();
    if (path === "/api/v1/admin/missions" && method === "GET") return route.fulfill({ json: [mission()] });
    if (path.endsWith("/activate") && method === "POST") { missionStatus = "Active"; missionVersion++; activeRevisionId = revisionId; return route.fulfill({ json: mission() }); }
    if (path === "/api/v1/admin/vip-plan" && method === "GET") return route.fulfill({ json: { planId: revisionId, priceVnd: vipPrice, durationHours: 720, discountPercent: 20, version: vipVersion } });
    if (path === "/api/v1/admin/vip-plan" && method === "PATCH") { vipPrice = (request.postDataJSON() as { priceVnd: number }).priceVnd; vipVersion++; return route.fulfill({ json: { planId: revisionId, priceVnd: vipPrice, durationHours: 720, discountPercent: 20, version: vipVersion } }); }
    if (path === "/api/v1/admin/users" && method === "GET") return route.fulfill({ json: { items: [user()], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === `/api/v1/admin/users/${userId}` && method === "GET") return route.fulfill({ json: userDetail() });
    if (path.endsWith("/disable") && method === "POST") { userStatus = "Disabled"; userVersion++; return route.fulfill({ json: userDetail() }); }
    return route.fulfill({ status: 404, json: { errorCode: "RESOURCE_NOT_FOUND", message: "missing", traceId: "a03" } });
  });
  page.on("dialog", (dialog) => void dialog.accept());

  await page.goto("/admin/missions");
  await page.getByRole("button", { name: "DAILY_LOGIN" }).click();
  await page.getByRole("button", { name: "Kích hoạt" }).focus();
  await page.getByRole("button", { name: "Kích hoạt" }).press("Enter");
  await expect(page.getByRole("button", { name: "Đang active" })).toBeVisible();

  await page.goto("/admin/vip");
  await expect(page.getByText("720 giờ")).toBeVisible();
  await expect(page.getByText("20%")).toBeVisible();
  await page.getByLabel("Giá cho đơn VIP tương lai (VND)").fill("39000");
  await page.getByRole("button", { name: "Lưu giá tương lai" }).click();
  await expect(page.getByText("Đã cập nhật giá")).toBeVisible();

  await page.goto("/admin/users");
  await page.getByRole("button", { name: "Reader A03" }).click();
  await expect(page.getByText("User (chỉ đọc)")).toBeVisible();
  await page.getByRole("button", { name: "Tắt tài khoản" }).click();
  await expect(page.getByText("Đã cập nhật tài khoản")).toBeVisible();

  await page.goto("/admin/settings");
  await expect(page.getByText("300", { exact: true })).toBeVisible();
  await expect(page.getByText("Verification Required", { exact: false })).toBeVisible();
});
