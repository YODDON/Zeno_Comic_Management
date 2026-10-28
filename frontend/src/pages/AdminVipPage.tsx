import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { adminPlatformApi, adminPlatformKeys } from "@/features/admin-platform/admin-platform-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, LoadingState, SuccessState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const money = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND" });

export function AdminVipPage() {
  const profile = useProfileQuery();
  const viewer = profile.data?.id;
  const query = useQuery({ queryKey: adminPlatformKeys.vip(viewer ?? "unresolved"), queryFn: adminPlatformApi.vipPlan, enabled: Boolean(viewer) });
  if (query.isPending) return <LoadingState title="Đang tải cấu hình VIP" />;
  if (query.isError || !query.data) return <ApiErrorState error={query.error} onRetry={() => void query.refetch()} />;
  return <VipEditor plan={query.data} viewer={viewer!} />;
}

function VipEditor({ plan, viewer }: { plan: Awaited<ReturnType<typeof adminPlatformApi.vipPlan>>; viewer: string }) {
  const client = useQueryClient();
  const [price, setPrice] = useState(String(plan.priceVnd));
  const update = useMutation({
    mutationFn: () => adminPlatformApi.updateVipPrice(Number(price), plan.version),
    onSuccess: (value) => client.setQueryData(adminPlatformKeys.vip(viewer), value),
  });
  return <section className="admin-workspace admin-editor-stack">
    <header><p className="eyebrow">Quyền lợi</p><h1>Gói VIP</h1><p>Giá mới chỉ áp dụng cho đơn tạo sau khi lưu. Đơn hiện hữu giữ nguyên snapshot.</p></header>
    <dl className="finance-summary"><div><dt>Giá hiện tại</dt><dd>{money.format(plan.priceVnd)}</dd></div><div><dt>Thời hạn cố định</dt><dd>{plan.durationHours} giờ</dd></div><div><dt>Giảm giá cố định</dt><dd>{plan.discountPercent}%</dd></div><div><dt>Phiên bản</dt><dd>{plan.version}</dd></div></dl>
    <label>Giá cho đơn VIP tương lai (VND)<input min={1} step={1000} type="number" value={price} onChange={(event) => setPrice(event.target.value)} /></label>
    {update.isError ? <ApiErrorState error={update.error} onRetry={() => update.reset()} /> : null}
    {update.isSuccess ? <SuccessState title="Đã cập nhật giá" description="Thời hạn 720 giờ và mức giảm 20% không thay đổi." /> : null}
    <Button disabled={update.isPending || !Number.isSafeInteger(Number(price)) || Number(price) < 1} onClick={() => update.mutate()}>{update.isPending ? "Đang lưu…" : "Lưu giá tương lai"}</Button>
    <p role="note"><strong>Verification Required:</strong> SePay production chưa được xác minh; trang này không xác nhận thanh toán.</p>
  </section>;
}
