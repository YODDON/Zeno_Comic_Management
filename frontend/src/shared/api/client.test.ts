import { apiRequest, clearApiSessionState } from "@/shared/api/client";

describe("apiRequest", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("uses same-origin credentials and forwards command headers", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: "result-id" }), {
        status: 201,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await apiRequest<{ id: string }>("/api/v1/example", {
      method: "POST",
      body: { value: 1 },
      headers: { "X-Test-Header": "test-value" },
      idempotencyKey: "request-key",
    });

    expect(fetchMock).toHaveBeenCalledOnce();
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(url).toBe("/api/v1/example");
    expect(init.credentials).toBe("same-origin");
    expect(headers.get("X-Test-Header")).toBe("test-value");
    expect(headers.get("Idempotency-Key")).toBe("request-key");
    expect(init.body).toBe(JSON.stringify({ value: 1 }));
  });

  it("maps ProblemDetails to ApiError", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            title: "Conflict",
            status: 409,
            errorCode: "PRICE_CHANGED",
            traceId: "trace-1",
          }),
          { status: 409, headers: { "Content-Type": "application/problem+json" } },
        ),
      ),
    );

    const request = apiRequest("/api/v1/example");

    await expect(request).rejects.toMatchObject({
      name: "ApiError",
      status: 409,
      errorCode: "PRICE_CHANGED",
      traceId: "trace-1",
    });
  });

  it("captures, sends, and rotates the CSRF token for unsafe requests", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ displayName: "Reader" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": "csrf-one",
          },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ displayName: "Reader Two" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": "csrf-two",
          },
        }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await apiRequest("/api/v1/me");
    await apiRequest("/api/v1/me", {
      method: "PATCH",
      body: { displayName: "Reader Two" },
    });
    await apiRequest("/api/v1/example", { method: "DELETE" });

    const secondHeaders = new Headers(
      (fetchMock.mock.calls[1] as [string, RequestInit])[1].headers,
    );
    const thirdHeaders = new Headers(
      (fetchMock.mock.calls[2] as [string, RequestInit])[1].headers,
    );
    expect(secondHeaders.get("X-CSRF-TOKEN")).toBe("csrf-one");
    expect(thirdHeaders.get("X-CSRF-TOKEN")).toBe("csrf-two");
  });

  it("clears the CSRF token after an unauthorized response", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ displayName: "Reader" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": "csrf-stale",
          },
        }),
      )
      .mockResolvedValueOnce(
        new Response(null, { status: 401 }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await apiRequest("/api/v1/me");
    await expect(apiRequest("/api/v1/private")).rejects.toMatchObject({ status: 401 });
    await apiRequest("/api/v1/example", { method: "POST" });

    const headers = new Headers(
      (fetchMock.mock.calls[2] as [string, RequestInit])[1].headers,
    );
    expect(headers.has("X-CSRF-TOKEN")).toBe(false);
  });

  it.each(["", "{not-json"])(
    "keeps the HTTP status when a JSON error body is empty or malformed",
    async (body) => {
      vi.stubGlobal(
        "fetch",
        vi.fn().mockResolvedValue(
          new Response(body, {
            status: 503,
            headers: { "Content-Type": "application/problem+json" },
          }),
        ),
      );

      await expect(apiRequest("/api/v1/example")).rejects.toMatchObject({
        name: "ApiError",
        status: 503,
      });
    },
  );

  it("rejects paths that are not rooted", async () => {
    await expect(apiRequest("api/v1/example")).rejects.toThrow("API path must start with '/'.");
  });
});
