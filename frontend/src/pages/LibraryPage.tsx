import { BookMarked, BookOpen, Library, RotateCcw, Trash2 } from "lucide-react";
import { useRef, useState, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import { ComicCover } from "@/features/catalog/ComicCover";
import type { ContinueItem, FollowedItem } from "@/features/library/library-api";
import {
  useContinueQuery,
  useFollowsQuery,
  useUnfollowComic,
} from "@/features/library/use-library";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

type LibraryTab = "continue" | "followed";

export function LibraryPage() {
  const [tab, setTab] = useState<LibraryTab>("continue");
  const tabButtons = useRef<Array<HTMLButtonElement | null>>([]);

  function moveTab(event: KeyboardEvent<HTMLButtonElement>, currentIndex: number) {
    const keys = ["ArrowLeft", "ArrowRight", "Home", "End"];
    if (!keys.includes(event.key)) return;
    event.preventDefault();
    const targetIndex = event.key === "Home"
      ? 0
      : event.key === "End"
        ? 1
        : (currentIndex + (event.key === "ArrowRight" ? 1 : -1) + 2) % 2;
    const nextTab: LibraryTab = targetIndex === 0 ? "continue" : "followed";
    setTab(nextTab);
    tabButtons.current[targetIndex]?.focus();
  }

  return (
    <section className="library-page shell-container page-section" aria-labelledby="library-title">
      <header className="library-heading">
        <p className="eyebrow"><Library aria-hidden="true" /> Tủ truyện cá nhân</p>
        <h1 id="library-title">Đánh dấu chỗ đang đọc.</h1>
        <p>Tiến độ do máy chủ xác nhận; truyện đã ẩn sẽ không để lộ chương hoặc trang cũ.</p>
      </header>

      <div aria-label="Nội dung tủ truyện" className="library-tabs" role="tablist">
        <button
          aria-controls="continue-panel"
          aria-selected={tab === "continue"}
          id="continue-tab"
          onClick={() => setTab("continue")}
          onKeyDown={(event) => moveTab(event, 0)}
          ref={(node) => { tabButtons.current[0] = node; }}
          role="tab"
          tabIndex={tab === "continue" ? 0 : -1}
          type="button"
        ><RotateCcw aria-hidden="true" /> Đọc tiếp</button>
        <button
          aria-controls="followed-panel"
          aria-selected={tab === "followed"}
          id="followed-tab"
          onClick={() => setTab("followed")}
          onKeyDown={(event) => moveTab(event, 1)}
          ref={(node) => { tabButtons.current[1] = node; }}
          role="tab"
          tabIndex={tab === "followed" ? 0 : -1}
          type="button"
        ><BookMarked aria-hidden="true" /> Đang theo dõi</button>
      </div>

      {tab === "continue" ? <ContinuePanel /> : <FollowedPanel />}
    </section>
  );
}

function ContinuePanel() {
  const query = useContinueQuery();
  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div aria-labelledby="continue-tab" id="continue-panel" role="tabpanel" tabIndex={0}>
      {query.isPending ? <LoadingState title="Đang lấy dấu trang" /> : null}
      {query.isError ? (
        <ApiErrorState
          error={query.error}
          onRetry={() => void query.refetch()}
          unauthorizedAction={<LoginLink />}
        />
      ) : null}
      {!query.isPending && !query.isError && items.length === 0 ? (
        <EmptyState
          action={<Button asChild="link"><Link to="/comics">Khám phá kho truyện</Link></Button>}
          description="Mở một chương và bắt đầu đọc để tạo dấu trang đầu tiên."
          title="Chưa có truyện đang đọc"
        />
      ) : null}
      {items.length > 0 ? (
        <div className="continue-grid">
          {items.map((item) => <ContinueCard item={item} key={item.comicId} />)}
        </div>
      ) : null}
      <LoadMore query={query} label="Xem thêm lịch sử" />
    </div>
  );
}

