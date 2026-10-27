import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { adminFinanceApi, adminFinanceQueryKeys } from "@/features/admin-finance/admin-finance-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const operationTypes = ["", "TopUpCredit", "ChapterUnlock", "MissionReward", "RefundReserve", "RefundComplete", "RefundRelease"];

export function AdminLedgerPage() {
  const profile = useProfileQuery();
  const viewerKey = profile.data?.id;
  const [search, setSearch] = useState("");
  const [operationType, setOperationType] = useState("");
  const [page, setPage] = useState(1);
  const criteria = { search, operationType, page };
  const ledger = useQuery({ queryKey: adminFinanceQueryKeys.ledger(viewerKey ?? "unresolved", criteria), queryFn: () => adminFinanceApi.ledger(criteria), enabled: Boolean(viewerKey) });
  return <section className="admin-workspace finance-page"><header><p className="eyebrow">Tài chính</p><h1>Sổ cái Wallet</h1><p>Chế độ chỉ đọc. Mỗi dòng là một ledger leg bất biến; Admin không thể chỉnh số dư.</p></header><div className="finance-filters"><label>Tìm người dùng<input maxLength={200} value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} /></label><label>Loại nghiệp vụ<select value={operationType} onChange={(event) => { setOperationType(event.target.value); setPage(1); }}>{operationTypes.map((value) => <option key={value || "all"} value={value}>{value || "Tất cả"}</option>)}</select></label></div>{ledger.isPending ? <LoadingState title="Đang tải sổ cái" /> : null}{ledger.isError ? <ApiErrorState error={ledger.error} onRetry={() => void ledger.refetch()} /> : null}{ledger.data?.items.length === 0 ? <EmptyState title="Sổ cái trống" description="Không có ledger leg phù hợp bộ lọc." /> : null}{ledger.data?.items.length ? <div className="admin-table-scroll"><table className="admin-data-table"><thead><tr><th>Thời gian</th><th>Người dùng</th><th>Nghiệp vụ</th><th>Nguồn</th><th>Coin/Bucket</th><th>Biến động</th></tr></thead><tbody>{ledger.data.items.map((entry) => <tr key={entry.entryId}><td><time dateTime={entry.createdAt}>{new Date(entry.createdAt).toLocaleString("vi-VN")}</time></td><td>{entry.userDisplayName}<small>{entry.userEmail}</small></td><td>{entry.operationType}<small>{entry.operationId}</small></td><td>{entry.sourceType}<small>{entry.sourceId}</small></td><td>{entry.coinType} / {entry.bucket}</td><td className={entry.deltaCoins > 0 ? "coin-positive" : "coin-negative"}>{entry.deltaCoins > 0 ? "+" : ""}{entry.deltaCoins}</td></tr>)}</tbody></table></div> : null}{ledger.data && ledger.data.totalPages > 1 ? <nav aria-label="Phân trang sổ cái" className="admin-actions"><Button disabled={page <= 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {page}/{ledger.data.totalPages}</span><Button disabled={page >= ledger.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}</section>;
}
