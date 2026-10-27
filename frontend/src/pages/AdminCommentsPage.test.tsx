import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AdminCommentsPage } from "@/pages/AdminCommentsPage";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const commentId = "b242b27f-6665-4bf0-a75d-030a8ca5b454";
const parentId = "63a85156-1a47-4cb1-a3f8-ec779e91cd7c";
const userId = "14b84f73-ff6b-4564-a2e7-8544e5af86a6";
const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";
const timestamp = "2026-10-23T10:00:00+00:00";

function response(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": "csrf" } });
}

describe("AdminCommentsPage", () => {
  afterEach(() => { clearApiSessionState(); vi.unstubAllGlobals(); });

  it("submits an audited moderation reason and refreshes the allowed transitions", async () => {
    const requests: Array<{ url: string; method: string; body?: Record<string, unknown> }> = [];
    let status = "Visible";
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const method = init?.method ?? "GET";
      const body = typeof init?.body === "string" ? JSON.parse(init.body) as Record<string, unknown> : undefined;
      requests.push({ url, method, body });
      if (url.endsWith("/api/v1/me")) return Promise.resolve(response({ id: userId, displayName: "Admin", email: null, avatarUrl: null, role: "Admin", status: "Active" }));
      if (url.includes("/api/v1/admin/comments?") && method === "GET") return Promise.resolve(response({ items: [{ id: commentId, userId, displayName: "Bạn đọc", avatarUrl: null, comicId, chapterId: null, parentId, body: "Nội dung", status, createdAt: timestamp, updatedAt: timestamp, version: status === "Visible" ? 1 : 2, replyCount: 0, targetTitle: "Truyện kiểm duyệt", targetSlug: "truyen-kiem-duyet", parentBody: "Bình luận gốc", parentAuthorDisplayName: "Tác giả gốc" }], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 }));
      if (url.endsWith(`/api/v1/admin/comments/${commentId}/hide`) && method === "POST") { status = "Hidden"; return Promise.resolve(response({ id: commentId, userId, displayName: "Bạn đọc", avatarUrl: null, comicId, chapterId: null, parentId: null, body: "Nội dung", status, createdAt: timestamp, updatedAt: timestamp, version: 2, replyCount: 0 })); }
      throw new Error(`Unexpected request ${method} ${url}`);
    }));

    const user = userEvent.setup();
    render(<MemoryRouter><QueryClientProvider client={createQueryClient()}><AdminCommentsPage /></QueryClientProvider></MemoryRouter>);
    await screen.findByText("Nội dung");
    expect(screen.getByRole("link", { name: "Truyện kiểm duyệt" })).toHaveAttribute("href", "/comics/truyen-kiem-duyet");
    expect(screen.getByText("Phản hồi Tác giả gốc")).toBeInTheDocument();
    expect(screen.getByText("Bình luận gốc")).toBeInTheDocument();
    const hide = await screen.findByRole("button", { name: "Ẩn" });
    await waitFor(() => expect(hide).toBeEnabled());
    await user.click(hide);
    await user.type(screen.getByLabelText("Lý do hide"), "Vi phạm nội quy");
    await user.click(screen.getByRole("button", { name: "Xác nhận" }));
    await waitFor(() => expect(requests.some((request) => request.method === "POST" && request.body?.reason === "Vi phạm nội quy" && request.body?.expectedVersion === 1)).toBe(true));
    await screen.findByRole("button", { name: "Phục hồi" });
  });
});
