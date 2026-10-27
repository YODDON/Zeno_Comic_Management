import { apiRequest } from "@/shared/api/client";

export type AdminPage<T> = { items: T[]; page: number; pageSize: number; totalItems: number; totalPages: number };
export type AdminPayment = {
  paymentId: string; orderCode: string; userId: string; userDisplayName: string; userEmail: string | null;
  orderType: "TopUp" | "VipPurchase"; amountVnd: number; paidCoinsSnapshot: number | null;
  orderStatus: "Pending" | "Expired" | "Succeeded"; providerPaymentStatus: string;
  fulfillmentStatus: string; createdAt: string; expiresAt: string; verifiedPaidAt: string | null;
  reconciliationReason: string | null;
};
export type AdminProviderTransaction = {
  transactionId: string; provider: string; providerTransactionId: string; amountVnd: number;
  direction: string; accountNumberLast4: string; referenceCode: string | null;
  occurredAt: string; verifiedAt: string; processingStatus: string; reconciliationReason: string | null;
};
export type AdminPaymentDetail = { payment: AdminPayment; providerTransactions: AdminProviderTransaction[] };
export type AdminLedgerEntry = {
  entryId: string; operationId: string; userId: string; userDisplayName: string; userEmail: string | null;
  operationType: string; sourceType: string; sourceId: string; coinType: string; bucket: string;
  deltaCoins: number; legOrder: number; createdAt: string;
};
export type AdminRefund = {
  refundId: string; paymentOrderId: string; orderCode: string; userId: string; userDisplayName: string;
  userEmail: string | null; status: string; originalAmountVnd: number; feeVnd: number; netRefundVnd: number;
  requiredRecoveryCoins: number; requestedAt: string; version: number;
};
export type AdminRefundState = {
  refundId: string; status: string; requiredRecoveryCoins: number; version: number;
  reviewedBy: string | null; reviewedAt: string | null; approvedBy: string | null; approvedAt: string | null;
  decisionReason: string | null; reservationId: string | null; walletOperationId: string | null;
  transferRecordId: string | null; transferStatus: string | null; resolutionOperationId: string | null;
};
export type AdminRefundDetail = {
  refund: AdminRefund; reason: string;
  recipient: { bankCode: string; accountNumberMasked: string; accountHolderMasked: string };
  originalPaidCoinLot: null | { lotId: string; grantedCoins: number; spentCoins: number; refundedCoins: number; reservedCoins: number; availableCoins: number; version: number };
  reservation: null | { reservationId: string; walletOperationId: string; amountCoins: number; status: string; createdAt: string; resolvedAt: string | null };
  transfer: null | { transferRecordId: string; bankReference: string; amountVnd: number; evidenceReference: string; status: string; recordedAt: string; verifiedAt: string | null; confirmationNote: string | null; resolutionOperationId: string | null };
  auditTimeline: Array<{ auditId: string; actorId: string; action: string; occurredAt: string; redactedChanges: string }>;
};

export type AdminFinanceCriteria = { search?: string; status?: string; type?: string; operationType?: string; page?: number; pageSize?: number };

export const adminFinanceQueryKeys = {
  payments: (viewerKey: string, criteria: AdminFinanceCriteria) =>
    ["admin", "finance", viewerKey, "payments", criteria] as const,
  payment: (viewerKey: string, paymentId: string | null) =>
    ["admin", "finance", viewerKey, "payment", paymentId] as const,
  ledger: (viewerKey: string, criteria: AdminFinanceCriteria) =>
    ["admin", "finance", viewerKey, "ledger", criteria] as const,
  refunds: (viewerKey: string, criteria: AdminFinanceCriteria) =>
    ["admin", "finance", viewerKey, "refunds", criteria] as const,
  refund: (viewerKey: string, refundId: string | null) =>
    ["admin", "finance", viewerKey, "refund", refundId] as const,
};

function query(criteria: AdminFinanceCriteria) {
  const result = new URLSearchParams({ page: String(criteria.page ?? 1), pageSize: String(criteria.pageSize ?? 30) });
  for (const [key, value] of Object.entries(criteria)) {
    if (key !== "page" && key !== "pageSize" && typeof value === "string" && value.trim()) result.set(key, value.trim());
  }
  return result.toString();
}

export const adminFinanceApi = {
  payments: (criteria: AdminFinanceCriteria) => apiRequest<AdminPage<AdminPayment>>(`/api/v1/admin/payments?${query(criteria)}`),
  payment: (id: string) => apiRequest<AdminPaymentDetail>(`/api/v1/admin/payments/${id}`),
  ledger: (criteria: AdminFinanceCriteria) => apiRequest<AdminPage<AdminLedgerEntry>>(`/api/v1/admin/ledger?${query(criteria)}`),
  refunds: (criteria: AdminFinanceCriteria) => apiRequest<AdminPage<AdminRefund>>(`/api/v1/admin/refunds?${query(criteria)}`),
  refund: (id: string) => apiRequest<AdminRefundDetail>(`/api/v1/admin/refunds/${id}`),
  reviewRefund: (id: string) => apiRequest<AdminRefundState>(`/api/v1/admin/refunds/${id}/review`, { method: "POST" }),
  approveRefund: (id: string, expectedVersion: number, decisionNote: string, idempotencyKey: string) =>
    apiRequest<AdminRefundState>(`/api/v1/admin/refunds/${id}/approve`, { method: "POST", idempotencyKey, body: { expectedVersion, decisionNote } }),
  rejectRefund: (id: string, expectedVersion: number, reason: string) =>
    apiRequest<AdminRefundState>(`/api/v1/admin/refunds/${id}/reject`, { method: "POST", body: { expectedVersion, reason } }),
  recordTransfer: (id: string, expectedVersion: number, bankReference: string, evidenceReference: string, reportedOutcome: string, idempotencyKey: string) =>
    apiRequest<AdminRefundState>(`/api/v1/admin/refunds/${id}/transfer-attempts`, { method: "POST", idempotencyKey, body: { expectedVersion, bankReference, evidenceReference, reportedOutcome } }),
  resolveTransfer: (id: string, action: "complete" | "release", expectedVersion: number, transferRecordId: string, confirmationNote: string, idempotencyKey: string) =>
    apiRequest<AdminRefundState>(`/api/v1/admin/refunds/${id}/${action}`, { method: "POST", idempotencyKey, body: { expectedVersion, transferRecordId, confirmationNote } }),
};
