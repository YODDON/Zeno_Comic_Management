import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, BookOpen, CheckCircle2, CircleAlert, LockKeyhole, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useProfileQuery } from "@/features/profile/use-profile";
import {
  createReadingSession,
  getChapterAccess,
  getChapterManifest,
  qualifyReadingSession,
  updateReadingSession,
  type ChapterManifest,
  type ReadingSession,
} from "@/features/reader/reader-api";
import { ApiError } from "@/shared/api/problem-details";
import { ApiErrorState, ErrorState, LoadingState, UnauthorizedState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const heartbeatIntervalMs = 10_000;
type ReaderSyncStatus = "guest" | "starting" | "active" | "qualified" | "expired" | "error";
interface ReaderPosition { pageOrder: number; pageOffsetRatio: number }

export function ReaderPage() {
  const { chapterId = "" } = useParams();
  const access = useQuery({
    queryKey: ["reading", "access", chapterId],
    queryFn: () => getChapterAccess(chapterId),
    enabled: Boolean(chapterId),
  });
  const manifest = useQuery({
    queryKey: ["reading", "manifest", chapterId],
    queryFn: () => getChapterManifest(chapterId),
    enabled: access.data?.canRead === true,
    staleTime: 0,
  });

  if (!chapterId) {
    return <ReaderShell><ErrorState description="Đường dẫn chương không hợp lệ." title="Không thể mở chương" /></ReaderShell>;
  }
  if (access.isPending) return <ReaderShell><LoadingState title="Đang kiểm tra quyền đọc" /></ReaderShell>;
  if (access.isError) {
    return <ReaderShell><ApiErrorState error={access.error} onRetry={() => void access.refetch()} /></ReaderShell>;
  }
  if (!access.data.canRead) {
    if (access.data.needsLogin) {
      return (
        <ReaderShell>
          <UnauthorizedState
            action={<Button asChild="link"><a href={`/api/v1/auth/google/login?returnUrl=${encodeURIComponent(`/chapters/${chapterId}/read`)}`}>Đăng nhập với Google</a></Button>}
            description="Đăng nhập để máy chủ kiểm tra quyền đọc chương này."
          />
        </ReaderShell>
      );
    }
    return (
      <ReaderShell>
        <div className="state-panel state-panel-large" role="status">
          <LockKeyhole aria-hidden="true" className="state-icon state-icon-error" />
          <h2>Chương đang bị khóa</h2>
          <p>Cần mở khóa chương trước khi ảnh được tải. Giá hiện tại: {access.data.priceCoins.toLocaleString("vi-VN")} Coin.</p>
          <Button asChild="link"><Link to="/wallet">Mở ví</Link></Button>
        </div>
      </ReaderShell>
    );
  }
  if (manifest.isPending) return <ReaderShell><LoadingState title="Đang chuẩn bị trang truyện" /></ReaderShell>;
  if (manifest.isError) {
    return <ReaderShell><ManifestError error={manifest.error} onRetry={() => void manifest.refetch()} /></ReaderShell>;
  }
  return (
    <ReaderContent
      chapterId={chapterId}
      manifest={manifest.data}
      refreshManifest={() => manifest.refetch().then((result) => result.data)}
    />
  );
}

function ReaderContent({
  chapterId,
  manifest,
  refreshManifest,
}: {
  chapterId: string;
  manifest: ChapterManifest;
  refreshManifest: () => Promise<ChapterManifest | undefined>;
}) {
  const profile = useProfileQuery();
  const [searchParams] = useSearchParams();
  const [position, setPosition] = useState<ReaderPosition>(() => initialPosition(manifest, searchParams));
  const positionRef = useRef(position);
  const pageElements = useRef(new Map<number, HTMLElement>());
  const startedSession = useRef(false);
  const sessionRef = useRef<ReadingSession | undefined>(undefined);
  const nextSequence = useRef(1);
  const pendingHeartbeat = useRef<{ sequence: number; position: ReaderPosition } | undefined>(undefined);
  const heartbeatRunning = useRef(false);
  const qualificationRequested = useRef(false);
  const manifestRevision = useRef<string | undefined>(undefined);
  const [syncStatus, setSyncStatus] = useState<ReaderSyncStatus>("starting");
  const [acceptedProgress, setAcceptedProgress] = useState(0);
  const [activeDurationMs, setActiveDurationMs] = useState(0);
  const [toolbarVisible, setToolbarVisible] = useState(true);
  const [failedPages, setFailedPages] = useState<Set<number>>(new Set());
  const [imageAttempt, setImageAttempt] = useState(0);
  const initialScrollDone = useRef(false);
  const lastScrollY = useRef(0);

  useEffect(() => { positionRef.current = position; }, [position]);

  useEffect(() => {
    if (profile.isPending || startedSession.current) return;
    startedSession.current = true;
    if (!profile.data) return;

    void createReadingSession(chapterId)
      .then((session) => {
        sessionRef.current = session;
        manifestRevision.current = session.manifestRevision;
        nextSequence.current = 1;
        setSyncStatus("active");
      })
      .catch((error: unknown) => {
        setSyncStatus(error instanceof ApiError && error.status === 401 ? "guest" : "error");
      });
  }, [chapterId, profile.data, profile.isPending]);

  const sendHeartbeat = useCallback(async () => {
    const session = sessionRef.current;
    if (!session || heartbeatRunning.current) return;

    heartbeatRunning.current = true;
    const pending = pendingHeartbeat.current ?? {
      sequence: nextSequence.current,
      position: positionRef.current,
    };
    pendingHeartbeat.current = pending;
    try {
      const heartbeat = await updateReadingSession(
        session.sessionId,
        pending.position.pageOrder,
        pending.position.pageOffsetRatio,
        pending.sequence,
      );
      nextSequence.current = heartbeat.lastAcceptedSequence + 1;
      pendingHeartbeat.current = undefined;
      setAcceptedProgress(heartbeat.acceptedMaxProgressBasisPoints);
      setActiveDurationMs(heartbeat.activeDurationMs);

      if (manifestRevision.current !== heartbeat.manifestRevision) {
        manifestRevision.current = heartbeat.manifestRevision;
        await refreshManifest();
      }
      if (
        !qualificationRequested.current &&
        heartbeat.acceptedMaxProgressBasisPoints >= session.policy.minimumProgressBasisPoints &&
        heartbeat.activeDurationMs >= session.policy.minimumActiveDurationMs
      ) {
        qualificationRequested.current = true;
        try {
          const qualification = await qualifyReadingSession(session.sessionId);
          setSyncStatus(qualification.qualified ? "qualified" : "active");
          if (!qualification.qualified) qualificationRequested.current = false;
        } catch (error) {
          qualificationRequested.current = false;
          if (error instanceof ApiError && error.errorCode === "READING_SESSION_EXPIRED") {
            setSyncStatus("expired");
          } else if (error instanceof ApiError && error.status < 500) {
            setSyncStatus("error");
          }
        }
      }
    } catch (error) {
      if (error instanceof ApiError && error.errorCode === "READING_SESSION_EXPIRED") {
        setSyncStatus("expired");
      } else if (error instanceof ApiError && error.status < 500) {
        setSyncStatus("error");
      }
    } finally {
      heartbeatRunning.current = false;
    }
  }, [refreshManifest]);

  useEffect(() => {
    if (syncStatus !== "active" && syncStatus !== "qualified") return;
    const interval = window.setInterval(() => void sendHeartbeat(), heartbeatIntervalMs);
    return () => window.clearInterval(interval);
  }, [sendHeartbeat, syncStatus]);

  useEffect(() => {
    if (initialScrollDone.current) return;
    const target = pageElements.current.get(position.pageOrder);
    if (!target) return;
    initialScrollDone.current = true;
    const top = target.offsetTop + target.offsetHeight * position.pageOffsetRatio;
    window.scrollTo({ top: Math.max(0, top - window.innerHeight * 0.2), behavior: "auto" });
  }, [manifest.pages, position.pageOffsetRatio, position.pageOrder]);

  useEffect(() => {
    let frame = 0;
    const onScroll = () => {
      if (frame) return;
      frame = window.requestAnimationFrame(() => {
        frame = 0;
        const anchor = window.innerHeight * 0.45;
        let nearest: ReaderPosition | undefined;
        let distance = Number.POSITIVE_INFINITY;
        for (const page of manifest.pages) {
          const element = pageElements.current.get(page.order);
          if (!element) continue;
          const rect = element.getBoundingClientRect();
          const pageDistance = anchor < rect.top ? rect.top - anchor : anchor > rect.bottom ? anchor - rect.bottom : 0;
          if (pageDistance < distance) {
            distance = pageDistance;
            nearest = {
              pageOrder: page.order,
              pageOffsetRatio: roundOffset(Math.min(1, Math.max(0, (anchor - rect.top) / Math.max(1, rect.height)))),
            };
          }
        }
        if (nearest) setPosition(nearest);
        const scrollY = window.scrollY;
        if (Math.abs(scrollY - lastScrollY.current) > 8) {
          setToolbarVisible(scrollY < 100 || scrollY < lastScrollY.current);
          lastScrollY.current = scrollY;
        }
      });
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    onScroll();
    return () => {
      window.removeEventListener("scroll", onScroll);
      if (frame) window.cancelAnimationFrame(frame);
    };
  }, [manifest.pages]);

  const registerPage = useCallback((order: number, node: HTMLElement | null) => {
    if (node) pageElements.current.set(order, node);
    else pageElements.current.delete(order);
  }, []);

  const retryPage = useCallback(async (order: number) => {
    await refreshManifest().catch(() => undefined);
    setFailedPages((current) => {
      const next = new Set(current);
      next.delete(order);
      return next;
    });
    setImageAttempt((value) => value + 1);
  }, [refreshManifest]);

  const resolvedSyncStatus = syncStatus === "starting" && !profile.isPending && !profile.data
    ? profile.error instanceof ApiError && profile.error.status !== 401 ? "error" : "guest"
    : syncStatus;
  const displayProgress = resolvedSyncStatus === "guest" ? clientProgress(manifest, position) : acceptedProgress;
  return (
    <div className="reader-page" onClick={() => setToolbarVisible(true)}>
      <header className={`reader-toolbar ${toolbarVisible ? "reader-toolbar-visible" : "reader-toolbar-hidden"}`}>
        <Link aria-label="Quay lại Tủ truyện" className="reader-back" to="/library"><ArrowLeft aria-hidden="true" /></Link>
        <div><strong>Chương đang đọc</strong><span>Trang {position.pageOrder} · {Math.round(displayProgress / 100)}%</span></div>
        <SyncBadge activeDurationMs={activeDurationMs} status={resolvedSyncStatus} />
      </header>

      <div aria-label="Trang truyện" className="reader-canvas" id="reader-content" role="region">
        <div className="reader-opening"><BookOpen aria-hidden="true" /><p>Cuộn dọc để đọc · tiến độ xác nhận bởi máy chủ</p></div>
        {manifest.pages.map((page) => {
          const failed = failedPages.has(page.order);
          const style = { "--page-ratio": `${page.width} / ${page.height}` } as CSSProperties;
          return (
            <figure className="reader-page-frame" data-page-order={page.order} key={page.order} ref={(node) => registerPage(page.order, node)} style={style}>
              {failed ? (
                <div className="reader-image-error" role="alert">
                  <CircleAlert aria-hidden="true" /><strong>Không tải được trang {page.order}</strong>
                  <Button onClick={() => void retryPage(page.order)} variant="outline"><RefreshCw aria-hidden="true" /> Tải lại trang</Button>
                </div>
              ) : (
                <img
                  alt={`Trang ${page.order}`}
                  decoding="async"
                  height={page.height}
                  key={`${page.deliveryUrl}-${imageAttempt}`}
                  loading={page.order <= 2 ? "eager" : "lazy"}
                  onError={() => setFailedPages((current) => new Set(current).add(page.order))}
                  src={page.deliveryUrl}
                  width={page.width}
                />
              )}
              <figcaption>Trang {page.order}</figcaption>
            </figure>
          );
        })}
        <section className="reader-finish" aria-labelledby="reader-finish-title">
          <CheckCircle2 aria-hidden="true" /><p className="section-kicker">Cuối chương</p>
          <h1 id="reader-finish-title">Bạn đã đến trang cuối.</h1>
          <p>Tiến độ và điều kiện hoàn thành chỉ được ghi nhận sau xác minh của máy chủ.</p>
          <div><Button asChild="link"><Link to="/library">Về Tủ truyện</Link></Button><Button asChild="link" variant="outline"><Link to="/comics">Khám phá truyện khác</Link></Button></div>
        </section>
      </div>
    </div>
  );
}

function ReaderShell({ children }: { children: ReactNode }) {
  return <section className="reader-state shell-container page-section">{children}</section>;
}

function ManifestError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  if (error instanceof ApiError && error.status === 503) {
    return <ErrorState description="Kênh phân phối ảnh được bảo vệ chưa sẵn sàng. Không có URL tài sản riêng tư nào được công khai." onRetry={onRetry} title="Ảnh truyện tạm thời chưa khả dụng" />;
  }
  return <ApiErrorState error={error} onRetry={onRetry} />;
}

function SyncBadge({ status, activeDurationMs }: { status: ReaderSyncStatus; activeDurationMs: number }) {
  const label = {
    guest: "Khách · không lưu tiến độ",
    starting: "Đang mở phiên đọc",
    active: `Đã đồng bộ · ${Math.floor(activeDurationMs / 1000)} giây`,
    qualified: "Đã xác nhận hoàn thành",
    expired: "Phiên đọc đã hết hạn",
    error: "Tạm dừng đồng bộ",
  }[status];
  return <span className={`reader-sync reader-sync-${status}`} role="status">{label}</span>;
}

function initialPosition(manifest: ChapterManifest, params: URLSearchParams): ReaderPosition {
  const requestedOrder = Number(params.get("page"));
  const requestedOffset = Number(params.get("offset"));
  return {
    pageOrder: manifest.pages.some((page) => page.order === requestedOrder) ? requestedOrder : manifest.pages[0]?.order ?? 1,
    pageOffsetRatio: Number.isFinite(requestedOffset) ? Math.min(1, Math.max(0, requestedOffset)) : 0,
  };
}

function clientProgress(manifest: ChapterManifest, position: ReaderPosition) {
  const index = manifest.pages.findIndex((page) => page.order === position.pageOrder);
  return index < 0 || manifest.pages.length === 0 ? 0 : Math.round(((index + position.pageOffsetRatio) / manifest.pages.length) * 10_000);
}

function roundOffset(value: number) { return Math.round(value * 10_000) / 10_000; }
