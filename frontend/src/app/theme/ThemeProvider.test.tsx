import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@/app/theme/ThemeProvider";
import { ThemeToggle } from "@/app/theme/ThemeToggle";

describe("ThemeProvider", () => {
  it("uses dark by default and persists a light preference", async () => {
    const user = userEvent.setup();

    render(
      <ThemeProvider>
        <ThemeToggle />
      </ThemeProvider>,
    );

    expect(document.documentElement).toHaveAttribute("data-theme", "dark");

    await user.click(screen.getByRole("button", { name: "Chuyển sang giao diện sáng" }));

    expect(document.documentElement).toHaveAttribute("data-theme", "light");
    expect(window.localStorage.getItem("zeno-theme")).toBe("light");
  });

  it("restores the saved preference", () => {
    window.localStorage.setItem("zeno-theme", "light");

    render(
      <ThemeProvider>
        <ThemeToggle />
      </ThemeProvider>,
    );

    expect(document.documentElement).toHaveAttribute("data-theme", "light");
  });
});
