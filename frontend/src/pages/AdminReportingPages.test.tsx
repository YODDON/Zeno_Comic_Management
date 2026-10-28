import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { ReactNode } from "react";
import { profileQueryKey, type Profile } from "@/features/profile/profile-api";
import { createQueryClient } from "@/shared/api/query-client";
import { AdminFoundationPage } from "@/pages/AdminFoundationPage";
import { AdminAuditPage } from "@/pages/AdminAuditPage";

const admin: Profile = { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", displayName: "Admin", email: null, avatarUrl: null, role: "Admin", status: "Active" };

function renderPage(element: ReactNode, path: string) {
  const client = createQueryClient();
  client.setQueryData(profileQueryKey, admin);
  const router = createMemoryRouter([{ path, element }], { initialEntries: [path] });
  render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>);
}

describe("A04 reporting pages", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("loads explicit dashboard metric groups and applies Vietnam calendar dates", async () => {
    const requests: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost");
      requests.push(url.search);
      return Promise.resolve(Response.json({
        range: { from: "2026-10-01", to: "2026-10-02", startsAtUtc: "2026-09-30T17:00:00Z", endsAtUtc: "2026-10-02T17:00:00Z" },
        revenue: { topUpGrossVnd: 100000, vipGrossVnd: 29000 },
        refunds: { completedCount: 1, originalAmountVnd: 100000, feeVnd: 5000, netRefundVnd: 95000 },
        coinsIssued: { paidCoins: 10000, bonusCoins: 20 },
        reading: { qualifiedSessionCount: 2, distinctReaderCount: 1 },
        missions: { actionReceiptCount: 3, grantedRewardCount: 2, deniedDailyCapCount: 1, grantedBonusCoins: 20 },
        dailySeries: [],
      }));
    }));
    renderPage(<AdminFoundationPage />, "/admin");
    const user = userEvent.setup();
    const topUpMetric = (await screen.findByRole("heading", { name: "TopUp gross" })).closest("article");
    expect(topUpMetric).not.toBeNull();
    expect(within(topUpMetric!).getByText(/100\.000/)).toBeVisible();
    await user.type(screen.getByLabelText("Từ ngày"), "2026-10-01");
    await user.type(screen.getByLabelText("Đến ngày"), "2026-10-02");
    await user.click(screen.getByRole("button", { name: "Áp dụng" }));
    await waitFor(() => expect(requests.at(-1)).toContain("from=2026-10-01&to=2026-10-02"));
  });

  it("paginates audit and renders only redacted changes", async () => {
    const pages: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost");
      const page = url.searchParams.get("page") ?? "1";
      pages.push(page);
      return Promise.resolve(Response.json({
        items: [{ id: `00000000-0000-4000-8000-00000000000${page}`, actorId: admin.id, action: "Refund.Completed", targetType: "RefundRequest", targetId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", occurredAt: "2026-10-01T00:00:00Z", redactedChanges: "{\"status\":\"Completed\"}" }],
        page: Number(page), pageSize: 30, totalItems: 31, totalPages: 2,
      }));
    }));
    renderPage(<AdminAuditPage />, "/admin/audit");
    const user = userEvent.setup();
    expect(await screen.findByText(/Refund.Completed/)).toBeVisible();
    expect(screen.getByText(/"status": "Completed"/)).toBeVisible();
    expect(screen.queryByText(/accountNumber/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Trang sau" }));
    await waitFor(() => expect(pages).toEqual(["1", "2"]));
  });
});
