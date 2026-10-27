import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { adminCatalogApi, type AdminChapter, type AdminPageManifest } from "@/features/admin-catalog/admin-catalog-api";
import { useUnsavedChanges } from "@/features/admin-catalog/use-unsaved-changes";
import { Button } from "@/shared/ui/Button";

type UploadItem = { id: string; file: File; state: "pending" | "uploading" | "done" | "error" };
const blank: Partial<AdminChapter> = { chapterLabel: "", sortOrder: 1, priceOverrideCoins: null, vipOverride: null };

export function AdminChaptersPage() {
  const client = useQueryClient();
  const [search, setSearch] = useState("");
  const [publicationStatus, setPublicationStatus] = useState("");
  const [page, setPage] = useState(1);
  const chapters = useQuery({
    queryKey: ["admin", "chapters", search, publicationStatus, page],
    queryFn: () => adminCatalogApi.chapters({ search, publicationStatus, page }),
  });
  const comics = useQuery({ queryKey: ["admin", "comics", "chapter-options"], queryFn: () => adminCatalogApi.comics({ pageSize: 100 }) });
  const [draft, setDraft] = useState<Partial<AdminChapter>>(blank);
  const [dirty, setDirty] = useState(false);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  useUnsavedChanges(dirty);

  const manifest = useQuery({
    queryKey: ["admin", "chapter-pages", draft.id],
    queryFn: () => adminCatalogApi.pageManifest(draft.id!),
    enabled: Boolean(draft.id),
  });
  const orderedPages = useMemo(() => [...(manifest.data?.pages ?? [])].sort((a, b) => a.pageOrder - b.pageOrder), [manifest.data]);

  function acceptManifest(value: AdminPageManifest) {
    client.setQueryData(["admin", "chapter-pages", value.chapterId], value);
    setDraft((current) => current.id === value.chapterId
      ? { ...current, version: value.version }
      : current);
  }

  function selectDraft(next: Partial<AdminChapter>) {
    if (dirty && !window.confirm("Bạn có thay đổi chưa lưu. Bỏ thay đổi và chuyển mục?")) return;
    setDraft(next);
    setDirty(false);
    setUploads([]);
  }

  const save = useMutation({
    mutationFn: () => adminCatalogApi.saveChapter(draft as AdminChapter),
    onSuccess: async (value) => {
      setDraft(value); setDirty(false);
      await client.invalidateQueries({ queryKey: ["admin", "chapters"] });
    },
  });
  const pageMutation = useMutation({
    mutationFn: async (operation: () => Promise<AdminPageManifest>) => operation(),
    onSuccess: acceptManifest,
  });
  const publication = useMutation({
    mutationFn: async (publish: boolean) => {
      if (!draft.id || !draft.version) throw new Error("Chapter must be saved first");
      return publish
        ? adminCatalogApi.publishChapter(draft.id, draft.version)
        : adminCatalogApi.hideChapter(draft.id, draft.version);
    },
    onSuccess: async (value) => {
      setDraft((current) => ({ ...current, publicationStatus: value.publicationStatus as AdminChapter["publicationStatus"], version: value.version }));
      await client.invalidateQueries({ queryKey: ["admin", "chapters"] });
    },
  });

  async function uploadOne(item: UploadItem) {
    const version = manifest.data?.version ?? draft.version;
    if (!draft.id || !version) return;
    setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "uploading" } : x));
    try {
      const value = await adminCatalogApi.appendPages(draft.id, version, [item.file]);
      acceptManifest(value);
      setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "done" } : x));
      return value.version;
    } catch {
      setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "error" } : x));
      return undefined;
    }
  }

  async function uploadSelected(files: FileList) {
    const queue = Array.from(files).map((file) => ({ id: crypto.randomUUID(), file, state: "pending" as const }));
    setUploads((current) => [...current, ...queue]);
    let version = draft.version;
    for (const item of queue) {
      if (!draft.id || !version) break;
      setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "uploading" } : x));
      try {
        const value = await adminCatalogApi.appendPages(draft.id, version, [item.file]);
        version = value.version; acceptManifest(value);
        setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "done" } : x));
      } catch {
        setUploads((items) => items.map((x) => x.id === item.id ? { ...x, state: "error" } : x));
      }
    }
  }

  function move(pageId: string, delta: number) {
    if (!draft.id || !manifest.data) return;
    const chapterId = draft.id;
    const version = manifest.data.version;
    const ids = orderedPages.map((x) => x.id);
    const index = ids.indexOf(pageId); const next = index + delta;
    if (next < 0 || next >= ids.length) return;
    [ids[index], ids[next]] = [ids[next]!, ids[index]!];
    pageMutation.mutate(() => adminCatalogApi.reorderPages(chapterId, version, ids));
  }

  return <section className="admin-workspace">
    <header><p className="eyebrow">Nội dung</p><h1>Chương</h1><p>Quản lý metadata, xuất bản và thứ tự trang ảnh.</p></header>
    <div className="admin-split">
      <nav aria-label="Danh sách chương">
        <Button onClick={() => selectDraft(blank)}>Tạo chương</Button>
        <label>Tìm chương<input maxLength={200} value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} /></label>
        <label>Trạng thái<select value={publicationStatus} onChange={(event) => { setPublicationStatus(event.target.value); setPage(1); }}><option value="">Tất cả</option><option value="Draft">Nháp</option><option value="Published">Đã xuất bản</option><option value="Hidden">Đã ẩn</option></select></label>
        {chapters.data?.items.map((chapter) => <button className="admin-list-row" key={chapter.id} onClick={() => selectDraft(chapter)}>
          {chapter.chapterLabel}<small>{chapter.publicationStatus}</small>
        </button>)}
        <div className="admin-actions"><Button disabled={page <= 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {chapters.data?.page ?? page}/{Math.max(chapters.data?.totalPages ?? 1, 1)}</span><Button disabled={!chapters.data || page >= chapters.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></div>
      </nav>
      <div>
        <form onSubmit={(event) => { event.preventDefault(); save.mutate(); }}>
          <label>Truyện<select required value={draft.comicId ?? ""} onChange={(event) => { setDraft({ ...draft, comicId: event.target.value }); setDirty(true); }}>
            <option value="">Chọn truyện</option>{comics.data?.items.map((comic) => <option key={comic.id} value={comic.id}>{comic.title}</option>)}
          </select></label>
          <label>Nhãn chương<input required maxLength={120} value={draft.chapterLabel ?? ""} onChange={(event) => { setDraft({ ...draft, chapterLabel: event.target.value }); setDirty(true); }} /></label>
          <label>Thứ tự<input type="number" value={draft.sortOrder ?? 1} onChange={(event) => { setDraft({ ...draft, sortOrder: Number(event.target.value) }); setDirty(true); }} /></label>
          <label>Giá riêng (để trống để kế thừa)<input min="0" type="number" value={draft.priceOverrideCoins ?? ""} onChange={(event) => { setDraft({ ...draft, priceOverrideCoins: event.target.value === "" ? null : Number(event.target.value) }); setDirty(true); }} /></label>
          <label>Quyền VIP<select value={draft.vipOverride == null ? "inherit" : String(draft.vipOverride)} onChange={(event) => { setDraft({ ...draft, vipOverride: event.target.value === "inherit" ? null : event.target.value === "true" }); setDirty(true); }}><option value="inherit">Kế thừa</option><option value="true">Có</option><option value="false">Không</option></select></label>
          {save.isError ? <p role="alert">Không thể lưu chương. Hãy tải lại nếu dữ liệu đã thay đổi.</p> : null}
          <Button disabled={save.isPending} type="submit">{save.isPending ? "Đang lưu…" : "Lưu chương"}</Button>
        </form>
        {draft.id ? <section aria-labelledby="chapter-pages-heading">
          <h2 id="chapter-pages-heading">Trang truyện</h2>
          {manifest.isLoading ? <p role="status">Đang tải manifest…</p> : null}
          {manifest.isError ? <p role="alert">Không tải được manifest. <button onClick={() => void manifest.refetch()}>Thử lại</button></p> : null}
          <label>Thêm ảnh<input accept="image/*" multiple type="file" onChange={(event) => { if (event.target.files) void uploadSelected(event.target.files); event.target.value = ""; }} /></label>
          <ul className="admin-upload-status" aria-live="polite">{uploads.map((item) => <li key={item.id}>{item.file.name}: {item.state === "pending" ? "đang chờ" : item.state === "uploading" ? "đang tải" : item.state === "done" ? "đã tải" : "lỗi"}{item.state === "error" ? <button onClick={() => void uploadOne(item)}>Thử lại</button> : null}</li>)}</ul>
          <ol className="admin-page-list">{orderedPages.map((page, index) => <li key={page.id}>
            <img alt={`Trang ${index + 1}`} src={adminCatalogApi.pagePreviewUrl(draft.id!, page.id)} />
            <span>Trang {index + 1} · {page.width}×{page.height}</span>
            <Button disabled={pageMutation.isPending || index === 0} onClick={() => move(page.id, -1)} type="button">Lên</Button>
            <Button disabled={pageMutation.isPending || index === orderedPages.length - 1} onClick={() => move(page.id, 1)} type="button">Xuống</Button>
            <label>Thay ảnh<input accept="image/*" disabled={pageMutation.isPending} type="file" onChange={(event) => { const file = event.target.files?.[0]; const current = manifest.data; if (file && current) pageMutation.mutate(() => adminCatalogApi.replacePage(current.chapterId, page.id, current.version, file)); }} /></label>
            <Button disabled={pageMutation.isPending} onClick={() => manifest.data && pageMutation.mutate(() => adminCatalogApi.removePage(draft.id!, page.id, manifest.data.version))} type="button">Xóa</Button>
          </li>)}</ol>
          {pageMutation.isError ? <p role="alert">Thao tác trang thất bại. Manifest có thể đã thay đổi; hãy tải lại.</p> : null}
        </section> : null}
        {draft.id ? <div className="admin-actions"><Button disabled={publication.isPending || dirty} onClick={() => publication.mutate(draft.publicationStatus !== "Published")} type="button">{draft.publicationStatus === "Published" ? "Ẩn chương" : "Xuất bản chương"}</Button>{publication.isError ? <p role="alert">Không thể đổi trạng thái xuất bản.</p> : null}</div> : null}
      </div>
    </div>
  </section>;
}
