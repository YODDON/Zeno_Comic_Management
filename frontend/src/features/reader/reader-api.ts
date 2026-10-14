import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

const accessSchema = z.object({
  chapterId: z.string().uuid(),
  accessType: z.enum(["Free", "VipIncluded", "CoinUnlocked", "Locked", "Unavailable"]),
  canRead: z.boolean(),
  priceCoins: z.number().int().nonnegative(),
  needsLogin: z.boolean(),
});

const manifestSchema = z.object({
  chapterId: z.string().uuid(),
  pages: z.array(z.object({
    order: z.number().int().positive(),
    width: z.number().int().positive(),
    height: z.number().int().positive(),
    deliveryUrl: z.string().min(1),
  })),
  manifestIssuedAt: z.iso.datetime({ offset: true }),
  refreshHint: z.string(),
});

const sessionSchema = z.object({
  sessionId: z.string().uuid(),
  startedAt: z.iso.datetime({ offset: true }),
  expiresAt: z.iso.datetime({ offset: true }),
  policy: z.object({
    revision: z.string(),
    minimumProgressBasisPoints: z.number().int(),
    minimumActiveDurationMs: z.number().int(),
  }),
  manifestRevision: z.string(),
  manifestPageCount: z.number().int().positive(),
  version: z.number().int().positive(),
});

const heartbeatSchema = z.object({
  sessionId: z.string().uuid(),
  acceptedPosition: z.object({
    pageOrder: z.number().int().positive(),
    pageOffsetRatio: z.number().min(0).max(1),
  }).nullable(),
  acceptedMaxProgressBasisPoints: z.number().int().min(0).max(10_000),
  activeDurationMs: z.number().int().nonnegative(),
  manifestRevision: z.string(),
  manifestPageCount: z.number().int().positive(),
  lastAcceptedSequence: z.number().int().nonnegative(),
  expiresAt: z.iso.datetime({ offset: true }),
  version: z.number().int().positive(),
});

const qualificationSchema = z.object({
  qualified: z.boolean(),
  qualifiedAt: z.iso.datetime({ offset: true }).nullable(),
  reasonCode: z.string().nullable(),
  version: z.number().int().positive(),
});

export type ChapterAccess = z.infer<typeof accessSchema>;
export type ChapterManifest = z.infer<typeof manifestSchema>;
export type ReadingSession = z.infer<typeof sessionSchema>;
export type ReadingHeartbeat = z.infer<typeof heartbeatSchema>;
export type ReadingQualification = z.infer<typeof qualificationSchema>;

export async function getChapterAccess(chapterId: string): Promise<ChapterAccess> {
  return accessSchema.parse(
    await apiRequest<unknown>(`/api/v1/chapters/${chapterId}/access`),
  );
}

export async function getChapterManifest(chapterId: string): Promise<ChapterManifest> {
  return manifestSchema.parse(
    await apiRequest<unknown>(`/api/v1/chapters/${chapterId}/pages`),
  );
}

export async function createReadingSession(chapterId: string): Promise<ReadingSession> {
  return sessionSchema.parse(
    await apiRequest<unknown>("/api/v1/reading/sessions", {
      method: "POST",
      body: { chapterId },
    }),
  );
}

export async function updateReadingSession(
  sessionId: string,
  currentPageOrder: number,
  currentPageOffsetRatio: number,
  sequenceNumber: number,
): Promise<ReadingHeartbeat> {
  return heartbeatSchema.parse(
    await apiRequest<unknown>(`/api/v1/reading/sessions/${sessionId}`, {
      method: "PATCH",
      body: { currentPageOrder, currentPageOffsetRatio, sequenceNumber },
    }),
  );
}

export async function qualifyReadingSession(sessionId: string): Promise<ReadingQualification> {
  return qualificationSchema.parse(
    await apiRequest<unknown>(`/api/v1/reading/sessions/${sessionId}/qualify`, {
      method: "POST",
    }),
  );
}
