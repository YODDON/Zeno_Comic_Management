import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { adminCatalogApi, type AdminComic, type CatalogOption } from "@/features/admin-catalog/admin-catalog-api";
import { useUnsavedChanges } from "@/features/admin-catalog/use-unsaved-changes";
import { Button } from "@/shared/ui/Button";

const blank: Partial<AdminComic> = {
  slug: "", title: "", description: "", seriesStatus: "Ongoing", coverAssetId: null,
  defaultPriceCoins: 0, defaultVipEligible: false, genreIds: [], authorIds: [], translationGroupIds: [],
};

function OptionChecks({ label, options, selected, onChange }: {
  label: string; options: CatalogOption[]; selected: string[]; onChange: (ids: string[]) => void;
}) {
  return <fieldset><legend>{label}</legend>{options.map((option) => <label key={option.id}>
    <input checked={selected.includes(option.id)} type="checkbox" onChange={(event) => onChange(event.target.checked ? [...selected, option.id] : selected.filter((id) => id !== option.id))} /> {option.name}
  </label>)}</fieldset>;
}

export function AdminComicsPage() {
  const client = useQueryClient();
  const [search, setSearch] = useState("");
  const [publicationStatus, setPublicationStatus] = useState("");
  const [page, setPage] = useState(1);
  const query = useQuery({
    queryKey: ["admin", "comics", search, publicationStatus, page],
    queryFn: () => adminCatalogApi.comics({ search, publicationStatus, page }),
  });
  const taxonomy = useQueries({ queries: ["genres", "authors", "translation-groups"].map((kind) => ({
    queryKey: ["admin", "taxonomy", kind], queryFn: () => adminCatalogApi.taxonomy(kind),
  })) });
  const [draft, setDraft] = useState<Partial<AdminComic>>(blank);
  const [dirty, setDirty] = useState(false);
  useUnsavedChanges(dirty);

  function change(next: Partial<AdminComic>) { setDraft((current) => ({ ...current, ...next })); setDirty(true); }
  function selectDraft(next: Partial<AdminComic>) {
    if (dirty && !window.confirm("Bạn có thay đổi chưa lưu. Bỏ thay đổi và chuyển mục?")) return;
    setDraft(next);
    setDirty(false);
  }
  const save = useMutation({
    mutationFn: () => adminCatalogApi.saveComic(draft as AdminComic),
    onSuccess: async (value) => { setDraft(value); setDirty(false); await client.invalidateQueries({ queryKey: ["admin", "comics"] }); },
  });
  const publication = useMutation({
    mutationFn: async (publish: boolean) => {
      if (!draft.id || !draft.version) throw new Error("Comic must be saved first");
      return publish ? adminCatalogApi.publishComic(draft.id, draft.version) : adminCatalogApi.hideComic(draft.id, draft.version);
    },
    onSuccess: async (value) => {
      setDraft((current) => ({ ...current, publicationStatus: value.publicationStatus as AdminComic["publicationStatus"], version: value.version }));
      await client.invalidateQueries({ queryKey: ["admin", "comics"] });
    },
  });

  return <section className="admin-workspace">
    <header><p className="eyebrow">Nội dung</p><h1>Truyện tranh</h1><p>Chỉnh sửa đầy đủ metadata, phân loại, giá và trạng thái xuất bản.</p></header>
    {query.isLoading ? <p role="status">Đang tải…</p> : query.isError ? <button onClick={() => void query.refetch()}>Thử lại</button> : <div className="admin-split">
      <nav aria-label="Danh sách truyện">
        <Button onClick={() => selectDraft(blank)}>Tạo truyện</Button>
        <label>Tìm truyện<input maxLength={200} value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} /></label>
        <label>Trạng thái<select value={publicationStatus} onChange={(event) => { setPublicationStatus(event.target.value); setPage(1); }}><option value="">Tất cả</option><option value="Draft">Nháp</option><option value="Published">Đã xuất bản</option><option value="Hidden">Đã ẩn</option></select></label>
        {query.data?.items.map((comic) => <button className="admin-list-row" key={comic.id} onClick={() => selectDraft(comic)}>{comic.title}<small>{comic.publicationStatus}</small></button>)}
        <div className="admin-actions"><Button disabled={page <= 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {query.data?.page ?? page}/{Math.max(query.data?.totalPages ?? 1, 1)}</span><Button disabled={!query.data || page >= query.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></div>
      </nav>
      <div>
        <form onSubmit={(event) => { event.preventDefault(); save.mutate(); }}>
          <label>Tiêu đề<input required value={draft.title ?? ""} onChange={(event) => change({ title: event.target.value })} /></label>
          <label>Slug<input required maxLength={200} value={draft.slug ?? ""} disabled={Boolean(draft.id && draft.publicationStatus !== "Draft")} onChange={(event) => change({ slug: event.target.value })} /></label>
          <label>Mô tả<textarea required value={draft.description ?? ""} onChange={(event) => change({ description: event.target.value })} /></label>
          <label>Ảnh bìa / Asset ID<input value={draft.coverAssetId ?? ""} onChange={(event) => change({ coverAssetId: event.target.value || null })} /><small>Chỉ lưu asset đã được quản lý; không công bố URL trang trả phí.</small></label>
          {draft.coverAssetId ? <div className="admin-cover-preview" aria-label={`Ảnh bìa đã chọn: ${draft.coverAssetId}`}>{draft.title?.slice(0, 1).toLocaleUpperCase("vi") || "?"}<small>{draft.coverAssetId}</small></div> : null}
          <label>Trạng thái bộ<select value={draft.seriesStatus} onChange={(event) => change({ seriesStatus: event.target.value as AdminComic["seriesStatus"] })}><option value="Ongoing">Đang tiến hành</option><option value="Completed">Hoàn thành</option><option value="Hiatus">Tạm ngưng</option></select></label>
          <label>Giá mặc định<input min="0" type="number" value={draft.defaultPriceCoins ?? 0} onChange={(event) => change({ defaultPriceCoins: Number(event.target.value) })} /></label>
          <label><input checked={draft.defaultVipEligible ?? false} type="checkbox" onChange={(event) => change({ defaultVipEligible: event.target.checked })} /> Cho phép VIP</label>
          <OptionChecks label="Thể loại" options={taxonomy[0]?.data ?? []} selected={draft.genreIds ?? []} onChange={(genreIds) => change({ genreIds })} />
          <OptionChecks label="Tác giả" options={taxonomy[1]?.data ?? []} selected={draft.authorIds ?? []} onChange={(authorIds) => change({ authorIds })} />
          <OptionChecks label="Nhóm dịch" options={taxonomy[2]?.data ?? []} selected={draft.translationGroupIds ?? []} onChange={(translationGroupIds) => change({ translationGroupIds })} />
          {save.isError ? <p role="alert">Không thể lưu. Kiểm tra taxonomy hoặc tải lại nếu phiên bản đã thay đổi.</p> : null}
          <Button disabled={save.isPending} type="submit">{save.isPending ? "Đang lưu…" : "Lưu truyện"}</Button>
        </form>
        {draft.id ? <div className="admin-actions"><Button disabled={publication.isPending || dirty} onClick={() => publication.mutate(draft.publicationStatus !== "Published")} type="button">{draft.publicationStatus === "Published" ? "Ẩn truyện" : "Xuất bản truyện"}</Button>{dirty ? <p role="status">Lưu thay đổi trước khi đổi trạng thái xuất bản.</p> : null}{publication.isError ? <p role="alert">Không thể đổi trạng thái. Truyện cần ít nhất một chương hợp lệ đã xuất bản.</p> : null}</div> : null}
      </div>
    </div>}
  </section>;
}
