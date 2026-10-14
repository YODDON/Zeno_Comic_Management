import { Bookmark, BookOpen, ChevronLeft, ChevronRight, LockKeyhole } from "lucide-react";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ComicCover } from "@/features/catalog/ComicCover";
import { useChaptersQuery, useComicQuery } from "@/features/catalog/use-catalog";
import { useFollowComic } from "@/features/library/use-library";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiError } from "@/shared/api/problem-details";
import { ApiErrorState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

export function ComicDetailPage() {
  const { slug = "" } = useParams();
  const [page, setPage] = useState(1);
  const comic = useComicQuery(slug);
  const chapters = useChaptersQuery(comic.data?.id, page);
  const follow = useFollowComic();
  const profile = useProfileQuery();

  if (comic.isPending) {
    return <div className="shell-container page-section"><LoadingState title="Đang mở hồ sơ truyện" /></div>;
  }

  if (comic.isError) {
    return <div className="shell-container page-section"><ApiErrorState error={comic.error} onRetry={() => void comic.refetch()} /></div>;
  }

  const detail = comic.data;
  const firstReadable = chapters.data?.items.find((chapter) => chapter.accessSummary.canRead);
  const followNeedsLogin =
    (!profile.isPending && !profile.data && profile.error instanceof ApiError && profile.error.status === 401) ||
    (follow.error instanceof ApiError && follow.error.status === 401);

  return (
    <article className="comic-detail-page shell-container page-section">
      <header className="comic-detail-hero">
        <ComicCover title={detail.title} />
        <div>
          <p className="eyebrow"><BookOpen aria-hidden="true" /> Hồ sơ truyện</p>
          <h1>{detail.title}</h1>
          <p className="comic-detail-meta">
            {[...detail.authors, ...detail.translationGroups].map((item) => item.name).join(" · ") || "Zeno tuyển chọn"}
          </p>
          <p>{detail.description || "Truyện chưa có phần giới thiệu."}</p>
          <div className="tag-row">
            {detail.genres.map((genre) => <span key={genre.id}>{genre.name}</span>)}
          </div>
          <div className="comic-detail-actions">
            {firstReadable ? (
              <Button asChild="link"><Link to={`/chapters/${firstReadable.id}/read`}>Đọc từ đầu</Link></Button>
            ) : (
              <Button disabled>Chưa có chương có thể đọc</Button>
            )}
            <Button
              disabled={profile.isPending || !profile.data || follow.isPending || follow.isSuccess}
              onClick={() => void follow.mutateAsync(detail.id).catch(() => undefined)}
              variant="outline"
            >
              <Bookmark aria-hidden="true" />
              {profile.isPending ? "Đang kiểm tra phiên…" : follow.isPending ? "Đang lưu…" : follow.isSuccess ? "Đã theo dõi" : "Theo dõi truyện"}
            </Button>
          </div>
          {followNeedsLogin ? (
            <p className="inline-notice" role="alert">
              <a href={`/api/v1/auth/google/login?returnUrl=${encodeURIComponent(`/comics/${detail.slug}`)}`}>
                Đăng nhập với Google
              </a>{" "}để theo dõi truyện.
            </p>
          ) : null}
          {follow.isError && !followNeedsLogin ? <p className="field-error" role="alert">Không thể theo dõi lúc này. Hãy thử lại.</p> : null}
        </div>
      </header>

      <section className="chapter-section" aria-labelledby="chapter-heading">
        <div className="section-heading-row">
          <div>
            <p className="section-kicker">Danh sách đọc</p>
            <h2 id="chapter-heading">Các chương đã xuất bản</h2>
          </div>
          {chapters.data ? <span>{chapters.data.totalItems} chương</span> : null}
        </div>
        {chapters.isPending ? <LoadingState title="Đang tải chương" /> : null}
        {chapters.isError ? <ApiErrorState error={chapters.error} onRetry={() => void chapters.refetch()} /> : null}
        <ol className="chapter-list">
          {chapters.data?.items.map((chapter) => (
            <li key={chapter.id}>
              <span className="chapter-order">{String(chapter.sortOrder).padStart(2, "0")}</span>
              <div><strong>{chapter.chapterLabel}</strong><small>{accessLabel(chapter.accessSummary.accessType, chapter.accessSummary.finalPriceCoins)}</small></div>
              {chapter.accessSummary.canRead ? (
                <Button asChild="link" size="icon" variant="outline">
                  <Link aria-label={`Đọc ${chapter.chapterLabel}`} to={`/chapters/${chapter.id}/read`}><BookOpen aria-hidden="true" /></Link>
                </Button>
              ) : <LockKeyhole aria-label="Chương đang khóa" className="chapter-lock" />}
            </li>
          ))}
        </ol>
        {chapters.data && chapters.data.totalPages > 1 ? (
          <nav aria-label="Phân trang chương" className="chapter-pagination">
            <Button disabled={page === 1} onClick={() => setPage((value) => value - 1)} variant="outline"><ChevronLeft /> Trang trước</Button>
            <span>Trang {page} / {chapters.data.totalPages}</span>
            <Button disabled={page >= chapters.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau <ChevronRight /></Button>
          </nav>
        ) : null}
      </section>
    </article>
  );
}

function accessLabel(type: string, price: number) {
  if (type === "Free") return "Đọc miễn phí";
  if (type === "VipIncluded") return "Đã gồm trong VIP";
  if (type === "CoinUnlocked") return "Đã mở khóa";
  return `${price.toLocaleString("vi-VN")} Coin`;
}
