import { MoonStar, Sun } from "lucide-react";
import { useTheme } from "@/app/theme/theme-context";
import { Button } from "@/shared/ui/Button";

export function ThemeToggle() {
  const { theme, toggleTheme } = useTheme();
  const nextThemeLabel = theme === "dark" ? "sáng" : "tối";

  return (
    <Button
      aria-label={`Chuyển sang giao diện ${nextThemeLabel}`}
      onClick={toggleTheme}
      size="icon"
      variant="ghost"
    >
      {theme === "dark" ? <Sun aria-hidden="true" /> : <MoonStar aria-hidden="true" />}
    </Button>
  );
}
