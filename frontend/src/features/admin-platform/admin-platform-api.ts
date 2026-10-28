import { apiRequest } from "@/shared/api/client";

export type MissionRevision = {
  id: string; missionId: string; revisionNumber: number; targetCount: number;
  rewardBonusCoins: number; repeatPolicy: "OncePerVietnamDate" | "UniqueSubjectPerPeriod";
  effectiveFrom: string; effectiveUntil: string | null; configurationSnapshot: Record<string, unknown>;
};
export type MissionDefinition = {
  id: string; code: string; name: string; description: string;
  actionType: "DailyLogin" | "ChapterReadQualified" | "ComicFollowed" | "ChapterUnlocked" | "WalletTopUpCompleted";
  cycleType: "Fixed" | "Daily" | "Weekly" | "Event";
  status: "Draft" | "Active"; activeRevisionId: string | null; createdAt: string; version: number;
  revisions: MissionRevision[];
};
export type VipPlan = { planId: string; priceVnd: number; durationHours: number; discountPercent: number; version: number };
export type AdminUser = {
  id: string; displayName: string; email: string | null; avatarUrl: string | null;
  role: "User" | "Admin"; status: "Active" | "Disabled";
  createdAt: string; updatedAt: string; version: number;
};
export type AdminUserPage = { items: AdminUser[]; page: number; pageSize: number; totalItems: number; totalPages: number };
export type AdminUserDetail = {
  user: AdminUser;
  wallet: { paidAvailable: number; paidReserved: number; bonusAvailable: number; totalAvailable: number; version: number | null };
  membership: { isActive: boolean; expiresAt: string | null; version: number | null };
  access: { followedComicCount: number; unlockedChapterCount: number };
  recentPayments: Array<{ paymentId: string; orderCode: string; orderType: "TopUp" | "VipPurchase"; amountVnd: number; status: "Pending" | "Expired" | "Succeeded"; createdAt: string }>;
};
export type AdminMissionReward = { rewardGrantId: string; userId: string; userDisplayName: string; userEmail: string | null; missionId: string; revisionId: string; missionName: string; outcome: "Granted" | "DeniedDailyCap"; rewardBonusCoins: number; grantedBonusCoins: number; localDate: string; decidedAt: string };
export type AdminMissionRewardPage = { items: AdminMissionReward[]; page: number; pageSize: number; totalItems: number; totalPages: number };

export const adminPlatformKeys = {
  missions: (viewer: string) => ["admin", "platform", viewer, "missions"] as const,
  missionRewards: (viewer: string, outcome: string, page: number, pageSize: number) => ["admin", "platform", viewer, "mission-rewards", outcome, page, pageSize] as const,
  vip: (viewer: string) => ["admin", "platform", viewer, "vip"] as const,
  users: (viewer: string, criteria: object) => ["admin", "platform", viewer, "users", criteria] as const,
  user: (viewer: string, id: string | null) => ["admin", "platform", viewer, "user", id] as const,
};

export const adminPlatformApi = {
  missions: () => apiRequest<MissionDefinition[]>("/api/v1/admin/missions"),
  missionRewards: ({ outcome, page, pageSize }: { outcome: string; page: number; pageSize: number }) => apiRequest<AdminMissionRewardPage>(`/api/v1/admin/mission-rewards?page=${page}&pageSize=${pageSize}${outcome ? `&outcome=${encodeURIComponent(outcome)}` : ""}`),
  createMission: (body: Pick<MissionDefinition, "code" | "name" | "description" | "actionType" | "cycleType">) =>
    apiRequest<MissionDefinition>("/api/v1/admin/missions", { method: "POST", body }),
  addRevision: (missionId: string, body: { targetCount: number; rewardBonusCoins: number; repeatPolicy: MissionRevision["repeatPolicy"]; effectiveFrom: string; effectiveUntil: string | null; configurationSnapshot: Record<string, unknown> }) =>
    apiRequest<MissionRevision>(`/api/v1/admin/missions/${missionId}/revisions`, { method: "POST", body }),
  activateMission: (missionId: string, revisionId: string, expectedVersion: number) =>
    apiRequest<MissionDefinition>(`/api/v1/admin/missions/${missionId}/activate`, { method: "POST", body: { revisionId, expectedVersion } }),
  deactivateMission: (missionId: string, expectedVersion: number) =>
    apiRequest<MissionDefinition>(`/api/v1/admin/missions/${missionId}/deactivate`, { method: "POST", body: { expectedVersion } }),
  vipPlan: () => apiRequest<VipPlan>("/api/v1/admin/vip-plan"),
  updateVipPrice: (priceVnd: number, expectedVersion: number) =>
    apiRequest<VipPlan>("/api/v1/admin/vip-plan", { method: "PATCH", body: { priceVnd, expectedVersion } }),
  users: (criteria: { search: string; status: string; page: number }) => {
    const params = new URLSearchParams({ page: String(criteria.page), pageSize: "30" });
    if (criteria.search.trim()) params.set("search", criteria.search.trim());
    if (criteria.status) params.set("status", criteria.status);
    return apiRequest<AdminUserPage>(`/api/v1/admin/users?${params}`);
  },
  user: (id: string) => apiRequest<AdminUserDetail>(`/api/v1/admin/users/${id}`),
  changeUserStatus: (id: string, status: AdminUser["status"], expectedVersion: number) =>
    apiRequest<AdminUserDetail>(`/api/v1/admin/users/${id}/${status === "Active" ? "enable" : "disable"}`, { method: "POST", body: { expectedVersion } }),
};
