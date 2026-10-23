import { Star } from "lucide-react";
import { useComicRatingQuery, useDeleteComicRating, useSetComicRating } from "@/features/ratings/use-ratings";
import { Button } from "@/shared/ui/Button";
import type { ComicRating } from "@/features/ratings/ratings-api";

interface ComicRatingPanelProps {
  comicId: string;
  isAuthenticated: boolean;
  loginUrl: string;
  initialRating?: ComicRating;
  viewerKey: string;
}

export function ComicRatingPanel({ comicId, isAuthenticated, loginUrl, initialRating, viewerKey }: ComicRatingPanelProps) {
  const rating = useComicRatingQuery(comicId, viewerKey, initialRating);
  const setRating = useSetComicRating(comicId, viewerKey);
  const deleteRating = useDeleteComicRating(comicId, viewerKey);
  const pending = setRating.isPending || deleteRating.isPending;

  if (rating.isPending) {
    return <section aria-busy="true" className="rating-panel">Đang tải đánh giá…</section>;
  }

  if (rating.isError || !rating.data) {
    return (
      <section className="rating-panel">
        <p className="field-error" role="alert">Không thể tải đánh giá lúc này.</p>
        <Button onClick={() => void rating.refetch()} variant="outline">Thử lại</Button>
      </section>
    );
  }

  const current = rating.data.myRating;
  return (
    <section aria-labelledby="rating-heading" className="rating-panel">
      <div>
        <p className="section-kicker">Đánh giá cộng đồng</p>
        <h2 id="rating-heading">
          {rating.data.ratingCount === 0
            ? "Chưa có đánh giá"
            : `${rating.data.averageStars.toFixed(1)} / 5`}
        </h2>
        <p>{rating.data.ratingCount.toLocaleString("vi-VN")} lượt đánh giá</p>
      </div>
      {isAuthenticated ? (
        <div>
          <fieldset className="rating-stars" disabled={pending}>
            <legend className="sr-only">Đánh giá truyện</legend>
            {[1, 2, 3, 4, 5].map((stars) => (
              <label
                className={current !== null && stars <= current ? "is-selected" : undefined}
                key={stars}
              >
                <input
                  checked={current === stars}
                  name={`comic-rating-${comicId}`}
                  onChange={() => setRating.mutate(stars)}
                  type="radio"
                  value={stars}
                />
                <Star aria-hidden="true" />
                <span className="sr-only">{stars} sao</span>
              </label>
            ))}
          </fieldset>
          {current ? (
            <Button disabled={pending} onClick={() => deleteRating.mutate()} variant="ghost">
              Xóa đánh giá của tôi
            </Button>
          ) : null}
          {setRating.isError || deleteRating.isError ? (
            <p className="field-error" role="alert">Không thể lưu đánh giá. Hãy thử lại.</p>
          ) : null}
        </div>
      ) : (
        <p className="inline-notice"><a href={loginUrl}>Đăng nhập với Google</a> để đánh giá truyện.</p>
      )}
    </section>
  );
}
