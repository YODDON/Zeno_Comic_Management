import { Link } from "react-router-dom";
import { Button } from "@/shared/ui/Button";

interface NotFoundPageProps {
  admin?: boolean;
}

export function NotFoundPage({ admin = false }: NotFoundPageProps) {
  return (
    <div className={admin ? "admin-page not-found" : "shell-container page-section not-found"}>
      <p className="error-number" aria-hidden="true">
        404
      </p>
      <h1>Không tìm thấy trang</h1>
      <p>Đường dẫn này không tồn tại hoặc chưa được mở.</p>
      <Button asChild="link">
        <Link to={admin ? "/admin" : "/"}>Quay về {admin ? "tổng quan" : "trang chủ"}</Link>
      </Button>
    </div>
  );
}
