import { apiRequest } from "@/shared/api/client";

export type AdminComic = {
  id: string; slug: string; title: string; description: string;
  seriesStatus: "Ongoing" | "Completed" | "Hiatus";
  publicationStatus: "Draft" | "Published" | "Hidden";
  coverAssetId: string | null; defaultPriceCoins: number; defaultVipEligible: boolean;
  version: number; genreIds: string[]; authorIds: string[]; translationGroupIds: string[];
};
export type AdminChapter = { id: string; comicId: string; chapterLabel: string; sortOrder: number;
  priceOverrideCoins: number | null; vipOverride: boolean | null;
  publicationStatus: "Draft" | "Published" | "Hidden"; version: number };
export type CatalogOption = { id: string; name: string; slug: string | null };
export type FeaturedState = { version: number; comicIds: string[] };
export type AdminPage = { id: string; pageOrder: number; assetStatus: string; width: number; height: number };
export type AdminPageManifest = { chapterId: string; version: number; pages: AdminPage[] };
export type AdminCatalogPage<T> = {
  items: T[]; page: number; pageSize: number; totalItems: number; totalPages: number;
};
export type AdminListCriteria = { search?: string; publicationStatus?: string; page?: number; pageSize?: number };
type PublicationState = { id: string; publicationStatus: string; version: number };

function listQuery(criteria: AdminListCriteria = {}) {
  const query = new URLSearchParams({
    page: String(criteria.page ?? 1),
    pageSize: String(criteria.pageSize ?? 30),
  });
  if (criteria.search?.trim()) query.set("search", criteria.search.trim());
  if (criteria.publicationStatus) query.set("publicationStatus", criteria.publicationStatus);
  return query.toString();
}

function pageForm(expectedVersion: number, files: File[]) {
  const body = new FormData();
  body.set("expectedVersion", String(expectedVersion));
  files.forEach((file) => body.append("files", file));
  return body;
}

export const adminCatalogApi = {
  comics: (criteria: AdminListCriteria = {}) =>
    apiRequest<AdminCatalogPage<AdminComic>>(`/api/v1/admin/comics?${listQuery(criteria)}`),
  saveComic: (comic: Partial<AdminComic> & Pick<AdminComic, "slug" | "title" | "description" | "seriesStatus">) => {
    const { id, version } = comic;
    return apiRequest<AdminComic>(id ? `/api/v1/admin/comics/${id}` : "/api/v1/admin/comics", {
      method: id ? "PATCH" : "POST",
      body: {
        slug: comic.slug, title: comic.title, description: comic.description,
        seriesStatus: comic.seriesStatus, coverAssetId: comic.coverAssetId ?? null,
        defaultPriceCoins: comic.defaultPriceCoins ?? 0,
        defaultVipEligible: comic.defaultVipEligible ?? false,
        genreIds: comic.genreIds ?? [], authorIds: comic.authorIds ?? [],
        translationGroupIds: comic.translationGroupIds ?? [],
        expectedVersion: id ? version : null,
      },
    });
  },
  chapters: (criteria: AdminListCriteria & { comicId?: string } = {}) => {
    const query = new URLSearchParams(listQuery(criteria));
    if (criteria.comicId) query.set("comicId", criteria.comicId);
    return apiRequest<AdminCatalogPage<AdminChapter>>(`/api/v1/admin/chapters?${query}`);
  },
  saveChapter: (chapter: Partial<AdminChapter> & Pick<AdminChapter, "comicId" | "chapterLabel" | "sortOrder">) => {
    const { id, version } = chapter;
    return apiRequest<AdminChapter>(id ? `/api/v1/admin/chapters/${id}` : "/api/v1/admin/chapters", {
      method: id ? "PATCH" : "POST",
      body: {
        comicId: chapter.comicId, chapterLabel: chapter.chapterLabel, sortOrder: chapter.sortOrder,
        priceOverrideCoins: chapter.priceOverrideCoins ?? null, vipOverride: chapter.vipOverride ?? null,
        expectedVersion: id ? version : null,
      },
    });
  },
  pageManifest: (chapterId: string) => apiRequest<AdminPageManifest>(`/api/v1/admin/chapters/${chapterId}/pages`),
  pagePreviewUrl: (chapterId: string, pageId: string) => `/api/v1/admin/chapters/${chapterId}/pages/${pageId}/content`,
  appendPages: (chapterId: string, expectedVersion: number, files: File[]) =>
    apiRequest<AdminPageManifest>(`/api/v1/admin/chapters/${chapterId}/pages`, {
      method: "POST", body: pageForm(expectedVersion, files),
    }),
  reorderPages: (chapterId: string, expectedVersion: number, pageIds: string[]) =>
    apiRequest<AdminPageManifest>(`/api/v1/admin/chapters/${chapterId}/pages/order`, {
      method: "PUT", body: { expectedVersion, pageIds },
    }),
  replacePage: (chapterId: string, pageId: string, expectedVersion: number, file: File) => {
    const body = new FormData(); body.set("expectedVersion", String(expectedVersion)); body.set("file", file);
    return apiRequest<AdminPageManifest>(`/api/v1/admin/chapters/${chapterId}/pages/${pageId}/content`, { method: "PUT", body });
  },
  removePage: (chapterId: string, pageId: string, expectedVersion: number) =>
    apiRequest<AdminPageManifest>(`/api/v1/admin/chapters/${chapterId}/pages/${pageId}?expectedVersion=${expectedVersion}`, { method: "DELETE" }),
  publishComic: (id: string, version: number) =>
    apiRequest<PublicationState>(`/api/v1/admin/comics/${id}/publish`, { method: "POST", body: { expectedVersion: version } }),
  hideComic: (id: string, version: number) =>
    apiRequest<PublicationState>(`/api/v1/admin/comics/${id}/hide`, { method: "POST", body: { expectedVersion: version } }),
  publishChapter: (id: string, version: number) =>
    apiRequest<PublicationState>(`/api/v1/admin/chapters/${id}/publish`, { method: "POST", body: { expectedVersion: version } }),
  hideChapter: (id: string, version: number) =>
    apiRequest<PublicationState>(`/api/v1/admin/chapters/${id}/hide`, { method: "POST", body: { expectedVersion: version } }),
  taxonomy: (kind: string) => apiRequest<CatalogOption[]>(`/api/v1/admin/taxonomy/${kind}`),
  createTaxonomy: (kind: string, name: string, slug?: string) =>
    apiRequest<CatalogOption>(`/api/v1/admin/taxonomy/${kind}`, { method: "POST", body: { name, slug } }),
  updateTaxonomy: (kind: string, item: CatalogOption) =>
    apiRequest<CatalogOption>(`/api/v1/admin/taxonomy/${kind}/${item.id}`, { method: "PATCH", body: { name: item.name, slug: item.slug } }),
  deleteTaxonomy: (kind: string, id: string) =>
    apiRequest<CatalogOption>(`/api/v1/admin/taxonomy/${kind}/${id}`, { method: "DELETE" }),
  featured: () => apiRequest<FeaturedState>("/api/v1/admin/featured"),
  saveFeatured: (state: FeaturedState) => apiRequest<FeaturedState>("/api/v1/admin/featured", {
    method: "PUT", body: { comicIds: state.comicIds, expectedVersion: state.version },
  }),
};
