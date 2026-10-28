import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import {
  adminReportingApi,
  adminReportingQueryKeys,
  type AuditCriteria,
} from "@/features/admin-reporting/admin-reporting-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const initialCriteria: AuditCriteria = { page: 1, pageSize: 30 };

export function AdminAuditPage() {
  const profile = useProfileQuery();
  const viewerKey = profile.data?.id ?? "unresolved";
  const [draft, setDraft] = useState<AuditCriteria>(initialCriteria);
  const [criteria, setCriteria] = useState<AuditCriteria>(initialCriteria);
  const audit = useQuery({
    queryKey: adminReportingQueryKeys.audit(viewerKey, criteria),
    queryFn: () => adminReportingApi.audit(criteria),
    enabled: Boolean(profile.data?.id),
  });

  function update(field: keyof AuditCriteria, value: string | number) {
    setDraft((current) => ({ ...current, [field]: value || undefined }));
  }

  function applyFilters() {
    setCriteria({ ...draft, page: 1 });
  }

  function goToPage(page: number) {
    setCriteria((current) => ({ ...current, page }));
  }

  return <section className="admin-workspace audit-page">
    <header><p className="eyebrow">Quản trị hệ thống</p><h1>Nhật ký kiểm toán</h1><p>Chỉ hiển thị thay đổi đã được redaction; không hiển thị dữ liệu ngân hàng hoặc secret.</p></header>
    <div className="finance-filters">
      <label>Từ ngày<input type="date" value={draft.from ?? ""} onChange={(event) => update("from", event.target.value)} /></label>
      <label>Đến ngày<input type="date" value={draft.to ?? ""} onChange={(event) => update("to", event.target.value)} /></label>
      <label>Actor ID<input value={draft.actorId ?? ""} onChange={(event) => update("actorId", event.target.value)} /></label>
      <label>Action<input maxLength={120} value={draft.action ?? ""} onChange={(event) => update("action", event.target.value)} /></label>
      <label>Target type<input maxLength={120} value={draft.targetType ?? ""} onChange={(event) => update("targetType", event.target.value)} /></label>
      <label>Target ID<input value={draft.targetId ?? ""} onChange={(event) => update("targetId", event.target.value)} /></label>
      <label>Số dòng<select value={draft.pageSize} onChange={(event) => update("pageSize", Number(event.target.value))}><option value={30}>30</option><option value={50}>50</option><option value={100}>100</option></select></label>
      <Button onClick={applyFilters}>Lọc</Button>
      <Button onClick={() => { setDraft(initialCriteria); setCriteria(initialCriteria); }} variant="outline">Xóa bộ lọc</Button>
    </div>

    {audit.isPending ? <LoadingState title="Đang tải audit log" /> : null}
    {audit.isError ? <ApiErrorState error={audit.error} onRetry={() => void audit.refetch()} /> : null}
    {audit.data?.items.length === 0 ? <EmptyState title="Không có audit log" description="Không có bản ghi phù hợp bộ lọc và khoảng ngày." /> : null}
    {audit.data?.items.length ? <div aria-label="Bảng audit log" className="admin-table-scroll" tabIndex={0}><table className="admin-data-table">
      <thead><tr><th>Thời gian</th><th>Actor</th><th>Action</th><th>Target</th><th>Thay đổi đã redaction</th></tr></thead>
      <tbody>{audit.data.items.map((item) => <tr key={item.id}>
        <td>{new Date(item.occurredAt).toLocaleString("vi-VN")}</td><td><code>{item.actorId}</code></td>
        <td>{item.action}</td><td>{item.targetType}<br /><code>{item.targetId}</code></td>
        <td><pre className="audit-json">{formatJson(item.redactedChanges)}</pre></td>
      </tr>)}</tbody>
    </table></div> : null}
    {audit.data ? <nav aria-label="Phân trang audit" className="admin-actions">
      <Button disabled={criteria.page <= 1 || audit.isFetching} onClick={() => goToPage(criteria.page - 1)} variant="outline">Trang trước</Button>
      <span>Trang {audit.data.page}/{Math.max(audit.data.totalPages, 1)} · {audit.data.totalItems} bản ghi</span>
      <Button disabled={criteria.page >= audit.data.totalPages || audit.isFetching} onClick={() => goToPage(criteria.page + 1)} variant="outline">Trang sau</Button>
    </nav> : null}
  </section>;
}

function formatJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return "[redacted payload unavailable]";
  }
}
