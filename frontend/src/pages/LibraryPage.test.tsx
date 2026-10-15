import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { LibraryPage } from "@/pages/LibraryPage";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";

function jsonResponse(body: unknown, status = 200) {
  if (status === 204) return new Response(null, { status });
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function renderLibrary() {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <MemoryRouter><LibraryPage /></MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("library page", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("redacts unavailable progress and supports keyboard tabs", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = requestUrl(input);
      if (url.includes("/api/v1/library/continue")) {
        return jsonResponse({
          items: [{
            comicId,
            availability: "Unavailable",
            slug: null,
            title: null,
            coverAssetId: null,
            seriesStatus: null,
            chapterId: null,
            chapterLabel: null,
            pageOrder: null,
            pageOffsetRatio: null,
            acceptedMaxProgressBasisPoints: null,
            lastReadAt: "2026-10-14T02:00:00+00:00",
            accessType: null,
            canRead: false,
          }],
          nextCursor: null,
        });
      }
      if (url.includes("/api/v1/library/following")) {
        return jsonResponse({ items: [], nextCursor: null });
      }
      throw new Error(`Unexpected request: ${url}`);
    }));

    renderLibrary();

    expect(await screen.findByRole("heading", { name: "Truyện đã được ẩn hoặc thay đổi" })).toBeVisible();
    expect(screen.queryByRole("link", { name: /Đọc tiếp/ })).not.toBeInTheDocument();

    const continueTab = screen.getByRole("tab", { name: "Đọc tiếp" });
    continueTab.focus();
    await user.keyboard("{ArrowRight}");
    const followedTab = screen.getByRole("tab", { name: "Đang theo dõi" });
    expect(followedTab).toHaveFocus();
    expect(followedTab).toHaveAttribute("aria-selected", "true");
    expect(await screen.findByRole("heading", { name: "Danh sách theo dõi đang trống" })).toBeVisible();
  });

  it("keeps a followed card until the backend confirms unfollow", async () => {
    const user = userEvent.setup();
    let resolveDelete!: (response: Response) => void;
    const deleteResponse = new Promise<Response>((resolve) => { resolveDelete = resolve; });
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = requestUrl(input);
      if (url.includes("/api/v1/library/continue")) return jsonResponse({ items: [], nextCursor: null });
      if (url.includes("/api/v1/library/following")) {
        return jsonResponse({
          items: [{
            comicId,
            availability: "Available",
            slug: "thu-vien-r03",
            title: "Thư viện R03",
            coverAssetId: null,
            seriesStatus: "Ongoing",
            followedAt: "2026-10-14T02:00:00+00:00",
          }],
          nextCursor: null,
        });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/follow`) && init?.method === "DELETE") {
        return deleteResponse;
      }
      throw new Error(`Unexpected request: ${url}`);
    }));

    renderLibrary();
    await user.click(screen.getByRole("tab", { name: "Đang theo dõi" }));
    const title = await screen.findByRole("heading", { name: "Thư viện R03" });
    await user.click(screen.getByRole("button", { name: "Bỏ theo dõi Thư viện R03" }));
    expect(title).toBeVisible();

    resolveDelete(jsonResponse(undefined, 204));
    await waitFor(() => expect(
      screen.getByRole("button", { name: "Bỏ theo dõi Thư viện R03" }),
    ).toBeEnabled());
  });
});

function requestUrl(input: string | URL | Request) {
  return input instanceof Request ? input.url : input.toString();
}
