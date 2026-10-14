import { ZodError } from "zod";
import {
  getChapterManifest,
  updateReadingSession,
} from "@/features/reader/reader-api";
import { clearApiSessionState } from "@/shared/api/client";

function jsonResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

describe("reader API contracts", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("rejects a manifest that could collapse the reserved page aspect ratio", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      chapterId: "2744b2b6-f009-4122-b574-1dad21741dc9",
      pages: [{ order: 1, width: 0, height: 1600, deliveryUrl: "/page" }],
      manifestIssuedAt: "2026-10-14T02:00:00+00:00",
      refreshHint: "retry-manifest",
    })));

    await expect(getChapterManifest("2744b2b6-f009-4122-b574-1dad21741dc9"))
      .rejects.toBeInstanceOf(ZodError);
  });

  it("sends only the client-owned heartbeat fields", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      sessionId: "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
      acceptedPosition: { pageOrder: 3, pageOffsetRatio: 0.25 },
      acceptedMaxProgressBasisPoints: 6250,
      activeDurationMs: 15000,
      manifestRevision: "revision-two",
      manifestPageCount: 4,
      lastAcceptedSequence: 2,
      expiresAt: "2026-10-14T02:30:00+00:00",
      version: 3,
    }));
    vi.stubGlobal("fetch", fetchMock);

    await updateReadingSession(
      "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
      3,
      0.25,
      2,
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({
      currentPageOrder: 3,
      currentPageOffsetRatio: 0.25,
      sequenceNumber: 2,
    });
  });

  it("rejects malformed authoritative progress responses", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      sessionId: "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
      acceptedPosition: { pageOrder: 3, pageOffsetRatio: 0.25 },
      acceptedMaxProgressBasisPoints: 10001,
      activeDurationMs: -1,
      manifestRevision: "revision-two",
      manifestPageCount: 4,
      lastAcceptedSequence: 2,
      expiresAt: "2026-10-14T02:30:00+00:00",
      version: 3,
    })));

    await expect(updateReadingSession(
      "70acb9d4-d02c-4a74-b607-9309eb5bdc31",
      3,
      0.25,
      2,
    )).rejects.toBeInstanceOf(ZodError);
  });
});
