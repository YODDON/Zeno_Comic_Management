import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import {
  adminReportingApi,
  adminReportingQueryKeys,
  type ReportingRange,
} from "@/features/admin-reporting/admin-reporting-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const money = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });
const number = new Intl.NumberFormat("vi-VN");

export function AdminFoundationPage() {
  const profile = useProfileQuery();
  const viewerKey = profile.data?.id ?? "unresolved";
  const [draftFrom, setDraftFrom] = useState("");
  const [draftTo, setDraftTo] = useState("");
  const [range, setRange] = useState<ReportingRange>({});
  const dashboard = useQuery({
    queryKey: adminReportingQueryKeys.dashboard(viewerKey, range),
    queryFn: () => adminReportingApi.dashboard(range),
    enabled: Boolean(profile.data?.id),
  });

  return <section className="admin-workspace dashboard-page">
    <header className="admin-page-heading">
      <p className="eyebrow">Báo cáo quản trị</p>
      <h1>Tổng quan</h1>
      <p>Chỉ số authoritative theo ngày Việt Nam. Doanh thu là gross và không tự trừ hoàn tiền.</p>
    </header>

    <div className="finance-filters" aria-label="Khoảng ngày báo cáo">
      <label>Từ ngày<input type="date" value={draftFrom} onChange={(event) => setDraftFrom(event.target.value)} /></label>
      <label>Đến ngày<input type="date" value={draftTo} onChange={(event) => setDraftTo(event.target.value)} /></label>
      <Button onClick={() => setRange({ from: draftFrom || undefined, to: draftTo || undefined })}>Áp dụng</Button>
      <Button onClick={() => { setDraftFrom(""); setDraftTo(""); setRange({}); }} variant="outline">30 ngày mặc định</Button>
    </div>

    {dashboard.isPending ? <LoadingState title="Đang tải dashboard" /> : null}
    {dashboard.isError ? <ApiErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} /> : null}
    {dashboard.data ? <>
      <p className="dashboard-range">{dashboard.data.range.from} – {dashboard.data.range.to} · Asia/Ho_Chi_Minh</p>
      <div className="dashboard-grid">
        <Metric title="TopUp gross" value={money.format(dashboard.data.revenue.topUpGrossVnd)} />
        <Metric title="VIP gross" value={money.format(dashboard.data.revenue.vipGrossVnd)} />
        <Metric
          title="Hoàn tiền đã hoàn tất"
          value={number.format(dashboard.data.refunds.completedCount)}
          detail={`Gốc ${money.format(dashboard.data.refunds.originalAmountVnd)} · phí ${money.format(dashboard.data.refunds.feeVnd)} · net ${money.format(dashboard.data.refunds.netRefundVnd)}`}
        />
        <Metric title="Paid Coins đã phát hành" value={number.format(dashboard.data.coinsIssued.paidCoins)} />
        <Metric title="Bonus Coins đã cấp" value={number.format(dashboard.data.coinsIssued.bonusCoins)} />
        <Metric title="Phiên đọc đạt chuẩn" value={number.format(dashboard.data.reading.qualifiedSessionCount)} detail={`${number.format(dashboard.data.reading.distinctReaderCount)} độc giả`} />
        <Metric title="Mission receipts" value={number.format(dashboard.data.missions.actionReceiptCount)} />
        <Metric title="Reward granted / denied cap" value={`${number.format(dashboard.data.missions.grantedRewardCount)} / ${number.format(dashboard.data.missions.deniedDailyCapCount)}`} />
      </div>
      <div aria-label="Bảng chuỗi số liệu dashboard" className="admin-table-scroll" tabIndex={0}>
        <table className="admin-data-table">
          <caption>Chuỗi số liệu theo ngày Việt Nam</caption>
          <thead><tr><th>Ngày</th><th>TopUp</th><th>VIP</th><th>Refund hoàn tất</th><th>Paid / Bonus</th><th>Đọc đạt chuẩn</th><th>Mission</th></tr></thead>
          <tbody>{dashboard.data.dailySeries.map((point) => <tr key={point.date}>
            <td>{point.date}</td><td>{money.format(point.topUpGrossVnd)}</td><td>{money.format(point.vipGrossVnd)}</td>
            <td>
              {number.format(point.completedRefundCount)} yêu cầu<br />
              Gốc {money.format(point.refundOriginalAmountVnd)}<br />
              Phí {money.format(point.refundFeeVnd)} · net {money.format(point.refundNetVnd)}
            </td>
            <td>{number.format(point.paidCoinsIssued)} / {number.format(point.bonusCoinsGranted)}</td>
            <td>{number.format(point.qualifiedSessionCount)} phiên / {number.format(point.distinctReaderCount)} người</td>
            <td>
              {number.format(point.actionReceiptCount)} receipt<br />
              {number.format(point.grantedRewardCount)} granted / {number.format(point.deniedDailyCapCount)} denied cap
            </td>
          </tr>)}</tbody>
        </table>
      </div>
    </> : null}
  </section>;
}

function Metric({ title, value, detail }: { title: string; value: string; detail?: string }) {
  return <article className="dashboard-metric"><h2>{title}</h2><strong>{value}</strong>{detail ? <p>{detail}</p> : null}</article>;
}
