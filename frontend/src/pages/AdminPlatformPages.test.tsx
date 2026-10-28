import { QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { ReactNode } from "react";
import { profileQueryKey, type Profile } from "@/features/profile/profile-api";
import type { MissionDefinition } from "@/features/admin-platform/admin-platform-api";
import { AdminMissionsPage } from "@/pages/AdminMissionsPage";
import { AdminUsersPage } from "@/pages/AdminUsersPage";
import { AdminVipPage } from "@/pages/AdminVipPage";
import { createQueryClient } from "@/shared/api/query-client";

const admin: Profile = { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", displayName: "Admin", email: "admin@example.test", avatarUrl: null, role: "Admin", status: "Active" };

function renderPage(element: ReactNode, path: string) {
  const client = createQueryClient();
  client.setQueryData(profileQueryKey, admin);
  const router = createMemoryRouter([{ path, element }], { initialEntries: [path] });
  render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>);
  return client;
}

describe("A03 admin platform pages", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("edits only the future VIP price and sends the authoritative version", async () => {
    const bodies: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((_input: string | URL | Request, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "PATCH") {
        bodies.push(JSON.parse(init!.body as string));
        return Promise.resolve(Response.json({ planId: "11111111-1111-4111-8111-111111111111", priceVnd: 39000, durationHours: 720, discountPercent: 20, version: 2 }));
      }
      return Promise.resolve(Response.json({ planId: "11111111-1111-4111-8111-111111111111", priceVnd: 29000, durationHours: 720, discountPercent: 20, version: 1 }));
    }));
    renderPage(<AdminVipPage />, "/admin/vip");
    const user = userEvent.setup();
    expect(await screen.findByText("720 giờ")).toBeVisible();
    expect(screen.getByText("20%")).toBeVisible();
    await user.clear(screen.getByLabelText("Giá cho đơn VIP tương lai (VND)"));
    await user.type(screen.getByLabelText("Giá cho đơn VIP tương lai (VND)"), "39000");
    await user.click(screen.getByRole("button", { name: "Lưu giá tương lai" }));
    await waitFor(() => expect(bodies).toEqual([{ priceVnd: 39000, expectedVersion: 1 }]));
  });

  it("activates the selected immutable Mission revision with expectedVersion", async () => {
    let mission: MissionDefinition = { id: "11111111-1111-4111-8111-111111111111", code: "LOGIN", name: "Đăng nhập", description: "", actionType: "DailyLogin", cycleType: "Daily", status: "Draft", activeRevisionId: null, createdAt: "2026-10-27T00:00:00Z", version: 1, revisions: [{ id: "22222222-2222-4222-8222-222222222222", missionId: "11111111-1111-4111-8111-111111111111", revisionNumber: 1, targetCount: 1, rewardBonusCoins: 5, repeatPolicy: "OncePerVietnamDate", effectiveFrom: "2026-10-27T00:00:00Z", effectiveUntil: null, configurationSnapshot: {} }] };
    const bodies: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const path = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost").pathname;
      if (path === "/api/v1/admin/mission-rewards") {
        return Promise.resolve(Response.json({ items: [], page: 1, pageSize: 30, totalItems: 0, totalPages: 0 }));
      }
      if (path.endsWith("/activate")) {
        bodies.push(JSON.parse(init!.body as string));
        mission = { ...mission, status: "Active", activeRevisionId: mission.revisions[0]!.id, version: 2 };
        return Promise.resolve(Response.json(mission));
      }
      return Promise.resolve(Response.json([mission]));
    }));
    renderPage(<AdminMissionsPage />, "/admin/missions");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "LOGIN" }));
    await user.click(screen.getByRole("button", { name: "Kích hoạt" }));
    await waitFor(() => expect(bodies).toEqual([{ revisionId: mission.revisions[0]!.id, expectedVersion: 1 }]));
  });

  it("loads mission reward history under the current Admin identity and filters denied grants", async () => {
    const requests: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost");
      if (url.pathname === "/api/v1/admin/mission-rewards") {
        requests.push(`${url.searchParams.get("outcome") ?? ""}:${url.searchParams.get("page")}`);
        return Promise.resolve(Response.json({
          items: url.searchParams.get("outcome") === "DeniedDailyCap" ? [{
            rewardGrantId: "33333333-3333-4333-8333-333333333333",
            userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            userDisplayName: "Reader",
            userEmail: null,
            missionId: "11111111-1111-4111-8111-111111111111",
            revisionId: "22222222-2222-4222-8222-222222222222",
            missionName: "Đọc truyện",
            outcome: "DeniedDailyCap",
            rewardBonusCoins: 10,
            grantedBonusCoins: 0,
            localDate: "2026-10-27",
            decidedAt: "2026-10-27T00:00:00Z",
          }] : [],
          page: 1, pageSize: 30, totalItems: 1, totalPages: 1,
        }));
      }
      return Promise.resolve(Response.json([]));
    }));
    renderPage(<AdminMissionsPage />, "/admin/missions");
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText("Kết quả"), "DeniedDailyCap");

    expect(await screen.findByText("Đọc truyện")).toBeVisible();
    expect(requests).toEqual([":1", "DeniedDailyCap:1"]);
  });

  it("pages through more than thirty reward decisions without losing the selected page", async () => {
    const requestedPages: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost");
      if (url.pathname === "/api/v1/admin/mission-rewards") {
        const page = url.searchParams.get("page") ?? "1";
        requestedPages.push(page);
        const item = {
          rewardGrantId: page === "1" ? "11111111-1111-4111-8111-111111111111" : "22222222-2222-4222-8222-222222222222",
          userId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
          userDisplayName: page === "1" ? "Reader trang một" : "Reader trang hai",
          userEmail: null,
          missionId: "33333333-3333-4333-8333-333333333333",
          revisionId: "44444444-4444-4444-8444-444444444444",
          missionName: "Đọc truyện",
          outcome: "Granted",
          rewardBonusCoins: 10,
          grantedBonusCoins: 10,
          localDate: "2026-10-27",
          decidedAt: "2026-10-27T00:00:00Z",
        };
        return Promise.resolve(Response.json({ items: [item], page: Number(page), pageSize: 30, totalItems: 31, totalPages: 2 }));
      }
      return Promise.resolve(Response.json([]));
    }));
    renderPage(<AdminMissionsPage />, "/admin/missions");
    const user = userEvent.setup();

    expect(await screen.findByText("Reader trang một")).toBeVisible();
    expect(screen.getByRole("button", { name: "Trang trước" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Trang sau" }));

    expect(await screen.findByText("Reader trang hai")).toBeVisible();
    expect(screen.getByText("Trang 2/2 · 31 quyết định")).toBeVisible();
    expect(screen.getByRole("button", { name: "Trang sau" })).toBeDisabled();
    expect(requestedPages).toEqual(["1", "2"]);
  });

  it("matches the Mission domain name and description boundaries", async () => {
    const bodies: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost");
      if (url.pathname === "/api/v1/admin/mission-rewards") {
        return Promise.resolve(Response.json({ items: [], page: 1, pageSize: 30, totalItems: 0, totalPages: 0 }));
      }
      if ((init?.method ?? "GET") === "POST") {
        if (typeof init?.body !== "string") {
          throw new Error("Expected a JSON Mission create request body.");
        }
        const parsed: unknown = JSON.parse(init.body);
        if (typeof parsed !== "object" || parsed === null ||
            !("name" in parsed) || typeof parsed.name !== "string" ||
            !("description" in parsed) || typeof parsed.description !== "string") {
          throw new Error("Unexpected Mission create request body.");
        }
        const body = { name: parsed.name, description: parsed.description };
        bodies.push(body);
        return Promise.resolve(Response.json({ id: "11111111-1111-4111-8111-111111111111", code: "BOUNDARY", ...body, actionType: "DailyLogin", cycleType: "Daily", status: "Draft", activeRevisionId: null, createdAt: "2026-10-27T00:00:00Z", version: 1, revisions: [] }, { status: 201 }));
      }
      return Promise.resolve(Response.json([]));
    }));
    renderPage(<AdminMissionsPage />, "/admin/missions");
    const user = userEvent.setup();
    await user.click(await screen.findByText("Tạo MissionDefinition"));
    const name = screen.getByLabelText("Tên");
    const description = screen.getByLabelText("Mô tả");
    expect(name).toHaveAttribute("maxlength", "200");
    expect(description).toHaveAttribute("maxlength", "2000");
    await user.type(screen.getByLabelText("Mã"), "BOUNDARY");
    fireEvent.change(name, { target: { value: "N".repeat(200) } });
    fireEvent.change(description, { target: { value: "D".repeat(2000) } });
    await user.type(name, "X");
    await user.type(description, "X");
    expect(name).toHaveValue("N".repeat(200));
    expect(description).toHaveValue("D".repeat(2000));
    await user.click(screen.getByRole("button", { name: "Tạo Mission" }));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect((bodies[0] as { name: string; description: string }).name).toHaveLength(200);
    expect((bodies[0] as { name: string; description: string }).description).toHaveLength(2000);
  });

  it("scopes user detail to the Admin identity and sends optimistic disable", async () => {
    const statusBodies: unknown[] = [];
    const userRecord = { id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", displayName: "Reader", email: "reader@example.test", avatarUrl: null, role: "User", status: "Active", createdAt: "2026-10-27T00:00:00Z", updatedAt: "2026-10-27T00:00:00Z", version: 4 };
    const detail = { user: userRecord, wallet: { paidAvailable: 10, paidReserved: 0, bonusAvailable: 5, totalAvailable: 15, version: 1 }, membership: { isActive: false, expiresAt: null, version: null }, access: { followedComicCount: 2, unlockedChapterCount: 1 }, recentPayments: [] };
    vi.spyOn(window, "confirm").mockReturnValue(true);
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const path = new URL(input instanceof Request ? input.url : input.toString(), "https://localhost").pathname;
      if (path.endsWith("/disable")) {
        statusBodies.push(JSON.parse(init!.body as string));
        return Promise.resolve(Response.json({ ...detail, user: { ...userRecord, status: "Disabled", version: 5 } }));
      }
      if (path === `/api/v1/admin/users/${userRecord.id}`) return Promise.resolve(Response.json(detail));
      return Promise.resolve(Response.json({ items: [userRecord], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 }));
    }));
    renderPage(<AdminUsersPage />, "/admin/users");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Reader" }));
    await user.click(await screen.findByRole("button", { name: "Tắt tài khoản" }));
    await waitFor(() => expect(statusBodies).toEqual([{ expectedVersion: 4 }]));
    expect(await screen.findByText("Đã cập nhật tài khoản")).toBeVisible();
  });

  it("does not reuse an Admin user-list cache after direct identity change", async () => {
    const adminB = { ...admin, id: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", displayName: "Admin B" };
    let active = admin;
    const requests: string[] = [];
    vi.stubGlobal("fetch", vi.fn(() => {
      requests.push(active.id);
      const user = { id: active.id, displayName: active.displayName, email: null, avatarUrl: null, role: "Admin", status: "Active", createdAt: "2026-10-27T00:00:00Z", updatedAt: "2026-10-27T00:00:00Z", version: 1 };
      return Promise.resolve(Response.json({ items: [user], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 }));
    }));
    const client = renderPage(<AdminUsersPage />, "/admin/users");
    expect(await screen.findByRole("button", { name: "Admin" })).toBeVisible();
    active = adminB;
    act(() => { client.setQueryData(profileQueryKey, adminB); });
    expect(await screen.findByRole("button", { name: "Admin B" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Admin" })).not.toBeInTheDocument();
    expect(requests).toEqual([admin.id, adminB.id]);
  });
});
