import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

const comicRatingSchema = z.object({
  averageStars: z.number().min(0).max(5),
  ratingCount: z.number().int().nonnegative(),
  myRating: z.number().int().min(1).max(5).nullable(),
});

export type ComicRating = z.infer<typeof comicRatingSchema>;

export async function getComicRating(comicId: string): Promise<ComicRating> {
  return comicRatingSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/rating`),
  );
}

export async function setComicRating(comicId: string, stars: number): Promise<ComicRating> {
  return comicRatingSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/rating`, {
      method: "PUT",
      body: { stars },
    }),
  );
}

export async function deleteComicRating(comicId: string): Promise<ComicRating> {
  return comicRatingSchema.parse(
    await apiRequest<unknown>(`/api/v1/comics/${comicId}/rating`, { method: "DELETE" }),
  );
}
