import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CommentSection } from "@/features/comments/CommentSection";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";
const userId = "14b84f73-ff6b-4564-a2e7-8544e5af86a6";
const rootId = "b242b27f-6665-4bf0-a75d-030a8ca5b454";
const replyId = "0f5634a9-e044-4a7e-8aec-a1e39fc644c3";
const timestamp = "2026-10-23T10:00:00+00:00";

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": "csrf" } });
}

function item(overrides: Record<string, unknown> = {}) {
  return { id: rootId, userId, displayName: "Bạn đọc", avatarUrl: null, comicId, chapterId: null, parentId: null, body: "Bình luận gốc", status: "Visible", createdAt: timestamp, updatedAt: timestamp, version: 1, replyCount: 1, ...overrides };
}

describe("CommentSection", () => {
  afterEach(() => { clearApiSessionState(); vi.unstubAllGlobals(); });

  it("supports create, one-level reply, owner reply editing and pending guards", async () => {
    const calls: Array<{ url: string; method: string; body?: Record<string, unknown> }> = [];
    let releaseCreate: (() => void) | undefined;
    let firstCreate = true;
    vi.stubGlobal("fetch", vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const method = init?.method ?? "GET";
      const parsedBody = typeof init?.body === "string" ? JSON.parse(init.body) as Record<string, unknown> : undefined;
      calls.push({ url, method, body: parsedBody });
      if (url.endsWith("/api/v1/me")) return response({ id: userId, displayName: "Bạn đọc", email: null, role: "User", status: "Active", avatarUrl: null });
      if (url.includes(`/api/v1/comics/${comicId}/comments`) && method === "GET") return response({ items: [item()], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 });
      if (url.includes(`/api/v1/comments/${rootId}/replies`) && method === "GET") return response({ items: [item({ id: replyId, parentId: rootId, body: "Phản hồi", replyCount: 0 })], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 });
      if (url.endsWith("/api/v1/comments") && method === "POST") {
        if (firstCreate) {
          firstCreate = false;
          await new Promise<void>((resolve) => { releaseCreate = resolve; });
        }
        return response(item({ body: parsedBody?.body, parentId: parsedBody?.parentId ?? null }), 201);
      }
      if (url.endsWith(`/api/v1/comments/${replyId}`) && method === "PATCH") return response(item({ id: replyId, parentId: rootId, body: parsedBody?.body, version: 2, replyCount: 0 }));
      throw new Error(`Unexpected request ${method} ${url}`);
    }));

    const user = userEvent.setup();
    render(<QueryClientProvider client={createQueryClient()}><CommentSection target={{ comicId }} /></QueryClientProvider>);
    await screen.findByText("Bình luận gốc");

    await user.type(await screen.findByLabelText("Viết bình luận"), "Bình luận mới");
    await user.click(screen.getByRole("button", { name: "Gửi bình luận" }));
    expect(screen.getByRole("button", { name: "Đang gửi…" })).toBeDisabled();
    releaseCreate!();
    await waitFor(() => expect(screen.getByLabelText("Viết bình luận")).toHaveValue(""));

    await user.click(screen.getByRole("button", { name: "Trả lời" }));
    const replyBox = screen.getByLabelText("Phản hồi Bạn đọc");
    expect(replyBox).toHaveFocus();
    await user.type(replyBox, "Phản hồi mới");
    await user.click(screen.getByRole("button", { name: "Gửi phản hồi" }));
    await screen.findByText("Phản hồi");

    await user.click(screen.getByRole("button", { name: "Sửa phản hồi" }));
    const edit = screen.getByLabelText("Sửa phản hồi");
    await user.clear(edit);
    await user.type(edit, "Đã sửa");
    await user.click(screen.getByRole("button", { name: "Lưu" }));

    expect(calls.some((call) => call.method === "POST" && call.body?.parentId === rootId)).toBe(true);
    expect(calls.some((call) => call.method === "PATCH" && call.url.endsWith(replyId) && call.body?.body === "Đã sửa")).toBe(true);
  });

  it("shows accessible loading and retry states", async () => {
    let attempts = 0;
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.endsWith("/api/v1/me")) return Promise.resolve(response(null, 401));
      attempts += 1;
      return attempts <= 3
        ? Promise.resolve(response({ errorCode: "INTERNAL_ERROR", message: "Lỗi", traceId: "trace" }, 500))
        : Promise.resolve(response({ items: [], page: 1, pageSize: 20, totalItems: 0, totalPages: 0 }));
    }));
    const user = userEvent.setup();
    render(<QueryClientProvider client={createQueryClient()}><CommentSection target={{ comicId }} /></QueryClientProvider>);
    expect(screen.getByText("Đang tải bình luận")).toBeInTheDocument();
    const retry = await screen.findByRole("button", { name: /thử lại/i }, { timeout: 5_000 });
    await user.click(retry);
    await screen.findByText(/Chưa có bình luận/);
  });

  it("loads reply pages incrementally without fetching all replies eagerly", async () => {
    const requestedPages: number[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.endsWith("/api/v1/me")) return Promise.resolve(response(null, 401));
      if (url.includes(`/api/v1/comics/${comicId}/comments`))
        return Promise.resolve(response({ items: [item({ replyCount: 21 })], page: 1, pageSize: 20, totalItems: 1, totalPages: 1 }));
      if (url.includes(`/api/v1/comments/${rootId}/replies`)) {
        const page = Number(new URL(url, "https://localhost").searchParams.get("page"));
        requestedPages.push(page);
        return Promise.resolve(response({
          items: [item({ id: page === 1 ? replyId : "6c4995a5-8476-4f8f-89a2-8c8a64e13cce", parentId: rootId, body: `Phản hồi trang ${page}`, replyCount: 0 })],
          page, pageSize: 20, totalItems: 21, totalPages: 2,
        }));
      }
      throw new Error(`Unexpected request GET ${url}`);
    }));

    const user = userEvent.setup();
    render(<QueryClientProvider client={createQueryClient()}><CommentSection target={{ comicId }} /></QueryClientProvider>);
    await user.click(await screen.findByRole("button", { name: "Xem 21 phản hồi" }));
    await screen.findByText("Phản hồi trang 1");
    expect(requestedPages).toEqual([1]);
    await user.click(screen.getByRole("button", { name: "Tải thêm phản hồi" }));
    await screen.findByText("Phản hồi trang 2");
    expect(requestedPages).toEqual([1, 2]);
    expect(screen.queryByRole("button", { name: "Tải thêm phản hồi" })).not.toBeInTheDocument();
  });
});
