import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  deleteComicRating,
  getComicRating,
  setComicRating,
} from "@/features/ratings/ratings-api";

export const comicRatingQueryKey = (comicId: string, viewerKey: string) =>
  ["interaction", "rating", comicId, viewerKey] as const;

export function useComicRatingQuery(
  comicId: string | undefined,
  viewerKey: string,
  initialData?: Awaited<ReturnType<typeof getComicRating>>,
) {
  return useQuery({
    queryKey: comicRatingQueryKey(comicId ?? "", viewerKey),
    queryFn: () => getComicRating(comicId!),
    enabled: Boolean(comicId),
    // Public aggregate data may paint immediately, but it must not make a
    // viewer-owned query fresh or seed myRating from another identity.
    placeholderData: initialData
      ? { ...initialData, myRating: null }
      : undefined,
  });
}

export function useSetComicRating(comicId: string, viewerKey: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (stars: number) => setComicRating(comicId, stars),
    onSuccess: (rating) => queryClient.setQueryData(comicRatingQueryKey(comicId, viewerKey), rating),
  });
}

export function useDeleteComicRating(comicId: string, viewerKey: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => deleteComicRating(comicId),
    onSuccess: (rating) => queryClient.setQueryData(comicRatingQueryKey(comicId, viewerKey), rating),
  });
}
