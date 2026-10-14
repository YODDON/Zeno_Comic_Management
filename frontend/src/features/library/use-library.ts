import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  followComic,
  getContinue,
  getFollows,
  unfollowComic,
} from "@/features/library/library-api";

export const continueQueryKey = ["reading", "continue"] as const;
export const followsQueryKey = ["library", "follows"] as const;

export function useContinueQuery() {
  return useInfiniteQuery({
    queryKey: continueQueryKey,
    queryFn: ({ pageParam }) => getContinue(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
}

export function useFollowsQuery() {
  return useInfiniteQuery({
    queryKey: followsQueryKey,
    queryFn: ({ pageParam }) => getFollows(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
}

export function useFollowComic() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: followComic,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: followsQueryKey }),
  });
}

export function useUnfollowComic() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: unfollowComic,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: followsQueryKey }),
  });
}
