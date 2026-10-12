import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "@/app/App";
import { router } from "@/app/router";
import { clearApiSessionState } from "@/shared/api/client";

const originalProfile = {
  id: "4d29db54-02f1-4e99-9ba3-ccbd6bf5c03f",
  displayName: "Tên ban đầu",
  email: "reader@example.com",
  avatarUrl: "https://example.com/avatar.png",
  role: "User",
  status: "Active",
};

function jsonResponse(
  body: unknown,
  status = 200,
  headers: Record<string, string> = {},
) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": status >= 400 ? "application/problem+json" : "application/json",
      ...headers,
    },
  });
}

describe("profile display name", () => {
  afterEach(() => {
    clearApiSessionState();
    vi.unstubAllGlobals();
  });

  it("loads the read-only Google identity and updates the header after saving", async () => {
    const user = userEvent.setup();
    const updatedProfile = { ...originalProfile, displayName: "Bút danh mới" };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse(originalProfile, 200, { "X-CSRF-TOKEN": "profile-csrf-one" }),
      )
      .mockResolvedValueOnce(
        jsonResponse(updatedProfile, 200, { "X-CSRF-TOKEN": "profile-csrf-two" }),
      );
    vi.stubGlobal("fetch", fetchMock);
    await router.navigate("/profile");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Hồ sơ của bạn" })).toBeVisible();
    expect(screen.getByText("reader@example.com")).toBeVisible();
    expect(screen.getByRole("link", { name: /Xem ví/ })).toHaveAttribute("href", "/wallet");
    expect(screen.getByRole("link", { name: /Quyền lợi VIP/ })).toHaveAttribute("href", "/vip");
    expect(screen.getByRole("button", { name: "Sáng" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: "Tối" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByLabelText(/vai trò/i)).not.toBeInTheDocument();
    const input = screen.getByRole("textbox", { name: "Tên hiển thị" });
    await user.clear(input);
    await user.type(input, "  Bút danh mới  ");
    await user.click(screen.getByRole("button", { name: "Lưu tên hiển thị" }));

    expect(await screen.findByText("Tên hiển thị đã được cập nhật.")).toBeVisible();
    const primaryNavigation = screen.getByRole("navigation", { name: "Điều hướng chính" });
    expect(within(primaryNavigation).getByRole("link", { name: "Bút danh mới" })).toHaveAttribute(
      "href",
      "/profile",
    );

    const [, patchInit] = fetchMock.mock.calls[1] as [string, RequestInit];
    const patchHeaders = new Headers(patchInit.headers);
    expect(patchInit.method).toBe("PATCH");
    expect(patchHeaders.get("X-CSRF-TOKEN")).toBe("profile-csrf-one");
    expect(patchInit.body).toBe(JSON.stringify({ displayName: "Bút danh mới" }));
  });

  it("validates Unicode length before sending the update", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse(originalProfile, 200, { "X-CSRF-TOKEN": "profile-csrf" }),
    );
    vi.stubGlobal("fetch", fetchMock);
    await router.navigate("/profile");
    render(<App />);
    const input = await screen.findByRole("textbox", { name: "Tên hiển thị" });
    await user.clear(input);
    await user.type(input, "a".repeat(121));
    await user.click(screen.getByRole("button", { name: "Lưu tên hiển thị" }));

    expect(
      await screen.findByText("Tên hiển thị không được vượt quá 120 ký tự."),
    ).toBeVisible();
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("shows the sign-in state for an unauthenticated profile request", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse(
          { title: "Authentication required", status: 401, errorCode: "AUTH_REQUIRED" },
          401,
        ),
      ),
    );
    await router.navigate("/profile");

    render(<App />);

    expect(await screen.findByRole("heading", { name: "Bạn chưa đăng nhập" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Đăng nhập với Google" })).toHaveAttribute(
      "href",
      "/api/v1/auth/google/login?returnUrl=%2Fprofile",
    );
  });

  it("does not accept a malformed successful update response", async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(
          jsonResponse(originalProfile, 200, { "X-CSRF-TOKEN": "profile-csrf" }),
        )
        .mockResolvedValueOnce(jsonResponse({ displayName: "Incomplete" })),
    );
    await router.navigate("/profile");
    render(<App />);
    const input = await screen.findByRole("textbox", { name: "Tên hiển thị" });
    await user.clear(input);
    await user.type(input, "Incomplete");
    await user.click(screen.getByRole("button", { name: "Lưu tên hiển thị" }));

    expect(
      await screen.findByText("Phản hồi từ máy chủ không hợp lệ. Vui lòng thử lại."),
    ).toBeVisible();
  });

  it("replaces the form with the sign-in state when the session expires during save", async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(
          jsonResponse(originalProfile, 200, { "X-CSRF-TOKEN": "profile-csrf" }),
        )
        .mockResolvedValueOnce(
          jsonResponse(
            { title: "Authentication required", status: 401, errorCode: "AUTH_REQUIRED" },
            401,
          ),
        ),
    );
    await router.navigate("/profile");
    render(<App />);
    const input = await screen.findByRole("textbox", { name: "Tên hiển thị" });
    await user.clear(input);
    await user.type(input, "Tên sau khi hết phiên");
    await user.click(screen.getByRole("button", { name: "Lưu tên hiển thị" }));

    expect(await screen.findByRole("heading", { name: "Bạn chưa đăng nhập" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Đăng nhập lại với Google" })).toBeVisible();
    expect(screen.queryByText(originalProfile.displayName)).not.toBeInTheDocument();
    expect(screen.queryByText(originalProfile.email)).not.toBeInTheDocument();
  });

  it("persists the profile theme choice and logs out with CSRF", async () => {
    const user = userEvent.setup();
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse(originalProfile, 200, { "X-CSRF-TOKEN": "profile-csrf" }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValue(
        jsonResponse(
          { title: "Authentication required", status: 401, errorCode: "AUTH_REQUIRED" },
          401,
        ),
      );
    vi.stubGlobal("fetch", fetchMock);
    await router.navigate("/profile");
    render(<App />);
    await screen.findByRole("heading", { name: "Hồ sơ của bạn" });

    await user.click(screen.getByRole("button", { name: "Sáng" }));
    expect(document.documentElement).toHaveAttribute("data-theme", "light");
    expect(window.localStorage.getItem("zeno-theme")).toBe("light");

    await user.click(screen.getByRole("button", { name: "Đăng xuất" }));

    expect(router.state.location.pathname).toBe("/");
    const [, logoutInit] = fetchMock.mock.calls[1] as [string, RequestInit];
    const logoutHeaders = new Headers(logoutInit.headers);
    expect(logoutInit.method).toBe("POST");
    expect(logoutHeaders.get("X-CSRF-TOKEN")).toBe("profile-csrf");
    expect(screen.queryByText(originalProfile.email)).not.toBeInTheDocument();
  });
});
