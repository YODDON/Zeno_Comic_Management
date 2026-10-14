import { createBrowserRouter } from "react-router-dom";
import { AdminLayout } from "@/app/layouts/AdminLayout";
import { UserLayout } from "@/app/layouts/UserLayout";
import { AdminFoundationPage } from "@/pages/AdminFoundationPage";
import { CatalogPage } from "@/pages/CatalogPage";
import { ComicDetailPage } from "@/pages/ComicDetailPage";
import { HomePage } from "@/pages/HomePage";
import { LibraryPage } from "@/pages/LibraryPage";
import { NotFoundPage } from "@/pages/NotFoundPage";
import { ProfilePage } from "@/pages/ProfilePage";
import { ReaderPage } from "@/pages/ReaderPage";
import { RoutePlaceholderPage } from "@/pages/RoutePlaceholderPage";

export const router = createBrowserRouter([
  {
    element: <UserLayout />,
    children: [
      { index: true, element: <HomePage /> },
      { path: "comics", element: <CatalogPage /> },
      { path: "comics/:slug", element: <ComicDetailPage /> },
      { path: "chapters/:chapterId/read", element: <ReaderPage /> },
      { path: "library", element: <LibraryPage /> },
      { path: "profile", element: <ProfilePage /> },
      {
        path: "wallet",
        element: (
          <RoutePlaceholderPage
            eyebrow="Ví"
            title="Ví sẽ xuất hiện ở đây"
            description="Liên kết hồ sơ đã sẵn sàng; số dư và lịch sử giao dịch thuộc các task Wallet sau I03."
          />
        ),
      },
      {
        path: "vip",
        element: (
          <RoutePlaceholderPage
            eyebrow="VIP"
            title="Gói VIP sẽ xuất hiện ở đây"
            description="Liên kết hồ sơ đã sẵn sàng; checkout và quyền lợi VIP không được triển khai trong I03."
          />
        ),
      },
      { path: "*", element: <NotFoundPage /> },
    ],
  },
  {
    path: "admin",
    element: <AdminLayout />,
    children: [
      { index: true, element: <AdminFoundationPage /> },
      { path: "*", element: <NotFoundPage admin /> },
    ],
  },
]);
