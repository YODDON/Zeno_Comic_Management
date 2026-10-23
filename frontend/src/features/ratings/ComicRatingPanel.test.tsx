import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ComicRatingPanel } from "@/features/ratings/ComicRatingPanel";
import { clearApiSessionState } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

const comicId = "4ee9222c-9ea2-4adf-a115-36facdef085a";
const initialRating = { averageStars: 3, ratingCount: 1, myRating: 3 };

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function renderPanel(overrides: Partial<React.ComponentProps<typeof ComicRatingPanel>> = {}) {
  const queryClient = createQueryClient();
  const props: React.ComponentProps<typeof ComicRatingPanel> = {
    comicId,
    initialRating,
    isAuthenticated: true,
    loginUrl: "/login",
    viewerKey: "user-one",
    ...overrides,
  };
  return render(
    <QueryClientProvider client={queryClient}>
      <ComicRatingPanel {...props} />
    </QueryClientProvider>,
  );
}

describe("ComicRatingPanel", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("supports radiogroup focus and arrow-key single selection", async () => {
    const user = userEvent.setup();
    vi.stubGlobal("fetch", vi.fn((_: RequestInfo | URL, init?: RequestInit) => {
      if ((init?.method ?? "GET") === "PUT") {
        return jsonResponse({ averageStars: 4, ratingCount: 1, myRating: 4 });
      }
      return jsonResponse(initialRating);
    }));
    renderPanel();

    const current = screen.getByRole("radio", { name: "3 sao" });
    current.focus();
    expect(current).toHaveFocus();
    await user.keyboard("{ArrowRight}");

    await waitFor(() => expect(screen.getByRole("radio", { name: "4 sao" })).toBeChecked());
    expect(screen.getByRole("group", { name: "Đánh giá truyện" })).toBeInTheDocument();
  });

  it("renders loading, then an accessible error and retries", async () => {
    const user = userEvent.setup();
    let attempt = 0;
    vi.stubGlobal("fetch", vi.fn(() => {
      attempt += 1;
      return attempt === 1
        ? jsonResponse({ errorCode: "INTERNAL_ERROR", message: "failed", traceId: "trace" }, 500)
        : jsonResponse({ averageStars: 0, ratingCount: 0, myRating: null });
    }));
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <ComicRatingPanel comicId={comicId} isAuthenticated loginUrl="/login" viewerKey="user-one" />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Đang tải đánh giá…")).toHaveAttribute("aria-busy", "true");
    expect(await screen.findByRole("alert")).toHaveTextContent("Không thể tải đánh giá");
    await user.click(screen.getByRole("button", { name: "Thử lại" }));
    expect(await screen.findByText("Chưa có đánh giá")).toBeInTheDocument();
  });

  it("disables every option while pending and keeps authoritative state on failure", async () => {
    const user = userEvent.setup();
    let rejectRequest: (() => void) | undefined;
    const pending = new Promise<Response>((resolve) => {
      rejectRequest = () => resolve(jsonResponse(
        { errorCode: "INTERNAL_ERROR", message: "failed", traceId: "trace" },
        500,
      ));
    });
    const fetchMock = vi.fn((_: RequestInfo | URL, init?: RequestInit) =>
      (init?.method ?? "GET") === "PUT" ? pending : Promise.resolve(jsonResponse(initialRating)));
    vi.stubGlobal("fetch", fetchMock);
    renderPanel();

    await waitFor(() => expect(screen.getByRole("radio", { name: "3 sao" })).toBeChecked());
    await user.click(screen.getByRole("radio", { name: "4 sao" }));
    await waitFor(() => expect(screen.getByRole("radio", { name: "5 sao" })).toBeDisabled());
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === "PUT")).toHaveLength(1);
    rejectRequest!();

    expect(await screen.findByRole("alert")).toHaveTextContent("Không thể lưu đánh giá");
    await waitFor(() => expect(screen.getByRole("radio", { name: "3 sao" })).toBeChecked());
    expect(screen.getByRole("radio", { name: "4 sao" })).not.toBeChecked();
  });

  it("does not reuse myRating when the authenticated identity changes", async () => {
    let viewer = "user-one";
    const fetchMock = vi.fn(() => jsonResponse(
      viewer === "user-one"
        ? initialRating
        : { averageStars: 3, ratingCount: 1, myRating: null },
    ));
    vi.stubGlobal("fetch", fetchMock);
    const queryClient = createQueryClient();
    const view = render(
      <QueryClientProvider client={queryClient}>
        <ComicRatingPanel comicId={comicId} initialRating={initialRating} isAuthenticated loginUrl="/login" viewerKey="user-one" />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(screen.getByRole("radio", { name: "3 sao" })).toBeChecked());

    viewer = "user-two";
    view.rerender(
      <QueryClientProvider client={queryClient}>
        <ComicRatingPanel comicId={comicId} initialRating={initialRating} isAuthenticated loginUrl="/login" viewerKey="user-two" />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("radio", { name: "3 sao" })).not.toBeChecked());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
