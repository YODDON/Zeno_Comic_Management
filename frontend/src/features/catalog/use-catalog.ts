import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { getChapters, getComic, getComics } from "@/features/catalog/catalog-api";

export function useCatalogQuery(search: string) {
  return useInfiniteQuery({
    queryKey: ["catalog", "comics", search.trim()] as const,
    queryFn: ({ pageParam }) => getComics(search, pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
}

export function useComicQuery(slug: string) {
  return useQuery({
    queryKey: ["catalog", "comic", slug] as const,
    queryFn: () => getComic(slug),
    enabled: Boolean(slug),
  });
}

export function useChaptersQuery(comicId?: string, page = 1) {
  return useQuery({
    queryKey: ["catalog", "chapters", comicId, page] as const,
    queryFn: () => getChapters(comicId!, page),
    enabled: Boolean(comicId),
  });
}
