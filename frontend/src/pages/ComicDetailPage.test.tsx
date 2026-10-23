import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { PersonalCacheIsolation } from "@/app/PersonalCacheIsolation";
import { profileQueryKey, type Profile } from "@/features/profile/profile-api";
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
    let myRating: number | null = null;
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
          rating: { averageStars: 0, ratingCount: 0, myRating: null },
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
      if (url.endsWith(`/api/v1/comics/${comicId}/rating`) && method === "GET") {
        return jsonResponse({ averageStars: myRating ?? 0, ratingCount: myRating ? 1 : 0, myRating });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/rating`) && method === "PUT") {
        expect(new Headers(init?.headers).get("X-CSRF-TOKEN")).toBe("r03-csrf");
        if (typeof init?.body !== "string") throw new Error("Expected a JSON rating body.");
        const parsed: unknown = JSON.parse(init.body);
        if (typeof parsed !== "object" || parsed === null || !("stars" in parsed) || typeof parsed.stars !== "number") {
          throw new Error("Expected a numeric star value.");
        }
        myRating = parsed.stars;
        return jsonResponse({ averageStars: myRating, ratingCount: 1, myRating });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/rating`) && method === "DELETE") {
        myRating = null;
        return jsonResponse({ averageStars: 0, ratingCount: 0, myRating });
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

    await user.click(await screen.findByRole("radio", { name: "4 sao" }));
    await waitFor(() => expect(screen.getByRole("radio", { name: "4 sao" })).toBeChecked());
    expect(screen.getByText("4.0 / 5")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Xóa đánh giá của tôi" }));
    expect(await screen.findByText("Chưa có đánh giá")).toBeInTheDocument();
  });

  it("isolates production detail initial rating when identity changes without logout or 401", async () => {
    const userA: Profile = {
      id: "a7bff851-a613-42ab-a089-82c399c7810e",
      displayName: "Reader A",
      email: "reader-a@example.com",
      avatarUrl: null,
      role: "User",
      status: "Active",
    };
    const userB: Profile = {
      ...userA,
      id: "c2528e94-a1be-4f92-9462-852ee9c3b514",
      displayName: "Reader B",
      email: "reader-b@example.com",
    };
    let currentViewer = userA;
    const ratingRequests: string[] = [];
    let resolveUserBRating: ((response: Response) => void) | undefined;
    const userBRating = new Promise<Response>((resolve) => {
      resolveUserBRating = resolve;
    });

    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.endsWith("/api/v1/comics/r03-comic")) {
        const myRating = currentViewer.id === userA.id ? 4 : 2;
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
          // This follows the real personalized detail response. The catalog
          // query must remove this personal value before caching it.
          rating: { averageStars: 3, ratingCount: 2, myRating },
        });
      }
      if (url.includes(`/api/v1/comics/${comicId}/chapters`)) {
        return jsonResponse({ items: [], page: 1, pageSize: 30, totalItems: 0, totalPages: 0 });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/follow-status`)) {
        return jsonResponse({ comicId, followed: false, followedAt: null });
      }
      if (url.endsWith(`/api/v1/comics/${comicId}/rating`)) {
        ratingRequests.push(currentViewer.id);
        if (currentViewer.id === userB.id) return userBRating;
        const myRating = currentViewer.id === userA.id ? 4 : 2;
        return jsonResponse({ averageStars: 3, ratingCount: 2, myRating });
      }
      throw new Error(`Unexpected request: GET ${url}`);
    }));

    const queryClient = createQueryClient();
    queryClient.setQueryData(profileQueryKey, userA);
    render(
      <QueryClientProvider client={queryClient}>
        <PersonalCacheIsolation />
        <MemoryRouter initialEntries={["/comics/r03-comic"]}>
          <Routes><Route element={<ComicDetailPage />} path="/comics/:slug" /></Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("radio", { name: "4 sao" })).toBeChecked());

    currentViewer = userB;
    void act(() => {
      queryClient.setQueryData(profileQueryKey, userB);
    });

    await waitFor(() => expect(screen.getByRole("radio", { name: "4 sao" })).not.toBeChecked());
    resolveUserBRating!(jsonResponse({ averageStars: 3, ratingCount: 2, myRating: 2 }));
    await waitFor(() => expect(screen.getByRole("radio", { name: "2 sao" })).toBeChecked());
    expect(ratingRequests).toContain(userA.id);
    expect(ratingRequests).toContain(userB.id);
  });
});
