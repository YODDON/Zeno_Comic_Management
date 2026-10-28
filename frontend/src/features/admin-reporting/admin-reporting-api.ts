import { apiRequest } from "@/shared/api/client";

export type AdminDateRange = {
  from: string;
  to: string;
  startsAtUtc: string;
  endsAtUtc: string;
};

export type AdminDashboard = {
  range: AdminDateRange;
  revenue: { topUpGrossVnd: number; vipGrossVnd: number };
  refunds: { completedCount: number; originalAmountVnd: number; feeVnd: number; netRefundVnd: number };
  coinsIssued: { paidCoins: number; bonusCoins: number };
  reading: { qualifiedSessionCount: number; distinctReaderCount: number };
  missions: { actionReceiptCount: number; grantedRewardCount: number; deniedDailyCapCount: number; grantedBonusCoins: number };
  dailySeries: Array<{
    date: string;
    topUpGrossVnd: number;
    vipGrossVnd: number;
    completedRefundCount: number;
    refundOriginalAmountVnd: number;
    refundFeeVnd: number;
    refundNetVnd: number;
    paidCoinsIssued: number;
    bonusCoinsGranted: number;
    qualifiedSessionCount: number;
    distinctReaderCount: number;
    actionReceiptCount: number;
    grantedRewardCount: number;
    deniedDailyCapCount: number;
  }>;
};

export type AdminAuditItem = {
  id: string;
  actorId: string;
  action: string;
  targetType: string;
  targetId: string;
  occurredAt: string;
  redactedChanges: string;
};

export type AdminAuditPage = {
  items: AdminAuditItem[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
};

export type ReportingRange = { from?: string; to?: string };
export type AuditCriteria = ReportingRange & {
  actorId?: string;
  action?: string;
  targetType?: string;
  targetId?: string;
  page: number;
  pageSize: number;
};

function query(values: Record<string, string | number | undefined>) {
  const params = new URLSearchParams();
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && String(value).trim()) params.set(key, String(value).trim());
  });
  return params.toString();
}

export const adminReportingQueryKeys = {
  dashboard: (viewerKey: string, range: ReportingRange) =>
    ["admin", "reporting", viewerKey, "dashboard", range] as const,
  audit: (viewerKey: string, criteria: AuditCriteria) =>
    ["admin", "reporting", viewerKey, "audit", criteria] as const,
};

export const adminReportingApi = {
  dashboard: (range: ReportingRange) =>
    apiRequest<AdminDashboard>(`/api/v1/admin/dashboard?${query(range)}`),
  audit: (criteria: AuditCriteria) =>
    apiRequest<AdminAuditPage>(`/api/v1/admin/audit?${query(criteria)}`),
};
