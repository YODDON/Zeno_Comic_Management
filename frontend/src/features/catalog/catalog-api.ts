import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

const referenceSchema = z.object({
  id: z.string().uuid(),
  name: z.string(),
  slug: z.string().nullable(),
});

const comicListItemSchema = z.object({
  id: z.string().uuid(),
  slug: z.string(),
  title: z.string(),
  coverAssetId: z.string().nullable(),
  seriesStatus: z.enum(["Ongoing", "Completed", "Hiatus"]),
  updateSortAt: z.iso.datetime({ offset: true }),
  genres: z.array(referenceSchema),
});

const comicListPageSchema = z.object({
  items: z.array(comicListItemSchema),
  nextCursor: z.string().nullable(),
});

const comicDetailSchema = z.object({
  id: z.string().uuid(),
  slug: z.string(),
  title: z.string(),
  description: z.string(),
  coverAssetId: z.string().nullable(),
  seriesStatus: z.enum(["Ongoing", "Completed", "Hiatus"]),
  firstPublishedAt: z.iso.datetime({ offset: true }),
  updatedAt: z.iso.datetime({ offset: true }),
  genres: z.array(referenceSchema),
  authors: z.array(referenceSchema),
  translationGroups: z.array(referenceSchema),
});

const chapterSchema = z.object({
  id: z.string().uuid(),
  chapterLabel: z.string(),
  sortOrder: z.number().int(),
  publicationStatus: z.literal("Published"),
  accessSummary: z.object({
    accessType: z.enum(["Free", "VipIncluded", "CoinUnlocked", "Locked", "Unavailable"]),
    canRead: z.boolean(),
    finalPriceCoins: z.number().int().nonnegative(),
  }),
});

const chapterPageSchema = z.object({
  items: z.array(chapterSchema),
  page: z.number().int().positive(),
  pageSize: z.literal(30),
  totalItems: z.number().int().nonnegative(),
  totalPages: z.number().int().nonnegative(),
});

export type ComicListItem = z.infer<typeof comicListItemSchema>;
export type ComicDetail = z.infer<typeof comicDetailSchema>;
export type ChapterListItem = z.infer<typeof chapterSchema>;
export type ChapterPage = z.infer<typeof chapterPageSchema>;
export type ComicListPage = z.infer<typeof comicListPageSchema>;

export async function getComics(search: string, cursor?: string): Promise<ComicListPage> {
  const params = new URLSearchParams({ limit: "30" });
  if (search.trim()) params.set("search", search.trim());
  if (cursor) params.set("cursor", cursor);
  return comicListPageSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics?${params.toString()}`),
  );
}

export async function getComic(slug: string): Promise<ComicDetail> {
  return comicDetailSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${encodeURIComponent(slug)}`),
  );
}

export async function getChapters(comicId: string, page = 1): Promise<ChapterPage> {
  return chapterPageSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/chapters?page=${page}`),
  );
}
