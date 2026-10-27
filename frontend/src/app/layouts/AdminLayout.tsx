import { BookOpen, Layers, LayoutDashboard, Menu, Star, Tags, Users, X } from "lucide-react";
import { useState, type KeyboardEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { NavLink, Outlet } from "react-router-dom";
import { Button } from "@/shared/ui/Button";
import { getProfile, profileQueryKey } from "@/features/profile/profile-api";

const adminNavigation = [
  { to: "/admin", label: "Tổng quan", icon: LayoutDashboard, end: true },
  { to: "/admin/comics", label: "Truyện", icon: BookOpen },
  { to: "/admin/chapters", label: "Chương", icon: Layers },
  { to: "/admin/genres", label: "Thể loại", icon: Tags },
  { to: "/admin/authors", label: "Tác giả", icon: Users },
  { to: "/admin/translation-groups", label: "Nhóm dịch", icon: Users },
  { to: "/admin/featured", label: "Nổi bật", icon: Star },
];

export function AdminLayout() {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const profile = useQuery({ queryKey: profileQueryKey, queryFn: getProfile, retry: false });

  function handleMobileNavigationKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key !== "Escape" || !mobileMenuOpen) return;
    setMobileMenuOpen(false);
    event.currentTarget.querySelector<HTMLButtonElement>("[data-admin-menu-trigger]")?.focus();
  }

  if (profile.isLoading) return <main><p role="status">Đang xác minh quyền quản trị…</p></main>;
  if (profile.isError || profile.data?.role !== "Admin")
    return <main><p role="alert">Không thể xác minh quyền quản trị.</p></main>;

  return <div className="admin-shell min-h-screen">
    <a className="skip-link" href="#admin-content">Bỏ qua điều hướng</a>
    <aside className="admin-sidebar">
      <NavLink aria-label="Zeno Admin" className="admin-brand" to="/admin">
        <span className="brand-registration" aria-hidden="true" /><span>Zeno</span><small>Admin</small>
      </NavLink>
      <nav aria-label="Điều hướng quản trị" className="admin-navigation">
        {adminNavigation.map(({ icon: Icon, ...item }) => <NavLink
          className={({ isActive }) => isActive ? "admin-nav-link admin-nav-link-active" : "admin-nav-link"}
          end={item.end} key={item.to} to={item.to}><Icon aria-hidden="true" />{item.label}</NavLink>)}
      </nav>
      <p className="admin-sidebar-note">Khu quản trị luôn dùng giao diện sáng.</p>
    </aside>
    <div className="admin-main">
      <div className="admin-mobile-region" onKeyDown={handleMobileNavigationKeyDown}>
        <header className="admin-mobile-header"><span>Zeno Admin</span><Button
          aria-controls="admin-mobile-navigation" aria-expanded={mobileMenuOpen}
          aria-label={mobileMenuOpen ? "Đóng menu quản trị" : "Mở menu quản trị"}
          data-admin-menu-trigger onClick={() => setMobileMenuOpen((open) => !open)} size="icon" variant="outline">
          {mobileMenuOpen ? <X aria-hidden="true" /> : <Menu aria-hidden="true" />}
        </Button></header>
        {mobileMenuOpen ? <nav aria-label="Điều hướng quản trị di động" className="admin-mobile-navigation" id="admin-mobile-navigation">
          {adminNavigation.map(({ icon: Icon, ...item }) => <NavLink
            className={({ isActive }) => isActive ? "admin-nav-link admin-nav-link-active" : "admin-nav-link"}
            end={item.end} key={item.to} onClick={() => setMobileMenuOpen(false)} to={item.to}>
            <Icon aria-hidden="true" />{item.label}</NavLink>)}
        </nav> : null}
      </div>
      <main id="admin-content"><Outlet /></main>
    </div>
  </div>;
}
