import { LayoutDashboard, PanelLeft } from "lucide-react";
import { NavLink, Outlet } from "react-router-dom";

const adminNavigation = [
  { to: "/admin", label: "Tổng quan", icon: LayoutDashboard, end: true },
];

export function AdminLayout() {
  return (
    <div className="admin-shell min-h-screen">
      <a className="skip-link" href="#admin-content">
        Bỏ qua điều hướng
      </a>
      <aside className="admin-sidebar">
        <NavLink aria-label="Zeno Admin" className="admin-brand" to="/admin">
          <span className="brand-registration" aria-hidden="true" />
          <span>Zeno</span>
          <small>Admin</small>
        </NavLink>
        <nav aria-label="Điều hướng quản trị" className="admin-navigation">
          {adminNavigation.map(({ icon: Icon, ...item }) => (
            <NavLink
              className={({ isActive }) =>
                isActive ? "admin-nav-link admin-nav-link-active" : "admin-nav-link"
              }
              end={item.end}
              key={item.to}
              to={item.to}
            >
              <Icon aria-hidden="true" />
              {item.label}
            </NavLink>
          ))}
        </nav>
        <p className="admin-sidebar-note">Khu quản trị luôn dùng giao diện sáng.</p>
      </aside>

      <div className="admin-main">
        <header className="admin-mobile-header">
          <PanelLeft aria-hidden="true" />
          <span>Zeno Admin</span>
        </header>
        <main id="admin-content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
