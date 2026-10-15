import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  followComic,
  getContinue,
  getFollowStatus,
  getFollows,
  unfollowComic,
} from "@/features/library/library-api";

export const continueQueryKey = ["reading", "continue"] as const;
export const followsQueryKey = ["library", "follows"] as const;
export const followStatusQueryKey = (comicId: string) => ["library", "follow-status", comicId] as const;

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

export function useFollowStatusQuery(comicId: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: followStatusQueryKey(comicId ?? ""),
    queryFn: () => getFollowStatus(comicId!),
    enabled: enabled && Boolean(comicId),
  });
}

export function useFollowComic() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: followComic,
    onSuccess: (state) => {
      queryClient.setQueryData(followStatusQueryKey(state.comicId), {
        comicId: state.comicId,
        followed: true,
        followedAt: state.followedAt,
      });
      void queryClient.invalidateQueries({ queryKey: followsQueryKey });
    },
  });
}

export function useUnfollowComic() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: unfollowComic,
    onSuccess: (_, comicId) => {
      queryClient.setQueryData(followStatusQueryKey(comicId), {
        comicId,
        followed: false,
        followedAt: null,
      });
      void queryClient.invalidateQueries({ queryKey: followsQueryKey });
    },
  });
}
