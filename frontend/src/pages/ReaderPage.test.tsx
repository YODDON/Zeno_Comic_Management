import { QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { Mock } from "vitest";
import { ReaderPage } from "@/pages/ReaderPage";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const chapterId = "2744b2b6-f009-4122-b574-1dad21741dc9";

function jsonResponse(body: unknown, status = 200, csrfToken?: string) {
  const headers = new Headers({
    "Content-Type": status >= 400 ? "application/problem+json" : "application/json",
  });
  if (csrfToken) headers.set("X-CSRF-TOKEN", csrfToken);
  return new Response(JSON.stringify(body), {
    status,
    headers,
  });
}

function parseJsonBody(init?: RequestInit): unknown {
  if (typeof init?.body !== "string") {
    throw new Error("Expected a JSON request body.");
  }
  return JSON.parse(init.body) as unknown;
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
  let layoutScrollY = 0;
  let scrollToMock: Mock<(options: ScrollToOptions | number, y?: number) => void>;

  beforeEach(() => {
    layoutScrollY = 0;
    vi.spyOn(window, "scrollY", "get").mockImplementation(() => layoutScrollY);
    vi.spyOn(HTMLElement.prototype, "offsetTop", "get").mockImplementation(function (this: HTMLElement) {
      const frames = Array.from(document.querySelectorAll<HTMLElement>(".reader-page-frame"));
      const index = frames.indexOf(this);
      return index < 0 ? 0 : 200 + index * 1_000;
    });
    vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) {
      return this.matches(".reader-page-frame") ? 1_000 : 0;
    });
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const frames = Array.from(document.querySelectorAll<HTMLElement>(".reader-page-frame"));
      const index = frames.indexOf(this);
      const top = index < 0 ? 0 : 200 + index * 1_000 - layoutScrollY;
      const height = index < 0 ? 0 : 1_000;
      return {
        x: 0,
        y: top,
        top,
        right: 800,
        bottom: top + height,
        left: 0,
        width: 800,
        height,
        toJSON: () => ({}),
      };
    });
    scrollToMock = vi.fn((options: ScrollToOptions | number, y?: number) => {
      layoutScrollY = typeof options === "number" ? y ?? 0 : options.top ?? 0;
    });
    vi.stubGlobal("scrollTo", scrollToMock);
  });

  afterEach(() => {
    clearApiSessionState();
    vi.useRealTimers();
    vi.restoreAllMocks();
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

  it("falls back to the first Ready page with zero offset when a saved page disappeared", async () => {
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = requestUrl(input);
      if (url.endsWith(`/chapters/${chapterId}/access`)) {
        return jsonResponse({ chapterId, accessType: "Free", canRead: true, priceCoins: 0, needsLogin: false });
      }
      if (url.endsWith(`/chapters/${chapterId}/pages`)) {
        return jsonResponse({
          chapterId,
          pages: [{ order: 3, width: 800, height: 1200, deliveryUrl: `/api/v1/chapters/${chapterId}/pages/3/content` }],
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

    renderReader(`/chapters/${chapterId}/read?page=2&offset=0.8`);

    expect(await screen.findByText("Trang 3 · 0%")).toBeVisible();
  });

  it("applies an authoritative remap and sends it in the next heartbeat", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let manifestRequests = 0;
    const heartbeatPayloads: Array<{
      currentPageOrder: number;
      currentPageOffsetRatio: number;
      sequenceNumber: number;
    }> = [];
    const fetchMock = vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = requestUrl(input);
      const method = init?.method ?? "GET";
      if (url.endsWith(`/chapters/${chapterId}/access`)) {
        return jsonResponse({ chapterId, accessType: "Free", canRead: true, priceCoins: 0, needsLogin: false });
      }
      if (url.endsWith(`/chapters/${chapterId}/pages`)) {
        manifestRequests += 1;
        const pageOrders = manifestRequests === 1 ? [1, 2] : [2];
        return jsonResponse({
          chapterId,
          pages: pageOrders.map((order) => ({
            order,
            width: 800,
            height: 1200,
            deliveryUrl: `/api/v1/chapters/${chapterId}/pages/${order}/content`,
          })),
          manifestIssuedAt: "2026-10-14T02:00:00+00:00",
          refreshHint: "retry-manifest",
        });
      }
      if (url.endsWith("/api/v1/me")) return authenticatedProfileResponse();
      if (url.endsWith("/api/v1/reading/sessions") && method === "POST") {
        return readingSessionResponse("manifest-old");
      }
      if (url.includes("/api/v1/reading/sessions/") && method === "PATCH") {
        const payload = parseJsonBody(init) as typeof heartbeatPayloads[number];
        heartbeatPayloads.push(payload);
        return jsonResponse({
          sessionId: "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
          acceptedPosition: { pageOrder: 2, pageOffsetRatio: 0.4 },
          acceptedMaxProgressBasisPoints: 7_000,
          activeDurationMs: heartbeatPayloads.length === 1 ? 0 : 10_000,
          manifestRevision: "manifest-new",
          manifestPageCount: 1,
          lastAcceptedSequence: payload.sequenceNumber,
          expiresAt: "2026-10-14T02:30:00+00:00",
          version: payload.sequenceNumber + 1,
        });
      }
      throw new Error(`Unexpected request: ${method} ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    renderReader();
    expect(await screen.findByText(/Đã đồng bộ/)).toBeVisible();
    scrollToMock.mockClear();

    await act(async () => { await vi.advanceTimersByTimeAsync(heartbeatIntervalMsForTest()); });
    await waitFor(() => expect(heartbeatPayloads).toHaveLength(1));
    expect(await screen.findByText("Trang 2 · 70%")).toBeVisible();
    await waitFor(() => expect(manifestRequests).toBe(2));
    expect(scrollToMock.mock.calls.length).toBeGreaterThan(0);

    await act(async () => { await vi.advanceTimersByTimeAsync(heartbeatIntervalMsForTest()); });
    await waitFor(() => expect(heartbeatPayloads).toHaveLength(2));
    expect(heartbeatPayloads[1]).toMatchObject({
      currentPageOrder: 2,
      currentPageOffsetRatio: 0.4,
      sequenceNumber: 2,
    });
  });

  it("recovers when session start fails and starts heartbeats after retry", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let startAttempts = 0;
    let heartbeatCount = 0;
    vi.stubGlobal("fetch", vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = requestUrl(input);
      const method = init?.method ?? "GET";
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
      if (url.endsWith("/api/v1/me")) return authenticatedProfileResponse();
      if (url.endsWith("/api/v1/reading/sessions") && method === "POST") {
        startAttempts += 1;
        return startAttempts === 1
          ? jsonResponse({ title: "Unavailable", status: 503, errorCode: "PROVIDER_UNAVAILABLE" }, 503)
          : readingSessionResponse("manifest-one");
      }
      if (url.includes("/api/v1/reading/sessions/") && method === "PATCH") {
        heartbeatCount += 1;
        const payload = parseJsonBody(init) as { sequenceNumber: number };
        return jsonResponse({
          sessionId: "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
          acceptedPosition: { pageOrder: 1, pageOffsetRatio: 0.1 },
          acceptedMaxProgressBasisPoints: 1_000,
          activeDurationMs: 0,
          manifestRevision: "manifest-one",
          manifestPageCount: 1,
          lastAcceptedSequence: payload.sequenceNumber,
          expiresAt: "2026-10-14T02:30:00+00:00",
          version: 2,
        });
      }
      throw new Error(`Unexpected request: ${method} ${url}`);
    }));

    renderReader();
    expect(await screen.findByText("Tiến độ hiện không được lưu")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Thử lưu tiến độ lại" }));
    expect(await screen.findByText(/Đã đồng bộ/)).toBeVisible();
    expect(startAttempts).toBe(2);

    await act(async () => { await vi.advanceTimersByTimeAsync(heartbeatIntervalMsForTest()); });
    await waitFor(() => expect(heartbeatCount).toBe(1));
  });
});

function requestUrl(input: string | URL | Request) {
  return input instanceof Request ? input.url : input.toString();
}

function authenticatedProfileResponse() {
  return jsonResponse({
    id: "a7bff851-a613-42ab-a089-82c399c7810e",
    displayName: "Bạn đọc R03",
    email: "reader@example.com",
    avatarUrl: null,
    role: "User",
    status: "Active",
  }, 200, "r03-csrf");
}

function readingSessionResponse(manifestRevision: string) {
  return jsonResponse({
    sessionId: "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
    startedAt: "2026-10-14T02:00:00+00:00",
    expiresAt: "2026-10-14T02:30:00+00:00",
    policy: { revision: "r02-v1", minimumProgressBasisPoints: 9000, minimumActiveDurationMs: 15000 },
    manifestRevision,
    manifestPageCount: 2,
    version: 1,
  }, 201);
}

function heartbeatIntervalMsForTest() {
  return 10_001;
}
