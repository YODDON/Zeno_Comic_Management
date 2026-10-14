import { ArrowRight, Search } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { ComicCover } from "@/features/catalog/ComicCover";
import { useCatalogQuery } from "@/features/catalog/use-catalog";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

export function CatalogPage() {
  const [draftSearch, setDraftSearch] = useState("");
  const [search, setSearch] = useState("");
  const catalog = useCatalogQuery(search);
  const comics = catalog.data?.pages.flatMap((page) => page.items) ?? [];

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSearch(draftSearch.trim());
  }

  return (
    <section className="catalog-page shell-container page-section" aria-labelledby="catalog-title">
      <header className="catalog-heading">
        <p className="eyebrow"><Search aria-hidden="true" /> Kho truyện đã xuất bản</p>
        <h1 id="catalog-title">Tìm nhịp đọc tiếp theo.</h1>
        <p>Chỉ những truyện và chương đang phát hành hợp lệ mới xuất hiện tại đây.</p>
      </header>

      <form className="catalog-search" role="search" onSubmit={submitSearch}>
        <label htmlFor="catalog-search-input">Tìm theo tên truyện</label>
        <div>
          <input
            id="catalog-search-input"
            maxLength={200}
            onChange={(event) => setDraftSearch(event.target.value)}
            placeholder="Ví dụ: hành trình, học đường…"
            value={draftSearch}
          />
          <Button type="submit"><Search aria-hidden="true" /> Tìm truyện</Button>
        </div>
      </form>

      {catalog.isPending ? <LoadingState title="Đang mở kho truyện" /> : null}
      {catalog.isError ? (
        <ApiErrorState error={catalog.error} onRetry={() => void catalog.refetch()} />
      ) : null}
      {!catalog.isPending && !catalog.isError && comics.length === 0 ? (
        <EmptyState
          title={search ? "Không tìm thấy truyện phù hợp" : "Kho truyện đang được chuẩn bị"}
          description={search ? "Thử một từ khóa ngắn hơn hoặc xóa nội dung tìm kiếm." : "Hãy quay lại khi truyện đầu tiên được xuất bản."}
          action={search ? <Button onClick={() => { setDraftSearch(""); setSearch(""); }} variant="outline">Xóa tìm kiếm</Button> : undefined}
        />
      ) : null}

      {comics.length > 0 ? (
        <>
          <div className="comic-grid" aria-live="polite">
            {comics.map((comic) => (
              <article className="comic-card" key={comic.id}>
                <Link aria-label={`Mở ${comic.title}`} to={`/comics/${comic.slug}`}>
                  <ComicCover title={comic.title} />
                  <div className="comic-card-copy">
                    <span>{statusLabel(comic.seriesStatus)}</span>
                    <h2>{comic.title}</h2>
                    <p>{comic.genres.map((genre) => genre.name).join(" · ") || "Truyện tranh"}</p>
                    <strong>Đọc chi tiết <ArrowRight aria-hidden="true" /></strong>
                  </div>
                </Link>
              </article>
            ))}
          </div>
          {catalog.hasNextPage ? (
            <div className="load-more-row">
              <Button
                disabled={catalog.isFetchingNextPage}
                onClick={() => void catalog.fetchNextPage()}
                variant="outline"
              >
                {catalog.isFetchingNextPage ? "Đang tải…" : "Xem thêm truyện"}
              </Button>
            </div>
          ) : null}
        </>
      ) : null}
    </section>
  );
}

function statusLabel(status: "Ongoing" | "Completed" | "Hiatus") {
  return { Ongoing: "Đang phát hành", Completed: "Hoàn thành", Hiatus: "Tạm dừng" }[status];
}