function ContinueCard({ item }: { item: ContinueItem }) {
  if (item.availability === "Unavailable" || !item.title) {
    return (
      <article className="continue-card continue-card-unavailable">
        <div className="reading-ribbon" aria-hidden="true" />
        <span className="section-kicker">Không còn hiển thị</span>
        <h2>Truyện đã được ẩn hoặc thay đổi</h2>
        <p>Dấu trang được giữ riêng tư nhưng chương và vị trí cũ không được tiết lộ.</p>
      </article>
    );
  }

  const resumeUrl = item.chapterId && item.pageOrder
    ? `/chapters/${item.chapterId}/read?page=${item.pageOrder}&offset=${item.pageOffsetRatio ?? 0}`
    : undefined;
  return (
    <article className="continue-card">
      <div className="reading-ribbon" aria-hidden="true"><span /></div>
      <ComicCover title={item.title} />
      <div className="continue-card-copy">
        <span className="section-kicker">Đọc gần đây · {formatDate(item.lastReadAt)}</span>
        <h2>{item.title}</h2>
        <p>{item.chapterLabel ?? "Chương đang đọc"}{item.pageOrder ? ` · trang ${item.pageOrder}` : ""}</p>
        {item.acceptedMaxProgressBasisPoints !== null ? (
          <p>Tiến độ đã xác nhận: {Math.floor(item.acceptedMaxProgressBasisPoints / 100)}%</p>
        ) : null}
        {item.canRead && resumeUrl ? (
          <Button asChild="link"><Link to={resumeUrl}><BookOpen aria-hidden="true" /> Đọc tiếp</Link></Button>
        ) : (
          <p className="access-warning" role="status">Quyền đọc cần được kiểm tra lại trước khi mở.</p>
        )}
      </div>
    </article>
  );
}

function FollowedPanel() {
  const query = useFollowsQuery();
  const unfollow = useUnfollowComic();
  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div aria-labelledby="followed-tab" id="followed-panel" role="tabpanel" tabIndex={0}>
      {query.isPending ? <LoadingState title="Đang mở danh sách theo dõi" /> : null}
      {query.isError ? (
        <ApiErrorState
          error={query.error}
          onRetry={() => void query.refetch()}
          unauthorizedAction={<LoginLink />}
        />
      ) : null}
      {!query.isPending && !query.isError && items.length === 0 ? (
        <EmptyState
          action={<Button asChild="link"><Link to="/comics">Tìm truyện để theo dõi</Link></Button>}
          description="Dùng nút Theo dõi trên hồ sơ truyện để giữ truyện ở đây."
          title="Danh sách theo dõi đang trống"
        />
      ) : null}
      {items.length > 0 ? (
        <div className="follow-grid">
          {items.map((item) => (
            <FollowedCard
              item={item}
              key={item.comicId}
              onUnfollow={() => void unfollow.mutateAsync(item.comicId).catch(() => undefined)}
              pending={unfollow.isPending && unfollow.variables === item.comicId}
            />
          ))}
        </div>
      ) : null}
      {unfollow.isError ? <p className="field-error" role="alert">Không thể bỏ theo dõi. Danh sách chưa thay đổi.</p> : null}
      <LoadMore query={query} label="Xem thêm truyện theo dõi" />
    </div>
  );
}

function FollowedCard({
  item,
  onUnfollow,
  pending,
}: {
  item: FollowedItem;
  onUnfollow: () => void;
  pending: boolean;
}) {
  return (
    <article className="follow-card">
      {item.availability === "Available" && item.title && item.slug ? (
        <Link to={`/comics/${item.slug}`}>
          <ComicCover title={item.title} />
          <h2>{item.title}</h2>
        </Link>
      ) : (
        <div className="follow-unavailable">
          <ComicCover title="?" />
          <h2>Truyện không còn hiển thị</h2>
        </div>
      )}
      <p>Theo dõi từ {formatDate(item.followedAt)}</p>
      <Button
        aria-label={item.title ? `Bỏ theo dõi ${item.title}` : "Bỏ theo dõi truyện không còn hiển thị"}
        disabled={pending}
        onClick={onUnfollow}
        variant="ghost"
      ><Trash2 aria-hidden="true" /> {pending ? "Đang bỏ…" : "Bỏ theo dõi"}</Button>
    </article>
  );
}

function LoginLink() {
  return <Button asChild="link"><a href="/api/v1/auth/google/login?returnUrl=%2Flibrary">Đăng nhập với Google</a></Button>;
}

function LoadMore({ query, label }: { query: { hasNextPage: boolean; isFetchingNextPage: boolean; fetchNextPage: () => Promise<unknown> }; label: string }) {
  return query.hasNextPage ? (
    <div className="load-more-row">
      <Button disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()} variant="outline">
        {query.isFetchingNextPage ? "Đang tải…" : label}
      </Button>
    </div>
  ) : null;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat("vi-VN", { dateStyle: "medium" }).format(new Date(value));
}
