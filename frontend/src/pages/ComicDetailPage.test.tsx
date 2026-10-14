import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { ComicDetailPage } from "@/pages/ComicDetailPage";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";

function jsonResponse(body: unknown, status = 200, csrfToken?: string) {
  const headers = new Headers({ "Content-Type": "application/json" });
  if (csrfToken) headers.set("X-CSRF-TOKEN", csrfToken);
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers });
}

describe("comic detail follow controls", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("loads follow status and toggles through the idempotent PUT and DELETE contracts", async () => {
    const user = userEvent.setup();
    let followed = false;
    const mutationMethods: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const method = init?.method ?? "GET";
      if (url.endsWith("/api/v1/comics/r03-comic")) {
        return jsonResponse({
          id: comicId,
          slug: "r03-comic",
          title: "Hành trình R03",
          description: "Truyện kiểm thử",
          coverAssetId: null,
          seriesStatus: "Ongoing",
          firstPublishedAt: "2026-10-14T01:00:00+00:00",
          updatedAt: "2026-10-14T02:00:00+00:00",
          genres: [],
          authors: [],
          translationGroups: [],
        });
      }
      if (url.includes(`/api/v1/comics/${comicId}/chapters`)) {
        return jsonResponse({ items: [], page: 1, pageSize: 30, totalItems: 0, totalPages: 0 });
      }
      if (url.endsWith("/api/v1/me")) {
        return jsonResponse({
          id: "a7bff851-a613-42ab-a089-82c399c7810e",
          displayName: "Bạn đọc",
          email: "reader@example.com",
          avatarUrl: null,
          role: "User",
          status: "Active",
        }, 200, "r03-csrf");
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/follow-status`)) {
        return jsonResponse({ comicId, followed, followedAt: followed ? "2026-10-14T02:00:00+00:00" : null });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/follow`) && method === "PUT") {
        mutationMethods.push(method);
        expect(new Headers(init?.headers).get("X-CSRF-TOKEN")).toBe("r03-csrf");
        followed = true;
        return jsonResponse({ comicId, followed: true, followedAt: "2026-10-14T02:00:00+00:00" });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/follow`) && method === "DELETE") {
        mutationMethods.push(method);
        followed = false;
        return jsonResponse(undefined, 204);
      }
      throw new Error(`Unexpected request: ${method} ${url}`);
    }));

    render(
      <QueryClientProvider client={createQueryClient()}>
        <MemoryRouter initialEntries={["/comics/r03-comic"]}>
          <Routes><Route element={<ComicDetailPage />} path="/comics/:slug" /></Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const followButton = await screen.findByRole("button", { name: "Theo dõi truyện" });
    await user.click(followButton);
    expect(await screen.findByRole("button", { name: "Bỏ theo dõi" })).toHaveAttribute("aria-pressed", "true");
    await user.click(screen.getByRole("button", { name: "Bỏ theo dõi" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Theo dõi truyện" })).toHaveAttribute("aria-pressed", "false"));
    expect(mutationMethods).toEqual(["PUT", "DELETE"]);
  });
});
