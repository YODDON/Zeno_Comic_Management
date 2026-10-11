import { BookOpen, Library, Menu, X } from "lucide-react";
import { useState } from "react";
import { NavLink, Outlet } from "react-router-dom";
import { ThemeToggle } from "@/app/theme/ThemeToggle";
import { Button } from "@/shared/ui/Button";
import { cn } from "@/shared/lib/cn";

const userNavigation = [
  { to: "/", label: "Trang chủ", end: true },
  { to: "/comics", label: "Kho truyện" },
  { to: "/library", label: "Tủ truyện" },
];

export function UserLayout() {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  return (
    <div className="user-shell min-h-screen">
      <a className="skip-link" href="#main-content">
        Bỏ qua điều hướng
      </a>
      <header className="site-header">
        <div className="shell-container header-row">
          <NavLink aria-label="Zeno — Trang chủ" className="brand-mark" to="/">
            <span className="brand-registration" aria-hidden="true" />
            <span>Zeno</span>
          </NavLink>

          <nav aria-label="Điều hướng chính" className="desktop-navigation">
            {userNavigation.map((item) => (
              <NavLink
                className={({ isActive }) => cn("nav-link", isActive && "nav-link-active")}
                end={item.end}
                key={item.to}
                onClick={() => setMobileMenuOpen(false)}
                to={item.to}
              >
                {item.label}
              </NavLink>
            ))}
          </nav>

          <div className="header-actions">
            <ThemeToggle />
            <Button
              aria-controls="mobile-navigation"
              aria-expanded={mobileMenuOpen}
              aria-label={mobileMenuOpen ? "Đóng menu" : "Mở menu"}
              className="mobile-menu-button"
              onClick={() => setMobileMenuOpen((open) => !open)}
              size="icon"
              variant="outline"
            >
              {mobileMenuOpen ? <X aria-hidden="true" /> : <Menu aria-hidden="true" />}
            </Button>
          </div>
        </div>

        {mobileMenuOpen ? (
          <nav aria-label="Điều hướng di động" className="mobile-navigation" id="mobile-navigation">
            {userNavigation.map((item) => (
              <NavLink
                className={({ isActive }) => cn("mobile-nav-link", isActive && "nav-link-active")}
                end={item.end}
                key={item.to}
                to={item.to}
              >
                {item.to === "/comics" ? <BookOpen aria-hidden="true" /> : null}
                {item.to === "/library" ? <Library aria-hidden="true" /> : null}
                {item.label}
              </NavLink>
            ))}
          </nav>
        ) : null}
      </header>

      <main id="main-content">
        <Outlet />
      </main>

      <footer className="site-footer">
        <div className="shell-container footer-row">
          <span>Zeno Comic Management</span>
          <span className="footer-note">Khung đọc tiếng Việt • MVP</span>
        </div>
      </footer>
    </div>
  );
}
