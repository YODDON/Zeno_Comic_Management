import { createBrowserRouter } from "react-router-dom";
import { AdminLayout } from "@/app/layouts/AdminLayout";
import { UserLayout } from "@/app/layouts/UserLayout";
import { AdminFoundationPage } from "@/pages/AdminFoundationPage";
import { HomePage } from "@/pages/HomePage";
import { NotFoundPage } from "@/pages/NotFoundPage";
import { ProfilePage } from "@/pages/ProfilePage";
import { RoutePlaceholderPage } from "@/pages/RoutePlaceholderPage";

export const router = createBrowserRouter([
  {
    element: <UserLayout />,
    children: [
      { index: true, element: <HomePage /> },
      {
        path: "comics",
        element: (
          <RoutePlaceholderPage
            eyebrow="Kho truyện"
            title="Danh mục sẽ xuất hiện ở đây"
            description="Khung giao diện đã sẵn sàng; dữ liệu truyện sẽ được kết nối trong các task Catalog."
          />
        ),
      },
      {
        path: "library",
        element: (
          <RoutePlaceholderPage
            eyebrow="Tủ truyện"
            title="Lịch sử đọc chưa có"
            description="Sau khi đăng nhập và đọc truyện, tiến độ của bạn sẽ được hiển thị tại đây."
          />
        ),
      },
      { path: "profile", element: <ProfilePage /> },
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
