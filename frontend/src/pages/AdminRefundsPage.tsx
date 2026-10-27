import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";
import { adminFinanceApi, adminFinanceQueryKeys, type AdminRefundDetail } from "@/features/admin-finance/admin-finance-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState, SuccessState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const statuses = ["", "Requested", "UnderReview", "RecoveryReserved", "TransferPending", "NeedsReconciliation", "Completed", "Rejected", "ReservationReleased"];
const money = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });

type RefundCommand =
  | { kind: "review" }
  | { kind: "approve"; note: string }
  | { kind: "reject"; note: string }
  | { kind: "transfer"; bankReference: string; evidenceReference: string; outcome: string }
  | { kind: "complete" | "release"; note: string; transferRecordId: string };

export function AdminRefundsPage() {
  const client = useQueryClient();
  const profile = useProfileQuery();
  const viewerKey = profile.data?.id;
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const criteria = { search, status, page };
  const refunds = useQuery({ queryKey: adminFinanceQueryKeys.refunds(viewerKey ?? "unresolved", criteria), queryFn: () => adminFinanceApi.refunds(criteria), enabled: Boolean(viewerKey) });
  const detail = useQuery({ queryKey: adminFinanceQueryKeys.refund(viewerKey ?? "unresolved", selected), queryFn: () => adminFinanceApi.refund(selected!), enabled: Boolean(viewerKey && selected) });
  const keys = useRef(new Map<string, string>());
  const keyFor = (fingerprint: string) => {
    const current = keys.current.get(fingerprint);
    if (current) return current;
    const created = crypto.randomUUID(); keys.current.set(fingerprint, created); return created;
  };
  const command = useMutation({
    mutationFn: async ({ detail: current, command: next }: { detail: AdminRefundDetail; command: RefundCommand }) => {
      const id = current.refund.refundId; const version = current.refund.version;
      if (next.kind === "review") return adminFinanceApi.reviewRefund(id);
      if (next.kind === "approve") return adminFinanceApi.approveRefund(id, version, next.note, keyFor(`approve:${id}:${version}:${next.note}`));
      if (next.kind === "reject") return adminFinanceApi.rejectRefund(id, version, next.note);
      if (next.kind === "transfer") return adminFinanceApi.recordTransfer(id, version, next.bankReference, next.evidenceReference, next.outcome, keyFor(`transfer:${id}:${version}:${next.bankReference}:${next.evidenceReference}:${next.outcome}`));
      return adminFinanceApi.resolveTransfer(id, next.kind, version, next.transferRecordId, next.note, keyFor(`${next.kind}:${id}:${version}:${next.transferRecordId}:${next.note}`));
    },
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: ["admin", "finance", viewerKey, "refunds"] }),
        client.invalidateQueries({ queryKey: ["admin", "finance", viewerKey, "refund", selected] }),
        client.invalidateQueries({ queryKey: ["admin", "finance", viewerKey, "ledger"] }),
      ]);
    },
  });

  return <section className="admin-workspace finance-page"><header><p className="eyebrow">Tài chính</p><h1>Hoàn tiền</h1><p>Chỉ hoàn toàn bộ TopUp gốc. Duyệt giữ đúng PaidCoinLot; bằng chứng chuyển khoản phải được xác nhận độc lập trước khi hoàn tất.</p></header><div className="finance-filters"><label>Tìm mã đơn hoặc người dùng<input maxLength={200} value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} /></label><label>Trạng thái<select value={status} onChange={(event) => { setStatus(event.target.value); setPage(1); }}>{statuses.map((value) => <option key={value || "all"} value={value}>{value || "Tất cả"}</option>)}</select></label></div>{refunds.isPending ? <LoadingState title="Đang tải yêu cầu hoàn tiền" /> : null}{refunds.isError ? <ApiErrorState error={refunds.error} onRetry={() => void refunds.refetch()} /> : null}{refunds.data?.items.length === 0 ? <EmptyState title="Không có yêu cầu" description="Không có hoàn tiền phù hợp bộ lọc." /> : null}{refunds.data?.items.length ? <div className="admin-table-scroll"><table className="admin-data-table"><thead><tr><th>Mã đơn</th><th>Người dùng</th><th>Tiền gốc / thực nhận</th><th>Coin cần thu hồi</th><th>Trạng thái</th></tr></thead><tbody>{refunds.data.items.map((refund) => <tr key={refund.refundId}><td><button className="table-link" onClick={() => { setSelected(refund.refundId); command.reset(); }}>{refund.orderCode}</button></td><td>{refund.userDisplayName}<small>{refund.userEmail}</small></td><td>{money.format(refund.originalAmountVnd)}<small>Net {money.format(refund.netRefundVnd)} · phí {money.format(refund.feeVnd)}</small></td><td>{refund.requiredRecoveryCoins}</td><td><span className={`status-badge status-${refund.status.toLowerCase()}`}>{refund.status}</span></td></tr>)}</tbody></table></div> : null}{refunds.data && refunds.data.totalPages > 1 ? <nav aria-label="Phân trang hoàn tiền" className="admin-actions"><Button disabled={page <= 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {page}/{refunds.data.totalPages}</span><Button disabled={page >= refunds.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}{selected ? <aside aria-label="Không gian xử lý hoàn tiền" className="finance-detail refund-workspace"><div className="finance-detail-heading"><h2>Hồ sơ hoàn tiền</h2><Button onClick={() => setSelected(null)} variant="ghost">Đóng</Button></div>{detail.isPending ? <LoadingState title="Đang tải hồ sơ" /> : null}{detail.isError ? <ApiErrorState error={detail.error} onRetry={() => void detail.refetch()} /> : null}{detail.data ? <RefundWorkspace detail={detail.data} disabled={command.isPending} onCommand={(next) => command.mutate({ detail: detail.data, command: next })} /> : null}{command.isPending ? <p aria-live="polite" role="status">Đang ghi nhận nghiệp vụ nguyên tử…</p> : null}{command.isSuccess ? <SuccessState title="Đã cập nhật" description="Trạng thái authoritative đã được tải lại từ máy chủ." /> : null}{command.isError ? <ApiErrorState error={command.error} onRetry={() => command.reset()} /> : null}</aside> : null}</section>;
}

type RefundWorkspaceProps = { detail: AdminRefundDetail; disabled: boolean; onCommand: (command: RefundCommand) => void };

function RefundWorkspace(props: RefundWorkspaceProps) {
  return <RefundWorkspaceState key={props.detail.refund.refundId} {...props} />;
}

function RefundWorkspaceState({ detail, disabled, onCommand }: RefundWorkspaceProps) {
  const [note, setNote] = useState("");
  const [bankReference, setBankReference] = useState("");
  const [evidenceReference, setEvidenceReference] = useState("");
  const [outcome, setOutcome] = useState("Unknown");
  const refund = detail.refund; const lot = detail.originalPaidCoinLot;
  const confirm = (message: string, command: RefundCommand) => { if (window.confirm(message)) onCommand(command); };
  return <><dl className="finance-summary"><div><dt>Trạng thái</dt><dd>{refund.status} · v{refund.version}</dd></div><div><dt>Lý do</dt><dd>{detail.reason}</dd></div><div><dt>Người nhận đã bảo vệ</dt><dd>{detail.recipient.bankCode} · {detail.recipient.accountNumberMasked} · {detail.recipient.accountHolderMasked}</dd></div><div><dt>Giá trị</dt><dd>{money.format(refund.originalAmountVnd)} − {money.format(refund.feeVnd)} = <strong>{money.format(refund.netRefundVnd)}</strong></dd></div></dl><section><h3>PaidCoinLot gốc</h3>{lot ? <div className="lot-grid"><span>Granted <strong>{lot.grantedCoins}</strong></span><span>Spent <strong>{lot.spentCoins}</strong></span><span>Refunded <strong>{lot.refundedCoins}</strong></span><span>Reserved <strong>{lot.reservedCoins}</strong></span><span>Available <strong>{lot.availableCoins}</strong></span></div> : <p role="alert">Không tìm thấy lot authoritative. Không được duyệt.</p>}</section>{detail.reservation ? <p><strong>Reservation:</strong> {detail.reservation.status} · {detail.reservation.amountCoins} coin · {detail.reservation.reservationId}</p> : null}{detail.transfer ? <section><h3>Bằng chứng chuyển khoản</h3><p>{detail.transfer.status} · {money.format(detail.transfer.amountVnd)}</p><p>Bank ref: {detail.transfer.bankReference}<br />Evidence: {detail.transfer.evidenceReference}</p><p><strong>Ghi nhận chưa đồng nghĩa đã xác minh.</strong></p></section> : null}<div className="refund-actions">{refund.status === "Requested" ? <Button disabled={disabled} onClick={() => onCommand({ kind: "review" })}>Bắt đầu xem xét</Button> : null}{refund.status === "UnderReview" ? <><label>Ghi chú quyết định<textarea maxLength={1000} required value={note} onChange={(event) => setNote(event.target.value)} /></label><Button disabled={disabled || !note.trim() || !lot} onClick={() => confirm(`Xác nhận giữ ${refund.requiredRecoveryCoins} coin từ đúng PaidCoinLot gốc?`, { kind: "approve", note: note.trim() })}>Duyệt và giữ coin</Button><Button disabled={disabled || !note.trim()} onClick={() => confirm("Từ chối yêu cầu này? Người dùng không thể tạo lại yêu cầu cho TopUp này.", { kind: "reject", note: note.trim() })} variant="outline">Từ chối</Button></> : null}{refund.status === "RecoveryReserved" ? <form onSubmit={(event) => { event.preventDefault(); confirm("Chỉ ghi nhận bằng chứng báo cáo; thao tác này chưa hoàn tất hoàn tiền. Tiếp tục?", { kind: "transfer", bankReference: bankReference.trim(), evidenceReference: evidenceReference.trim(), outcome }); }}><label>Mã tham chiếu ngân hàng<input maxLength={128} required value={bankReference} onChange={(event) => setBankReference(event.target.value)} /></label><label>Tham chiếu bằng chứng<input maxLength={500} required value={evidenceReference} onChange={(event) => setEvidenceReference(event.target.value)} /></label><label>Kết quả được báo cáo<select value={outcome} onChange={(event) => setOutcome(event.target.value)}><option value="Unknown">Chưa rõ</option><option value="Succeeded">Báo thành công</option><option value="Failed">Báo thất bại</option></select></label><Button disabled={disabled || !bankReference.trim() || !evidenceReference.trim()} type="submit">Ghi nhận bằng chứng</Button></form> : null}{(refund.status === "TransferPending" || refund.status === "NeedsReconciliation") && detail.transfer ? <><label>Ghi chú xác nhận độc lập<textarea maxLength={1000} required value={note} onChange={(event) => setNote(event.target.value)} /></label><Button disabled={disabled || !note.trim() || detail.transfer.status === "ReportedFailed"} onClick={() => confirm("Xác nhận bằng chứng thành công và hoàn tất hoàn tiền? Coin đã giữ sẽ được chuyển sang refunded.", { kind: "complete", note: note.trim(), transferRecordId: detail.transfer!.transferRecordId })}>Xác nhận thành công</Button><Button disabled={disabled || !note.trim() || detail.transfer.status === "ReportedSucceeded"} onClick={() => confirm("Xác nhận chuyển khoản thất bại và trả coin đã giữ về số dư khả dụng?", { kind: "release", note: note.trim(), transferRecordId: detail.transfer!.transferRecordId })} variant="outline">Xác nhận thất bại / nhả giữ</Button></> : null}</div><section><h3>Dòng thời gian audit</h3>{detail.auditTimeline.length === 0 ? <p>Chưa có audit transition.</p> : <ol className="finance-timeline">{detail.auditTimeline.map((audit) => <li key={audit.auditId}><strong>{audit.action}</strong><br /><time dateTime={audit.occurredAt}>{new Date(audit.occurredAt).toLocaleString("vi-VN")}</time></li>)}</ol>}</section></>;
}
