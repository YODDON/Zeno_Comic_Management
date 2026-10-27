import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { adminCatalogApi, type CatalogOption } from "@/features/admin-catalog/admin-catalog-api";
import { Button } from "@/shared/ui/Button";

export function AdminTaxonomyPage({ kind, title }: { kind: string; title: string }) {
  const client = useQueryClient();
  const [draft, setDraft] = useState<CatalogOption>({ id: "", name: "", slug: null });
  const query = useQuery({ queryKey: ["admin", "taxonomy", kind], queryFn: () => adminCatalogApi.taxonomy(kind) });
  const save = useMutation({
    mutationFn: () => draft.id
      ? adminCatalogApi.updateTaxonomy(kind, draft)
      : adminCatalogApi.createTaxonomy(kind, draft.name, kind === "genres" ? draft.slug ?? undefined : undefined),
    onSuccess: async () => { setDraft({ id: "", name: "", slug: null }); await client.invalidateQueries({ queryKey: ["admin", "taxonomy", kind] }); },
  });
  const remove = useMutation({
    mutationFn: (id: string) => adminCatalogApi.deleteTaxonomy(kind, id),
    onSuccess: async () => { setDraft({ id: "", name: "", slug: null }); await client.invalidateQueries({ queryKey: ["admin", "taxonomy", kind] }); },
  });

  return <section className="admin-workspace"><header><h1>{title}</h1><p>Tạo, đổi tên hoặc xóa taxonomy dùng trong Comic Editor.</p></header>
    <div className="admin-split"><nav aria-label={`Danh sách ${title}`}><Button onClick={() => setDraft({ id: "", name: "", slug: null })}>Thêm mới</Button>{query.data?.map((item) => <button className="admin-list-row" key={item.id} onClick={() => setDraft(item)}>{item.name}<small>{item.slug}</small></button>)}</nav>
      <form onSubmit={(event) => { event.preventDefault(); save.mutate(); }}>
        <label>Tên<input maxLength={200} required value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></label>
        {kind === "genres" ? <label>Slug<input maxLength={120} required value={draft.slug ?? ""} onChange={(event) => setDraft({ ...draft, slug: event.target.value })} /></label> : null}
        {(save.isError || remove.isError) ? <p role="alert">Không thể lưu taxonomy. Mục có thể đang được sử dụng hoặc dữ liệu đã trùng.</p> : null}
        <div className="admin-actions"><Button disabled={save.isPending || remove.isPending} type="submit">{draft.id ? "Cập nhật" : "Thêm"}</Button>{draft.id ? <Button disabled={save.isPending || remove.isPending} onClick={() => { if (window.confirm("Xóa mục này?")) remove.mutate(draft.id); }} type="button" variant="outline">Xóa</Button> : null}</div>
      </form>
    </div>
  </section>;
}
