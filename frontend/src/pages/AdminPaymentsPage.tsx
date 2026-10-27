import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { adminFinanceApi, adminFinanceQueryKeys } from "@/features/admin-finance/admin-finance-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const money = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });

export function AdminPaymentsPage() {
  const profile = useProfileQuery();
  const viewerKey = profile.data?.id;
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [type, setType] = useState("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const criteria = { search, status, type, page };
  const payments = useQuery({ queryKey: adminFinanceQueryKeys.payments(viewerKey ?? "unresolved", criteria), queryFn: () => adminFinanceApi.payments(criteria), enabled: Boolean(viewerKey) });
  const detail = useQuery({ queryKey: adminFinanceQueryKeys.payment(viewerKey ?? "unresolved", selected), queryFn: () => adminFinanceApi.payment(selected!), enabled: Boolean(viewerKey && selected) });

  return <section className="admin-workspace finance-page">
    <header><p className="eyebrow">Tài chính</p><h1>Thanh toán</h1><p>Đối chiếu PaymentOrder với bằng chứng nhà cung cấp. Trạng thái trình duyệt không xác nhận thanh toán.</p></header>
    <div className="finance-filters">
      <label>Tìm mã đơn hoặc người dùng<input maxLength={200} value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} /></label>
      <label>Loại<select value={type} onChange={(event) => { setType(event.target.value); setPage(1); }}><option value="">Tất cả</option><option value="TopUp">TopUp</option><option value="VipPurchase">VIP</option></select></label>
      <label>Trạng thái<select value={status} onChange={(event) => { setStatus(event.target.value); setPage(1); }}><option value="">Tất cả</option><option value="Pending">Pending</option><option value="Expired">Expired</option><option value="Succeeded">Succeeded</option></select></label>
    </div>
    {payments.isPending ? <LoadingState title="Đang tải thanh toán" /> : null}
    {payments.isError ? <ApiErrorState error={payments.error} onRetry={() => void payments.refetch()} /> : null}
    {payments.data?.items.length === 0 ? <EmptyState title="Chưa có thanh toán" description="Không có đơn phù hợp bộ lọc." /> : null}
    {payments.data?.items.length ? <div className="admin-table-scroll"><table className="admin-data-table"><thead><tr><th>Mã đơn</th><th>Người dùng</th><th>Loại</th><th>Số tiền</th><th>Trạng thái</th><th>Đối soát</th></tr></thead><tbody>{payments.data.items.map((item) => <tr key={item.paymentId}><td><button className="table-link" onClick={() => setSelected(item.paymentId)}>{item.orderCode}</button></td><td>{item.userDisplayName}<small>{item.userEmail}</small></td><td>{item.orderType}</td><td>{money.format(item.amountVnd)}</td><td><span className={`status-badge status-${item.orderStatus.toLowerCase()}`}>{item.orderStatus}</span></td><td>{item.reconciliationReason ?? "—"}</td></tr>)}</tbody></table></div> : null}
    {payments.data && payments.data.totalPages > 1 ? <nav aria-label="Phân trang thanh toán" className="admin-actions"><Button disabled={page <= 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {page}/{payments.data.totalPages}</span><Button disabled={page >= payments.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}
    {selected ? <aside aria-label="Chi tiết thanh toán" className="finance-detail"><div className="finance-detail-heading"><h2>Chi tiết thanh toán</h2><Button onClick={() => setSelected(null)} variant="ghost">Đóng</Button></div>{detail.isPending ? <LoadingState title="Đang tải chi tiết" /> : null}{detail.isError ? <ApiErrorState error={detail.error} onRetry={() => void detail.refetch()} /> : null}{detail.data ? <><dl className="finance-summary"><div><dt>Mã đơn</dt><dd>{detail.data.payment.orderCode}</dd></div><div><dt>Provider/Fulfillment</dt><dd>{detail.data.payment.providerPaymentStatus} / {detail.data.payment.fulfillmentStatus}</dd></div><div><dt>Hết hạn</dt><dd>{new Date(detail.data.payment.expiresAt).toLocaleString("vi-VN")}</dd></div></dl><h3>Bằng chứng nhà cung cấp</h3>{detail.data.providerTransactions.length === 0 ? <p>Chưa có giao dịch nhà cung cấp.</p> : <ul className="finance-timeline">{detail.data.providerTransactions.map((transaction) => <li key={transaction.transactionId}><strong>{transaction.processingStatus}</strong> · {money.format(transaction.amountVnd)}<br /><span>{transaction.provider} #{transaction.providerTransactionId} · tài khoản ••••{transaction.accountNumberLast4}</span>{transaction.reconciliationReason ? <p role="alert">Cần đối soát: {transaction.reconciliationReason}</p> : null}</li>)}</ul>}</> : null}</aside> : null}
  </section>;
}
