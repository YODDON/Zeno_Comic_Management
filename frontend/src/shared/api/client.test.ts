import { apiRequest } from "@/shared/api/client";

describe("apiRequest", () => {
  afterEach(() => {
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

  it("rejects paths that are not rooted", async () => {
    await expect(apiRequest("api/v1/example")).rejects.toThrow("API path must start with '/'.");
  });
});
