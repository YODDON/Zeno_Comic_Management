import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { adminCatalogApi, type AdminComic, type FeaturedState } from "@/features/admin-catalog/admin-catalog-api";
import { Button } from "@/shared/ui/Button";

export function AdminFeaturedPage() {
  const comics = useQuery({ queryKey: ["admin", "comics", "featured-options"], queryFn: () => adminCatalogApi.comics({ pageSize: 100 }) });
  const state = useQuery({ queryKey: ["admin", "featured"], queryFn: adminCatalogApi.featured });
  if (state.isLoading || comics.isLoading) return <p role="status">Đang tải…</p>;
  if (state.isError || comics.isError || !state.data || !comics.data) return <p role="alert">Không thể tải danh sách nổi bật.</p>;
  return <FeaturedEditor key={state.data.version} initial={state.data} comics={comics.data.items} />;
}

function FeaturedEditor({ initial, comics }: { initial: FeaturedState; comics: AdminComic[] }) {
  const client = useQueryClient();
  const [ids, setIds] = useState(initial.comicIds);
  const save = useMutation({
    mutationFn: () => adminCatalogApi.saveFeatured({ version: initial.version, comicIds: ids }),
    onSuccess: async () => client.invalidateQueries({ queryKey: ["admin", "featured"] }),
  });
  const move = (index: number, delta: number) => {
    const nextIndex = index + delta;
    if (nextIndex < 0 || nextIndex >= ids.length) return;
    const copy = [...ids];
    const current = copy[index]!;
    copy[index] = copy[nextIndex]!;
    copy[nextIndex] = current;
    setIds(copy);
  };
  return <section className="admin-workspace">
    <h1>Truyện nổi bật</h1>
    <p>Tối đa 5 truyện đã xuất bản. Dùng nút lên/xuống bằng bàn phím để đổi thứ tự.</p>
    <label>Thêm truyện<select value="" onChange={event => {
      const id = event.target.value;
      if (id && !ids.includes(id) && ids.length < 5) setIds([...ids, id]);
    }}><option value="">Chọn truyện</option>{comics.filter(comic => comic.publicationStatus === "Published" && !ids.includes(comic.id)).map(comic => <option key={comic.id} value={comic.id}>{comic.title}</option>)}</select></label>
    <ol aria-live="polite">{ids.map((id, index) => <li key={id}>{comics.find(comic => comic.id === id)?.title ?? id}<Button aria-label={`Đưa mục ${index + 1} lên`} disabled={index === 0} onClick={() => move(index, -1)} variant="outline">↑</Button><Button aria-label={`Đưa mục ${index + 1} xuống`} disabled={index === ids.length - 1} onClick={() => move(index, 1)} variant="outline">↓</Button><Button onClick={() => setIds(ids.filter(item => item !== id))} variant="outline">Bỏ</Button></li>)}</ol>
    {save.isError ? <p role="alert">Không thể lưu. Nếu có xung đột, hãy tải lại danh sách.</p> : null}
    <Button disabled={save.isPending} onClick={() => save.mutate()}>{save.isPending ? "Đang lưu…" : "Lưu thứ tự"}</Button>
  </section>;
}
