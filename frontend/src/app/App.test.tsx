import { render, screen } from "@testing-library/react";
import { App } from "@/app/App";
import { router } from "@/app/router";

describe("application shell", () => {
  it("renders the Vietnamese user shell", async () => {
    await router.navigate("/");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Đọc theo nhịp của bạn." })).toBeVisible();
    expect(screen.getByRole("navigation", { name: "Điều hướng chính" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Khám phá kho truyện" })).toHaveAttribute(
      "href",
      "/comics",
    );
  });

  it("renders the light-only admin shell without fake metrics", async () => {
    await router.navigate("/admin");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Tổng quan" })).toBeVisible();
    expect(screen.getByText("Chưa kết nối dữ liệu quản trị")).toBeVisible();
    expect(document.querySelector(".admin-shell")).toBeInTheDocument();
  });
});
