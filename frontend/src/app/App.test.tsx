import { render, screen } from "@testing-library/react";
import { App } from "@/app/App";
import { router } from "@/app/router";

describe("application shell", () => {
  afterEach(() => vi.unstubAllGlobals());
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
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(Response.json({
      id: "11111111-1111-4111-8111-111111111111",
      displayName: "Admin",
      email: "admin@example.test",
      avatarUrl: null,
      role: "Admin",
      status: "Active",
    }, { headers: { "X-CSRF-TOKEN": "app-test-csrf" } }))));
    await router.navigate("/admin");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Tổng quan" })).toBeVisible();
    expect(screen.getByText("Chưa kết nối dữ liệu quản trị")).toBeVisible();
    expect(document.querySelector(".admin-shell")).toBeInTheDocument();
  });
});
