export function AdminSettingsPage() {
  return <section className="admin-workspace admin-editor-stack">
    <header><p className="eyebrow">Hệ thống</p><h1>Cài đặt</h1><p>Các policy MVP chưa được phê duyệt chỉnh sửa được hiển thị chỉ đọc.</p></header>
    <div className="settings-grid">
      <article><h2>Đọc truyện</h2><dl><div><dt>Tiến độ tối thiểu</dt><dd>90%</dd></div><div><dt>Thời gian active tối thiểu</dt><dd>15 giây</dd></div><div><dt>Session tuyệt đối</dt><dd>30 phút</dd></div></dl></article>
      <article><h2>Nhiệm vụ</h2><dl><div><dt>Bonus tối đa/ngày</dt><dd>300</dd></div><div><dt>Múi giờ</dt><dd>Asia/Ho_Chi_Minh</dd></div></dl><p>Giới hạn 300 là policy cố định, không có nút sửa.</p></article>
      <article><h2>Nhà cung cấp</h2><p>SePay và Cloudinary protected delivery vẫn là <strong>Verification Required</strong>. Secrets không được hiển thị hoặc sửa trong UI.</p></article>
    </div>
  </section>;
}
