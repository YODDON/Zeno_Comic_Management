import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminTaxonomyPage } from "@/pages/AdminTaxonomyPage";
import { createQueryClient } from "@/shared/api/query-client";

describe("AdminTaxonomyPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("updates and deletes an existing taxonomy item", async () => {
    const calls: string[] = [];
    vi.stubGlobal("confirm", vi.fn(() => true));
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString(); const method = init?.method ?? "GET";
      if (method === "GET") return Promise.resolve(Response.json([{ id: "g", name: "Cũ", slug: "cu" }]));
      calls.push(method);
      if (method === "PATCH") return Promise.resolve(Response.json({ id: "g", name: "Mới", slug: "moi" }));
      if (method === "DELETE") return Promise.resolve(Response.json({ id: "g", name: "Mới", slug: "moi" }));
      throw new Error(`Unexpected ${url}`);
    }));
    const user = userEvent.setup();
    render(<QueryClientProvider client={createQueryClient()}><AdminTaxonomyPage kind="genres" title="Thể loại" /></QueryClientProvider>);
    await user.click(await screen.findByRole("button", { name: /Cũ/ }));
    await user.clear(screen.getByLabelText("Tên")); await user.type(screen.getByLabelText("Tên"), "Mới");
    await user.clear(screen.getByLabelText("Slug")); await user.type(screen.getByLabelText("Slug"), "moi");
    await user.click(screen.getByRole("button", { name: "Cập nhật" }));
    await waitFor(() => expect(calls).toContain("PATCH"));

    await user.click(await screen.findByRole("button", { name: /Cũ/ }));
    await user.click(screen.getByRole("button", { name: "Xóa" }));
    await waitFor(() => expect(calls).toContain("DELETE"));
  });
});
