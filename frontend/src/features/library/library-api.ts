import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

const availabilitySchema = z.enum(["Available", "Unavailable"]);

const continueItemSchema = z.object({
  comicId: z.string().uuid(),
  availability: availabilitySchema,
  slug: z.string().nullable(),
  title: z.string().nullable(),
  coverAssetId: z.string().nullable(),
  seriesStatus: z.enum(["Ongoing", "Completed", "Hiatus"]).nullable(),
  chapterId: z.string().uuid().nullable(),
  chapterLabel: z.string().nullable(),
  pageOrder: z.number().int().positive().nullable(),
  pageOffsetRatio: z.number().min(0).max(1).nullable(),
  acceptedMaxProgressBasisPoints: z.number().int().min(0).max(10_000).nullable(),
  lastReadAt: z.iso.datetime({ offset: true }),
  accessType: z.enum(["Free", "VipIncluded", "CoinUnlocked", "Locked"]).nullable(),
  canRead: z.boolean(),
});

const followedItemSchema = z.object({
  comicId: z.string().uuid(),
  availability: availabilitySchema,
  slug: z.string().nullable(),
  title: z.string().nullable(),
  coverAssetId: z.string().nullable(),
  seriesStatus: z.enum(["Ongoing", "Completed", "Hiatus"]).nullable(),
  followedAt: z.iso.datetime({ offset: true }),
});

const continuePageSchema = z.object({
  items: z.array(continueItemSchema),
  nextCursor: z.string().nullable(),
});

const followedPageSchema = z.object({
  items: z.array(followedItemSchema),
  nextCursor: z.string().nullable(),
});

const followResponseSchema = z.object({
  comicId: z.string().uuid(),
  followed: z.literal(true),
  followedAt: z.iso.datetime({ offset: true }),
});

const followStatusSchema = z.object({
  comicId: z.string().uuid(),
  followed: z.boolean(),
  followedAt: z.iso.datetime({ offset: true }).nullable(),
});

export type ContinueItem = z.infer<typeof continueItemSchema>;
export type FollowedItem = z.infer<typeof followedItemSchema>;
export type ContinuePage = z.infer<typeof continuePageSchema>;
export type FollowedPage = z.infer<typeof followedPageSchema>;
export type FollowStatus = z.infer<typeof followStatusSchema>;

export async function getContinue(cursor?: string): Promise<ContinuePage> {
  const params = new URLSearchParams({ limit: "20" });
  if (cursor) params.set("cursor", cursor);
  return continuePageSchema.parse(
    await apiRequest<unknown>(`/api/v1/library/continue?${params.toString()}`),
  );
}

export async function getFollows(cursor?: string): Promise<FollowedPage> {
  const params = new URLSearchParams({ limit: "20" });
  if (cursor) params.set("cursor", cursor);
  return followedPageSchema.parse(
    await apiRequest<unknown>(`/api/v1/library/following?${params.toString()}`),
  );
}

export async function followComic(comicId: string) {
  return followResponseSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/follow`, { method: "PUT" }),
  );
}

export async function getFollowStatus(comicId: string): Promise<FollowStatus> {
  return followStatusSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/follow-status`),
  );
}

export async function unfollowComic(comicId: string): Promise<void> {
  await apiRequest<void>(`/api/v1/comics/${comicId}/follow`, { method: "DELETE" });
}
