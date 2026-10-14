import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { ReaderPage } from "@/pages/ReaderPage";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const chapterId = "2744b2b6-f009-4122-b574-1dad21741dc9";

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" },
  });
}

function renderReader(path = `/chapters/${chapterId}/read`) {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <MemoryRouter initialEntries={[path]}>
        <Routes><Route element={<ReaderPage />} path="/chapters/:chapterId/read" /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("reader page", () => {
  beforeEach(() => {
    vi.stubGlobal("scrollTo", vi.fn());
  });

  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("does not request a manifest before a locked access decision", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      chapterId,
      accessType: "Locked",
      canRead: false,
      priceCoins: 25,
      needsLogin: true,
    }));
    vi.stubGlobal("fetch", fetchMock);

    renderReader();

    expect(await screen.findByRole("heading", { name: "Bạn chưa đăng nhập" })).toBeVisible();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain(`/chapters/${chapterId}/access`);
  });

  it("renders a free manifest for a guest without creating a reading session", async () => {
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = requestUrl(input);
      if (url.endsWith(`/chapters/${chapterId}/access`)) {
        return jsonResponse({ chapterId, accessType: "Free", canRead: true, priceCoins: 0, needsLogin: false });
      }
      if (url.endsWith(`/chapters/${chapterId}/pages`)) {
        return jsonResponse({
          chapterId,
          pages: [{ order: 1, width: 800, height: 1200, deliveryUrl: `/api/v1/chapters/${chapterId}/pages/1/content` }],
          manifestIssuedAt: "2026-10-14T02:00:00+00:00",
          refreshHint: "retry-manifest",
        });
      }
      if (url.endsWith("/api/v1/me")) {
        return jsonResponse({ title: "Authentication required", status: 401, errorCode: "AUTH_REQUIRED" }, 401);
      }
      throw new Error(`Unexpected request: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    renderReader(`/chapters/${chapterId}/read?page=1&offset=0.4`);

    expect(await screen.findByRole("img", { name: "Trang 1" })).toHaveAttribute(
      "src",
      `/api/v1/chapters/${chapterId}/pages/1/content`,
    );
    expect(await screen.findByText("Khách · không lưu tiến độ")).toBeVisible();
    expect(fetchMock.mock.calls.some(([input]) => requestUrl(input).endsWith("/api/v1/reading/sessions"))).toBe(false);
  });
});

function requestUrl(input: string | URL | Request) {
  return input instanceof Request ? input.url : input.toString();
}
