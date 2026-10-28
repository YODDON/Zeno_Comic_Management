import { useState, type FormEvent } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { adminPlatformApi, adminPlatformKeys, type MissionDefinition } from "@/features/admin-platform/admin-platform-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState, SuccessState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

export function AdminMissionsPage() {
  const rewardPageSize = 30;
  const profile = useProfileQuery();
  const viewer = profile.data?.id;
  const client = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [rewardOutcome, setRewardOutcome] = useState("");
  const [rewardPage, setRewardPage] = useState(1);
  const query = useQuery({ queryKey: adminPlatformKeys.missions(viewer ?? "unresolved"), queryFn: adminPlatformApi.missions, enabled: Boolean(viewer) });
  const rewards = useQuery({
    queryKey: adminPlatformKeys.missionRewards(viewer ?? "unresolved", rewardOutcome, rewardPage, rewardPageSize),
    queryFn: () => adminPlatformApi.missionRewards({ outcome: rewardOutcome, page: rewardPage, pageSize: rewardPageSize }),
    enabled: Boolean(viewer),
    placeholderData: keepPreviousData,
  });
  const refresh = async () => client.invalidateQueries({ queryKey: adminPlatformKeys.missions(viewer!) });
  const create = useMutation({ mutationFn: adminPlatformApi.createMission, onSuccess: async (mission) => { setSelectedId(mission.id); await refresh(); } });
  const selected = query.data?.find((mission) => mission.id === selectedId) ?? null;

  return <section className="admin-workspace admin-editor-stack">
    <header><p className="eyebrow">Quyền lợi</p><h1>Nhiệm vụ</h1><p>Revision là append-only. Chỉ revision được kích hoạt mới là authority hiện tại; lịch sử activation quyết định event-time.</p></header>
    <MissionCreateForm disabled={create.isPending} error={create.error} onSubmit={(value) => create.mutate(value)} />
    {create.isSuccess ? <SuccessState title="Đã tạo Mission" description="Hãy thêm revision rồi kích hoạt revision đã chọn." /> : null}
    {query.isPending ? <LoadingState title="Đang tải Mission" /> : null}
    {query.isError ? <ApiErrorState error={query.error} onRetry={() => void query.refetch()} /> : null}
    {query.data?.length === 0 ? <EmptyState title="Chưa có Mission" description="Tạo MissionDefinition đầu tiên để bắt đầu cấu hình." /> : null}
    {query.data?.length ? <div className="admin-table-scroll"><table className="admin-data-table"><thead><tr><th>Mã</th><th>Tên</th><th>Action / Chu kỳ</th><th>Trạng thái</th><th>Version</th></tr></thead><tbody>{query.data.map((mission) => <tr key={mission.id}><td><button className="table-link" onClick={() => setSelectedId(mission.id)}>{mission.code}</button></td><td>{mission.name}</td><td>{mission.actionType}<small>{mission.cycleType}</small></td><td><span className={`status-badge status-${mission.status.toLowerCase()}`}>{mission.status}</span></td><td>{mission.version}</td></tr>)}</tbody></table></div> : null}
    {selected ? <MissionWorkspace key={`${selected.id}:${selected.version}:${selected.revisions.length}`} mission={selected} onChanged={refresh} /> : null}
    <section><div className="finance-detail-heading"><div><h2>Lịch sử thưởng</h2><p>DeniedDailyCap là quyết định thành công nhưng cấp 0 Bonus.</p></div><label>Kết quả<select value={rewardOutcome} onChange={(event) => { setRewardOutcome(event.target.value); setRewardPage(1); }}><option value="">Tất cả</option><option>Granted</option><option>DeniedDailyCap</option></select></label></div>
      {rewards.isPending ? <LoadingState title="Đang tải lịch sử thưởng" /> : null}{rewards.isError ? <ApiErrorState error={rewards.error} onRetry={() => void rewards.refetch()} /> : null}{rewards.data?.items.length === 0 ? <EmptyState title="Chưa có quyết định thưởng" description="Worker chưa ghi nhận reward grant nào." /> : null}
      {rewards.data?.items.length ? <div className="admin-table-scroll"><table className="admin-data-table"><thead><tr><th>Mission</th><th>Người dùng</th><th>Kết quả</th><th>Bonus</th><th>Ngày hành động</th></tr></thead><tbody>{rewards.data.items.map((reward) => <tr key={reward.rewardGrantId}><td>{reward.missionName}</td><td>{reward.userDisplayName}<small>{reward.userEmail ?? "—"}</small></td><td>{reward.outcome}</td><td>{reward.grantedBonusCoins}/{reward.rewardBonusCoins}</td><td>{reward.localDate}</td></tr>)}</tbody></table></div> : null}
      {rewards.isFetching && !rewards.isPending ? <p aria-live="polite" role="status">Đang tải trang {rewardPage}…</p> : null}
      {rewards.data && rewards.data.totalPages > 1 ? <nav aria-label="Phân trang lịch sử thưởng" className="admin-actions"><Button disabled={rewardPage <= 1 || rewards.isFetching} onClick={() => setRewardPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {rewardPage}/{rewards.data.totalPages} · {rewards.data.totalItems} quyết định</span><Button disabled={rewardPage >= rewards.data.totalPages || rewards.isFetching} onClick={() => setRewardPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}
    </section>
  </section>;
}

type CreateMission = Parameters<typeof adminPlatformApi.createMission>[0];
function MissionCreateForm({ disabled, error, onSubmit }: { disabled: boolean; error: unknown; onSubmit: (value: CreateMission) => void }) {
  const [code, setCode] = useState(""); const [name, setName] = useState(""); const [description, setDescription] = useState("");
  const [actionType, setActionType] = useState<CreateMission["actionType"]>("DailyLogin"); const [cycleType, setCycleType] = useState<CreateMission["cycleType"]>("Daily");
  return <details><summary>Tạo MissionDefinition</summary><form className="admin-form-grid" onSubmit={(event) => { event.preventDefault(); onSubmit({ code: code.trim(), name: name.trim(), description: description.trim(), actionType, cycleType }); }}>
    <label>Mã<input maxLength={80} required value={code} onChange={(e) => setCode(e.target.value)} /></label><label>Tên<input maxLength={200} required value={name} onChange={(e) => setName(e.target.value)} /></label>
    <label className="admin-field-wide">Mô tả<textarea maxLength={2000} value={description} onChange={(e) => setDescription(e.target.value)} /></label>
    <label>Action<select value={actionType} onChange={(e) => setActionType(e.target.value as CreateMission["actionType"])}>{["DailyLogin", "ChapterReadQualified", "ComicFollowed", "ChapterUnlocked", "WalletTopUpCompleted"].map((value) => <option key={value}>{value}</option>)}</select></label>
    <label>Chu kỳ<select value={cycleType} onChange={(e) => setCycleType(e.target.value as CreateMission["cycleType"])}>{["Fixed", "Daily", "Weekly", "Event"].map((value) => <option key={value}>{value}</option>)}</select></label>
    {error ? <ApiErrorState error={error} /> : null}<Button disabled={disabled || !code.trim() || !name.trim()} type="submit">{disabled ? "Đang tạo…" : "Tạo Mission"}</Button>
  </form></details>;
}

function MissionWorkspace({ mission, onChanged }: { mission: MissionDefinition; onChanged: () => Promise<unknown> }) {
  const [target, setTarget] = useState("1"); const [reward, setReward] = useState("1");
  const [repeat, setRepeat] = useState<"OncePerVietnamDate" | "UniqueSubjectPerPeriod">(mission.actionType === "DailyLogin" ? "OncePerVietnamDate" : "UniqueSubjectPerPeriod");
  const [from, setFrom] = useState(""); const [until, setUntil] = useState(""); const [configuration, setConfiguration] = useState("{}");
  const [configurationError, setConfigurationError] = useState<string | null>(null);
  const parseConfiguration = () => {
    const parsed = JSON.parse(configuration) as unknown;
    if (parsed === null || Array.isArray(parsed) || typeof parsed !== "object") throw new Error();
    return parsed as Record<string, unknown>;
  };
  const add = useMutation({ mutationFn: () => adminPlatformApi.addRevision(mission.id, { targetCount: Number(target), rewardBonusCoins: Number(reward), repeatPolicy: repeat, effectiveFrom: new Date(from).toISOString(), effectiveUntil: until ? new Date(until).toISOString() : null, configurationSnapshot: parseConfiguration() }), onSuccess: onChanged });
  const activate = useMutation({ mutationFn: (revisionId: string) => adminPlatformApi.activateMission(mission.id, revisionId, mission.version), onSuccess: onChanged });
  const deactivate = useMutation({ mutationFn: () => adminPlatformApi.deactivateMission(mission.id, mission.version), onSuccess: onChanged });
  const busy = add.isPending || activate.isPending || deactivate.isPending;
  const error = add.error ?? activate.error ?? deactivate.error;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    try {
      parseConfiguration();
      setConfigurationError(null);
      add.mutate();
    } catch {
      setConfigurationError("Configuration phải là một JSON object hợp lệ.");
    }
  };
  return <aside className="finance-detail" aria-label="Trình chỉnh sửa Mission"><div className="finance-detail-heading"><div><h2>{mission.name}</h2><p>{mission.code} · {mission.status} · v{mission.version}</p></div></div>
    <p>{mission.description || "Không có mô tả."}</p>
    <h3>Revision</h3>{mission.revisions.length === 0 ? <p>Chưa có revision.</p> : <ol className="finance-timeline">{mission.revisions.map((revision) => <li key={revision.id}><strong>Revision {revision.revisionNumber}</strong> · mục tiêu {revision.targetCount} · thưởng {revision.rewardBonusCoins} Bonus<br /><time>{new Date(revision.effectiveFrom).toLocaleString("vi-VN")}</time>{revision.effectiveUntil ? <> → <time>{new Date(revision.effectiveUntil).toLocaleString("vi-VN")}</time></> : null}<div><Button disabled={busy || mission.activeRevisionId === revision.id} onClick={() => activate.mutate(revision.id)} variant="outline">{mission.activeRevisionId === revision.id ? "Đang active" : "Kích hoạt"}</Button></div></li>)}</ol>}
    <form className="admin-form-grid" onSubmit={submit}><h3 className="admin-field-wide">Thêm revision bất biến</h3><label>Mục tiêu<input min={1} required type="number" value={target} onChange={(e) => setTarget(e.target.value)} /></label><label>Bonus<input max={300} min={1} required type="number" value={reward} onChange={(e) => setReward(e.target.value)} /></label><label>Repeat policy<select value={repeat} onChange={(e) => setRepeat(e.target.value as typeof repeat)}><option>OncePerVietnamDate</option><option>UniqueSubjectPerPeriod</option></select></label><label>Hiệu lực từ<input required type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} /></label><label>Hiệu lực đến (tùy chọn)<input type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} /></label><label className="admin-field-wide">Configuration JSON<textarea aria-invalid={Boolean(configurationError)} required value={configuration} onChange={(e) => { setConfiguration(e.target.value); setConfigurationError(null); }} /></label>{configurationError ? <p className="admin-field-wide" role="alert">{configurationError}</p> : null}<Button disabled={busy || !from} type="submit">{add.isPending ? "Đang thêm…" : "Thêm revision"}</Button></form>
    {mission.status === "Active" ? <Button disabled={busy} onClick={() => deactivate.mutate()} variant="outline">Tắt Mission</Button> : null}
    {error ? <ApiErrorState error={error} onRetry={() => { add.reset(); activate.reset(); deactivate.reset(); }} /> : null}
  </aside>;
}
