import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { AdminRefundsPage } from "@/pages/AdminRefundsPage";
import { profileQueryKey, type Profile } from "@/features/profile/profile-api";
import { createQueryClient } from "@/shared/api/query-client";

const refundId = "11111111-1111-4111-8111-111111111111";
const refund = {
  refundId, paymentOrderId: "22222222-2222-4222-8222-222222222222", orderCode: "TOPUP-A02",
  userId: "33333333-3333-4333-8333-333333333333", userDisplayName: "Người đọc", userEmail: "reader@example.test",
  status: "Requested", originalAmountVnd: 100000, feeVnd: 5000, netRefundVnd: 95000,
  requiredRecoveryCoins: 10000, requestedAt: "2026-10-27T00:00:00Z", version: 1,
};
const adminA: Profile = { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", displayName: "Admin A", email: "admin-a@example.test", avatarUrl: null, role: "Admin", status: "Active" };
const adminB: Profile = { id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", displayName: "Admin B", email: "admin-b@example.test", avatarUrl: null, role: "Admin", status: "Active" };

describe("AdminRefundsPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows protected evidence and confirms the exact-lot approval workflow", async () => {
    let current = { ...refund };
    const mutations: Array<{ path: string; headers: Headers; body: unknown }> = [];
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    vi.stubGlobal("crypto", { randomUUID: () => "a02-idempotency-key" });
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const path = new URL(url, "https://localhost").pathname;
      const method = init?.method ?? "GET";
      if (path === "/api/v1/admin/refunds" && method === "GET")
        return Promise.resolve(Response.json({ items: [current], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 }));
      if (path === `/api/v1/admin/refunds/${refundId}` && method === "GET")
        return Promise.resolve(Response.json({ refund: current, reason: "Hoàn TopUp gốc", recipient: { bankCode: "VCB", accountNumberMasked: "******6789", accountHolderMasked: "N*** V*** A***" }, originalPaidCoinLot: { lotId: "44444444-4444-4444-8444-444444444444", grantedCoins: 10000, spentCoins: 0, refundedCoins: 0, reservedCoins: 0, availableCoins: 10000, version: 1 }, reservation: null, transfer: null, auditTimeline: [] }));
      if (path.endsWith("/review") && method === "POST") {
        current = { ...current, status: "UnderReview", version: 2 };
        return Promise.resolve(Response.json({ refundId, status: current.status, requiredRecoveryCoins: 10000, version: 2 }));
      }
      if (path.endsWith("/approve") && method === "POST") {
        mutations.push({ path, headers: new Headers(init?.headers), body: JSON.parse(init?.body as string) });
        current = { ...current, status: "RecoveryReserved", version: 3 };
        return Promise.resolve(Response.json({ refundId, status: current.status, requiredRecoveryCoins: 10000, version: 3, reservationId: "55555555-5555-4555-8555-555555555555" }));
      }
      throw new Error(`Unexpected ${method} ${url}`);
    }));

    const router = createMemoryRouter([{ path: "/admin/refunds", element: <AdminRefundsPage /> }], { initialEntries: ["/admin/refunds"] });
    const queryClient = createQueryClient();
    queryClient.setQueryData(profileQueryKey, adminA);
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "TOPUP-A02" }));
    expect(await screen.findByText(/\*\*\*\*\*\*6789/)).toBeVisible();
    expect(screen.queryByText("123456789")).not.toBeInTheDocument();
    expect(screen.getByText(/Available/)).toHaveTextContent("10000");
    await user.click(screen.getByRole("button", { name: "Bắt đầu xem xét" }));
    await user.type(await screen.findByLabelText("Ghi chú quyết định"), "Đã đối chiếu lot gốc");
    await user.click(screen.getByRole("button", { name: "Duyệt và giữ coin" }));
    await waitFor(() => expect(mutations).toHaveLength(1));
    expect(mutations[0]?.headers.get("Idempotency-Key")).toBe("a02-idempotency-key");
    expect(mutations[0]?.body).toEqual({ expectedVersion: 2, decisionNote: "Đã đối chiếu lot gốc" });
    expect(confirmSpy).toHaveBeenCalledWith(expect.stringContaining("PaidCoinLot gốc"));
  });

  it("fetches a separate finance cache when the authenticated admin changes", async () => {
    let activeAdmin = adminA;
    const listRequests: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = input instanceof Request ? input.url : input.toString();
      const path = new URL(url, "https://localhost").pathname;
      if (path !== "/api/v1/admin/refunds") throw new Error(`Unexpected GET ${url}`);
      listRequests.push(activeAdmin.id);
      return Promise.resolve(Response.json({
        items: [{ ...refund, refundId: activeAdmin.id, orderCode: activeAdmin.displayName, userDisplayName: activeAdmin.displayName }],
        page: 1, pageSize: 30, totalItems: 1, totalPages: 1,
      }));
    }));
    const queryClient = createQueryClient();
    queryClient.setQueryData(profileQueryKey, adminA);
    const router = createMemoryRouter([{ path: "/admin/refunds", element: <AdminRefundsPage /> }], { initialEntries: ["/admin/refunds"] });
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    expect(await screen.findByRole("button", { name: "Admin A" })).toBeVisible();

    activeAdmin = adminB;
    void act(() => {
      queryClient.setQueryData(profileQueryKey, adminB);
    });
    expect(await screen.findByRole("button", { name: "Admin B" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Admin A" })).not.toBeInTheDocument();
    expect(listRequests).toEqual([adminA.id, adminB.id]);
  });

  it("resets transfer evidence when selecting a different refund", async () => {
    const refundB = { ...refund, refundId: "22222222-2222-4222-8222-222222222222", orderCode: "TOPUP-B02", status: "RecoveryReserved", version: 3 };
    const refundA = { ...refund, status: "RecoveryReserved", version: 3 };
    const submitted: unknown[] = [];
    vi.spyOn(window, "confirm").mockReturnValue(true);
    vi.stubGlobal("crypto", { randomUUID: () => "a02-transfer-key" });
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const path = new URL(url, "https://localhost").pathname;
      const method = init?.method ?? "GET";
      if (path === "/api/v1/admin/refunds" && method === "GET")
        return Promise.resolve(Response.json({ items: [refundA, refundB], page: 1, pageSize: 30, totalItems: 2, totalPages: 1 }));
      const selectedRefund = path.endsWith(refundB.refundId) ? refundB : refundA;
      if (method === "GET") return Promise.resolve(Response.json({ refund: selectedRefund, reason: "Reason", recipient: { bankCode: "VCB", accountNumberMasked: "******6789", accountHolderMasked: "N***" }, originalPaidCoinLot: { lotId: "44444444-4444-4444-8444-444444444444", grantedCoins: 10000, spentCoins: 0, refundedCoins: 0, reservedCoins: 10000, availableCoins: 0, version: 2 }, reservation: { reservationId: "55555555-5555-4555-8555-555555555555", walletOperationId: "66666666-6666-4666-8666-666666666666", amountCoins: 10000, status: "Active", createdAt: "2026-10-27T00:00:00Z", resolvedAt: null }, transfer: null, auditTimeline: [] }));
      submitted.push(JSON.parse(init?.body as string));
      return Promise.resolve(Response.json(selectedRefund));
    }));
    const queryClient = createQueryClient();
    queryClient.setQueryData(profileQueryKey, adminA);
    const router = createMemoryRouter([{ path: "/admin/refunds", element: <AdminRefundsPage /> }], { initialEntries: ["/admin/refunds"] });
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "TOPUP-A02" }));
    await user.type(await screen.findByLabelText("Mã tham chiếu ngân hàng"), "BANK-A");
    await user.type(screen.getByLabelText("Tham chiếu bằng chứng"), "EVIDENCE-A");
    await user.selectOptions(screen.getByLabelText("Kết quả được báo cáo"), "Succeeded");

    await user.click(screen.getByRole("button", { name: "TOPUP-B02" }));
    expect(await screen.findByLabelText("Mã tham chiếu ngân hàng")).toHaveValue("");
    expect(screen.getByLabelText("Tham chiếu bằng chứng")).toHaveValue("");
    expect(screen.getByLabelText("Kết quả được báo cáo")).toHaveValue("Unknown");
    expect(screen.getByRole("button", { name: "Ghi nhận bằng chứng" })).toBeDisabled();
    expect(submitted).toHaveLength(0);
  });
});
