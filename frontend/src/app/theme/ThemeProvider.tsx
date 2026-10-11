import {
  type ReactNode,
  useCallback,
  useEffect,
  useMemo,
  useState,
} from "react";
import { ThemeContext, type Theme } from "@/app/theme/theme-context";

const themeStorageKey = "zeno-theme";

function readStoredTheme(): Theme {
  if (typeof window === "undefined") {
    return "dark";
  }

  return window.localStorage.getItem(themeStorageKey) === "light" ? "light" : "dark";
}

interface ThemeProviderProps {
  children: ReactNode;
}

export function ThemeProvider({ children }: ThemeProviderProps) {
  const [theme, setThemeState] = useState<Theme>(readStoredTheme);

  const setTheme = useCallback((nextTheme: Theme) => {
    setThemeState(nextTheme);
    window.localStorage.setItem(themeStorageKey, nextTheme);
  }, []);

  const toggleTheme = useCallback(() => {
    setTheme(theme === "dark" ? "light" : "dark");
  }, [setTheme, theme]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
  }, [theme]);

  const value = useMemo(
    () => ({ theme, setTheme, toggleTheme }),
    [setTheme, theme, toggleTheme],
  );

  return <ThemeContext value={value}>{children}</ThemeContext>;
}
