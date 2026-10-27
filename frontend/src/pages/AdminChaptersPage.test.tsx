import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { AdminChaptersPage } from "@/pages/AdminChaptersPage";
import { createQueryClient } from "@/shared/api/query-client";

const chapter = {
  id: "11111111-1111-1111-1111-111111111111", comicId: "22222222-2222-2222-2222-222222222222",
  chapterLabel: "Chương 1", sortOrder: 1, priceOverrideCoins: null, vipOverride: null,
  publicationStatus: "Draft", version: 3,
};

function json(value: unknown, status = 200) {
  return Promise.resolve(new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } }));
}

describe("AdminChaptersPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("appends a second batch, shows per-file failure and retries with the authoritative version", async () => {
    const versions: number[] = [];
    let uploads = 0;
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      const method = init?.method ?? "GET";
      if (url.includes("/api/v1/admin/chapters?") && method === "GET") return json({ items: [chapter], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 });
      if (url.includes("/api/v1/admin/comics?")) return json({ items: [{ id: chapter.comicId, title: "Truyện A" }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      if (url.endsWith(`/api/v1/admin/chapters/${chapter.id}/pages`) && method === "GET")
        return json({ chapterId: chapter.id, version: 3, pages: [] });
      if (url.endsWith(`/api/v1/admin/chapters/${chapter.id}/pages`) && method === "POST") {
        const form = init?.body as FormData;
        versions.push(Number(form.get("expectedVersion")));
        uploads++;
        if (uploads === 2) return json({ errorCode: "PROVIDER_UNAVAILABLE", message: "failed", traceId: "t" }, 503);
        const count = uploads === 1 ? 1 : 2;
        return json({ chapterId: chapter.id, version: count + 3, pages: Array.from({ length: count }, (_, index) => ({
          id: `${index + 1}`.padStart(8, "0") + "-0000-0000-0000-000000000000",
          pageOrder: index + 1, assetStatus: "Ready", width: 800, height: 1200,
        })) });
      }
      throw new Error(`Unexpected ${method} ${url}`);
    }));

    const router = createMemoryRouter([{ path: "/admin/chapters", element: <AdminChaptersPage /> }], { initialEntries: ["/admin/chapters"] });
    render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /Chương 1/ }));
    const fileInput = await screen.findByLabelText("Thêm ảnh");
    await user.upload(fileInput, [new File(["one"], "one.png", { type: "image/png" }), new File(["two"], "two.png", { type: "image/png" })]);

    await screen.findByText(/two.png: lỗi/);
    expect(versions).toEqual([3, 4]);
    await user.click(screen.getByRole("button", { name: "Thử lại" }));
    await waitFor(() => expect(screen.getByText(/two.png: đã tải/)).toBeInTheDocument());
    expect(versions).toEqual([3, 4, 4]);
    expect(screen.getAllByRole("img")).toHaveLength(2);
  });

  it("guards dirty selection and ignores a late manifest response from the previous chapter", async () => {
    const chapterB = { ...chapter, id: "33333333-3333-3333-3333-333333333333", chapterLabel: "Chương 2", version: 9 };
    let resolveUpload!: (response: Response) => void;
    let savedVersion: number | undefined;
    const pendingUpload = new Promise<Response>((resolve) => { resolveUpload = resolve; });
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString(); const method = init?.method ?? "GET";
      if (url.includes("/api/v1/admin/chapters?") && method === "GET") return json({ items: [chapter, chapterB], page: 1, pageSize: 30, totalItems: 2, totalPages: 1 });
      if (url.includes("/api/v1/admin/comics?")) return json({ items: [{ id: chapter.comicId, title: "Truyện A" }], page: 1, pageSize: 100, totalItems: 1, totalPages: 1 });
      if (url.endsWith(`/api/v1/admin/chapters/${chapter.id}/pages`) && method === "GET") return json({ chapterId: chapter.id, version: 3, pages: [] });
      if (url.endsWith(`/api/v1/admin/chapters/${chapterB.id}/pages`) && method === "GET") return json({ chapterId: chapterB.id, version: 9, pages: [] });
      if (url.endsWith(`/api/v1/admin/chapters/${chapter.id}/pages`) && method === "POST") return pendingUpload;
      if (url.endsWith(`/api/v1/admin/chapters/${chapterB.id}`) && method === "PATCH") {
        const body = JSON.parse(init!.body as string) as { expectedVersion: number };
        savedVersion = body.expectedVersion;
        return json({ ...chapterB, ...body, version: 10 });
      }
      throw new Error(`Unexpected ${method} ${url}`);
    }));
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValueOnce(true);
    const router = createMemoryRouter([{ path: "/admin/chapters", element: <AdminChaptersPage /> }], { initialEntries: ["/admin/chapters"] });
    render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /Chương 1/ }));
    const label = screen.getByLabelText(/Nhãn chương/);
    await user.type(label, " draft");
    await user.click(screen.getByRole("button", { name: /Chương 2/ }));
    expect(label).toHaveValue("Chương 1 draft");
    await user.click(screen.getByRole("button", { name: /Chương 2/ }));
    expect(label).toHaveValue("Chương 2");

    // Start an A request, switch to B, then resolve A. B's version must remain authoritative.
    confirm.mockReturnValue(true);
    await user.click(screen.getByRole("button", { name: /Chương 1/ }));
    await user.upload(await screen.findByLabelText("Thêm ảnh"), new File(["one"], "late.png", { type: "image/png" }));
    await user.click(screen.getByRole("button", { name: /Chương 2/ }));
    resolveUpload(new Response(JSON.stringify({ chapterId: chapter.id, version: 4, pages: [] }), { status: 200, headers: { "Content-Type": "application/json" } }));
    await waitFor(() => expect(label).toHaveValue("Chương 2"));
    await user.type(label, " updated");
    await user.click(screen.getByRole("button", { name: "Lưu chương" }));
    await waitFor(() => expect(savedVersion).toBe(9));
  });
});
