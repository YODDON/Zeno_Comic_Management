import { Info } from "lucide-react";

export function AdminFoundationPage() {
  return (
    <div className="admin-page">
      <div className="admin-page-heading">
        <p className="eyebrow">Nền tảng quản trị</p>
        <h1>Tổng quan</h1>
        <p>Các chỉ số sẽ chỉ hiển thị sau khi có API được phân quyền.</p>
      </div>
      <div className="admin-notice" role="status">
        <Info aria-hidden="true" />
        <div>
          <strong>Chưa kết nối dữ liệu quản trị</strong>
          <p>F05 chỉ thiết lập layout. Không có quyền hay dữ liệu giả lập.</p>
        </div>
      </div>
    </div>
  );
}
