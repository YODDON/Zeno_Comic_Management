import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { AdminComicsPage } from "@/pages/AdminComicsPage";
import { createQueryClient } from "@/shared/api/query-client";

const comic = {
  id: "11111111-1111-1111-1111-111111111111", slug: "comic-a", title: "Comic A",
  description: "Mô tả", seriesStatus: "Ongoing", publicationStatus: "Draft",
  coverAssetId: null, defaultPriceCoins: 0, defaultVipEligible: false, version: 3,
  genreIds: [], authorIds: [], translationGroupIds: [],
};

describe("AdminComicsPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("edits cover and all taxonomy links, then publishes with the returned version", async () => {
    const requests: Array<{ method: string; body: unknown }> = [];
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString(); const method = init?.method ?? "GET";
      if (url.includes("/api/v1/admin/comics?") && method === "GET") return Promise.resolve(Response.json({ items: [comic], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 }));
      if (url.includes("/api/v1/admin/taxonomy/genres")) return Promise.resolve(Response.json([{ id: "g", name: "Phiêu lưu", slug: "phieu-luu" }]));
      if (url.includes("/api/v1/admin/taxonomy/authors")) return Promise.resolve(Response.json([{ id: "a", name: "Tác giả", slug: null }]));
      if (url.includes("/api/v1/admin/taxonomy/translation-groups")) return Promise.resolve(Response.json([{ id: "t", name: "Nhóm dịch", slug: null }]));
      if (url.endsWith(`/api/v1/admin/comics/${comic.id}`) && method === "PATCH") {
        const body: unknown = JSON.parse(init!.body as string); requests.push({ method, body });
        if (typeof body !== "object" || body === null) throw new Error("Invalid body");
        return Promise.resolve(Response.json({ ...comic, ...body, version: 4 }));
      }
      if (url.endsWith(`/api/v1/admin/comics/${comic.id}/publish`) && method === "POST") {
        const body: unknown = JSON.parse(init!.body as string); requests.push({ method, body });
        return Promise.resolve(Response.json({ id: comic.id, publicationStatus: "Published", version: 5, firstPublishedAt: "2026-10-26T00:00:00Z" }));
      }
      throw new Error(`Unexpected ${method} ${url}`);
    }));

    const router = createMemoryRouter([{ path: "/admin/comics", element: <AdminComicsPage /> }], { initialEntries: ["/admin/comics"] });
    render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /Comic A/ }));
    await user.type(screen.getByLabelText(/Ảnh bìa \/ Asset ID/), "cover-asset");
    await user.click(screen.getByLabelText(/Phiêu lưu/));
    await user.click(screen.getByLabelText(/Tác giả/));
    await user.click(screen.getByLabelText(/Nhóm dịch/));
    const blockedNavigation = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(blockedNavigation);
    expect(blockedNavigation.defaultPrevented).toBe(true);
    await user.click(screen.getByRole("button", { name: "Lưu truyện" }));

    await waitFor(() => expect(requests).toHaveLength(1));
    const allowedNavigation = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(allowedNavigation);
    expect(allowedNavigation.defaultPrevented).toBe(false);
    expect(requests[0]?.body).toMatchObject({ coverAssetId: "cover-asset", genreIds: ["g"], authorIds: ["a"], translationGroupIds: ["t"], expectedVersion: 3 });
    await user.click(screen.getByRole("button", { name: "Xuất bản truyện" }));
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[1]).toEqual({ method: "POST", body: { expectedVersion: 4 } });
    expect(await screen.findByRole("button", { name: "Ẩn truyện" })).toBeVisible();
  });

  it("guards same-route selection and create when the current draft is dirty", async () => {
    const comicB = { ...comic, id: "22222222-2222-2222-2222-222222222222", slug: "comic-b", title: "Comic B" };
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.includes("/api/v1/admin/comics?")) return Promise.resolve(Response.json({ items: [comic, comicB], page: 1, pageSize: 30, totalItems: 2, totalPages: 1 }));
      if (url.includes("/api/v1/admin/taxonomy/")) return Promise.resolve(Response.json([]));
      throw new Error(`Unexpected GET ${url}`);
    }));
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValueOnce(true).mockReturnValueOnce(false);
    const router = createMemoryRouter([{ path: "/admin/comics", element: <AdminComicsPage /> }], { initialEntries: ["/admin/comics"] });
    render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /Comic A/ }));
    const title = screen.getByLabelText(/Tiêu đề/);
    await user.clear(title); await user.type(title, "Draft A");

    await user.click(screen.getByRole("button", { name: /Comic B/ }));
    expect(title).toHaveValue("Draft A");
    await user.click(screen.getByRole("button", { name: /Comic B/ }));
    expect(title).toHaveValue("Comic B");
    await user.type(title, " changed");
    await user.click(screen.getByRole("button", { name: /Tạo truyện/ }));
    expect(title).toHaveValue("Comic B changed");
    expect(confirm).toHaveBeenCalledTimes(3);
  });
});
