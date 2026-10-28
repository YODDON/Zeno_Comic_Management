import { render, screen } from "@testing-library/react";
import { App } from "@/app/App";
import { router } from "@/app/router";

describe("application shell", () => {
  afterEach(() => vi.unstubAllGlobals());
  it("renders the Vietnamese user shell", async () => {
    await router.navigate("/");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Đọc theo nhịp của bạn." })).toBeVisible();
    expect(screen.getByRole("navigation", { name: "Điều hướng chính" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Khám phá kho truyện" })).toHaveAttribute(
      "href",
      "/comics",
    );
  });

  it("renders the light-only admin shell with authoritative dashboard data", async () => {
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const path = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost").pathname;
      if (path === "/api/v1/admin/dashboard") return Promise.resolve(Response.json({
        range: { from: "2026-10-01", to: "2026-10-30", startsAtUtc: "2026-09-30T17:00:00Z", endsAtUtc: "2026-10-30T17:00:00Z" },
        revenue: { topUpGrossVnd: 0, vipGrossVnd: 0 }, refunds: { completedCount: 0, originalAmountVnd: 0, feeVnd: 0, netRefundVnd: 0 },
        coinsIssued: { paidCoins: 0, bonusCoins: 0 }, reading: { qualifiedSessionCount: 0, distinctReaderCount: 0 },
        missions: { actionReceiptCount: 0, grantedRewardCount: 0, deniedDailyCapCount: 0, grantedBonusCoins: 0 }, dailySeries: [],
      }));
      return Promise.resolve(Response.json({
        id: "11111111-1111-4111-8111-111111111111", displayName: "Admin", email: "admin@example.test",
        avatarUrl: null, role: "Admin", status: "Active",
      }, { headers: { "X-CSRF-TOKEN": "app-test-csrf" } }));
    }));
    await router.navigate("/admin");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Tổng quan" })).toBeVisible();
    expect(screen.getByText("Báo cáo quản trị")).toBeVisible();
    expect(document.querySelector(".admin-shell")).toBeInTheDocument();
  });
});
