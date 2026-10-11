import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  EmptyState,
  ErrorState,
  LoadingState,
  PendingState,
  SuccessState,
} from "@/shared/ui/AsyncState";

describe("async state primitives", () => {
  it("announces loading, pending and success states", () => {
    const { rerender } = render(<LoadingState />);
    expect(screen.getByRole("status")).toHaveTextContent("Đang tải dữ liệu");

    rerender(<PendingState description="Chờ xác nhận." title="Đang xử lý" />);
    expect(screen.getByRole("status")).toHaveTextContent("Đang xử lý");

    rerender(<SuccessState description="Dữ liệu đã được xác nhận." title="Hoàn tất" />);
    expect(screen.getByRole("status")).toHaveTextContent("Hoàn tất");
  });

  it("renders a directional empty state", () => {
    render(<EmptyState description="Hãy quay lại kho truyện." title="Chưa có dữ liệu" />);

    expect(screen.getByRole("heading", { name: "Chưa có dữ liệu" })).toBeVisible();
    expect(screen.getByText("Hãy quay lại kho truyện.")).toBeVisible();
  });

  it("announces errors and exposes retry", async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    render(<ErrorState description="Kiểm tra kết nối." onRetry={onRetry} title="Không tải được" />);

    expect(screen.getByRole("alert")).toHaveTextContent("Không tải được");
    await user.click(screen.getByRole("button", { name: "Thử lại" }));
    expect(onRetry).toHaveBeenCalledOnce();
  });
});
