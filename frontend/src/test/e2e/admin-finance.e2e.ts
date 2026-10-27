import { expect, test } from "@playwright/test";

const refundId = "11111111-1111-4111-8111-111111111111";
const paymentId = "22222222-2222-4222-8222-222222222222";
const transferRecordId = "33333333-3333-4333-8333-333333333333";

test("admin inspects finance evidence and completes the explicit refund workflow", async ({ page }) => {
  let status = "Requested";
  let version = 1;
  let transfer: null | {
    transferRecordId: string; bankReference: string; amountVnd: number; evidenceReference: string;
    status: string; recordedAt: string; verifiedAt: string | null; confirmationNote: string | null;
    resolutionOperationId: string | null;
  } = null;
  const idempotencyKeys: string[] = [];

  const refund = () => ({
    refundId, paymentOrderId: paymentId, orderCode: "TOPUP-A02", userId: "44444444-4444-4444-8444-444444444444",
    userDisplayName: "Người đọc", userEmail: "reader@example.test", status,
    originalAmountVnd: 100_000, feeVnd: 5_000, netRefundVnd: 95_000,
    requiredRecoveryCoins: 10_000, requestedAt: "2026-10-27T00:00:00Z", version,
  });
  const detail = () => ({
    refund: refund(), reason: "Hoàn TopUp gốc",
    recipient: { bankCode: "VCB", accountNumberMasked: "******6789", accountHolderMasked: "N*** V*** A***" },
    originalPaidCoinLot: { lotId: "55555555-5555-4555-8555-555555555555", grantedCoins: 10_000, spentCoins: 0, refundedCoins: 0, reservedCoins: status === "RecoveryReserved" || status === "TransferPending" ? 10_000 : 0, availableCoins: status === "Requested" || status === "UnderReview" ? 10_000 : 0, version: 1 },
    reservation: ["RecoveryReserved", "TransferPending", "NeedsReconciliation", "Completed"].includes(status)
      ? { reservationId: "66666666-6666-4666-8666-666666666666", walletOperationId: "77777777-7777-4777-8777-777777777777", amountCoins: 10_000, status: status === "Completed" ? "Consumed" : "Active", createdAt: "2026-10-27T00:01:00Z", resolvedAt: status === "Completed" ? "2026-10-27T00:04:00Z" : null }
      : null,
    transfer,
    auditTimeline: [{ auditId: "88888888-8888-4888-8888-888888888888", actorId: "99999999-9999-4999-8999-999999999999", action: status, occurredAt: "2026-10-27T00:02:00Z", redactedChanges: "{}" }],
  });

  await page.route("**/api/v1/me", (route) => route.fulfill({
    headers: { "X-CSRF-TOKEN": "a02-csrf" },
    json: { id: "99999999-9999-4999-8999-999999999999", displayName: "Admin", email: "admin@example.test", avatarUrl: null, role: "Admin", status: "Active" },
  }));
  await page.route("**/api/v1/admin/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    if (path === "/api/v1/admin/payments" && method === "GET") return route.fulfill({ json: { items: [{
      paymentId, orderCode: "TOPUP-A02", userId: "44444444-4444-4444-8444-444444444444", userDisplayName: "Người đọc", userEmail: "reader@example.test",
      orderType: "TopUp", amountVnd: 100_000, paidCoinsSnapshot: 10_000, orderStatus: "Succeeded", providerPaymentStatus: "Verified",
      fulfillmentStatus: "Fulfilled", createdAt: "2026-10-27T00:00:00Z", expiresAt: "2026-10-27T00:30:00Z", verifiedPaidAt: "2026-10-27T00:01:00Z", reconciliationReason: null,
    }], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === `/api/v1/admin/payments/${paymentId}` && method === "GET") return route.fulfill({ json: {
      payment: { paymentId, orderCode: "TOPUP-A02", userId: "44444444-4444-4444-8444-444444444444", userDisplayName: "Người đọc", userEmail: "reader@example.test", orderType: "TopUp", amountVnd: 100_000, paidCoinsSnapshot: 10_000, orderStatus: "Succeeded", providerPaymentStatus: "Verified", fulfillmentStatus: "Fulfilled", createdAt: "2026-10-27T00:00:00Z", expiresAt: "2026-10-27T00:30:00Z", verifiedPaidAt: "2026-10-27T00:01:00Z", reconciliationReason: null },
      providerTransactions: [{ transactionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", provider: "SePay", providerTransactionId: "provider-a02", amountVnd: 100_000, direction: "Inbound", accountNumberLast4: "6789", referenceCode: "TOPUP-A02", occurredAt: "2026-10-27T00:01:00Z", verifiedAt: "2026-10-27T00:01:01Z", processingStatus: "Fulfilled", reconciliationReason: null }],
    } });
    if (path === "/api/v1/admin/ledger" && method === "GET") return route.fulfill({ json: { items: [{ entryId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", operationId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", userId: "44444444-4444-4444-8444-444444444444", userDisplayName: "Người đọc", userEmail: "reader@example.test", operationType: "TopUpCredit", sourceType: "PaymentOrder", sourceId: paymentId, coinType: "Paid", bucket: "Available", deltaCoins: 10_000, legOrder: 1, createdAt: "2026-10-27T00:01:00Z" }], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === "/api/v1/admin/refunds" && method === "GET") return route.fulfill({ json: { items: [refund()], page: 1, pageSize: 30, totalItems: 1, totalPages: 1 } });
    if (path === `/api/v1/admin/refunds/${refundId}` && method === "GET") return route.fulfill({ json: detail() });

    if (path === `/api/v1/admin/refunds/${refundId}/review` && method === "POST") { status = "UnderReview"; version++; return route.fulfill({ json: refund() }); }
    if (path === `/api/v1/admin/refunds/${refundId}/approve` && method === "POST") {
      idempotencyKeys.push(request.headers()["idempotency-key"] ?? ""); status = "RecoveryReserved"; version++;
      return route.fulfill({ json: refund() });
    }
    if (path === `/api/v1/admin/refunds/${refundId}/transfer-attempts` && method === "POST") {
      idempotencyKeys.push(request.headers()["idempotency-key"] ?? "");
      const body = request.postDataJSON() as { bankReference: string; evidenceReference: string };
      status = "TransferPending"; version++;
      transfer = { transferRecordId, bankReference: body.bankReference, amountVnd: 95_000, evidenceReference: body.evidenceReference, status: "ReportedSucceeded", recordedAt: "2026-10-27T00:03:00Z", verifiedAt: null, confirmationNote: null, resolutionOperationId: null };
      return route.fulfill({ json: refund() });
    }
    if (path === `/api/v1/admin/refunds/${refundId}/complete` && method === "POST") {
      idempotencyKeys.push(request.headers()["idempotency-key"] ?? ""); status = "Completed"; version++;
      if (transfer) transfer = { ...transfer, status: "ConfirmedSucceeded", verifiedAt: "2026-10-27T00:04:00Z", confirmationNote: "Đã xác minh" };
      return route.fulfill({ json: refund() });
    }
    return route.fulfill({ status: 404, json: { errorCode: "RESOURCE_NOT_FOUND", message: "missing", traceId: "a02-e2e" } });
  });
  page.on("dialog", (dialog) => void dialog.accept());

  await page.goto("/admin/payments");
  await expect(page.getByRole("heading", { name: "Thanh toán" })).toBeVisible();
  await page.getByRole("button", { name: "TOPUP-A02" }).click();
  await expect(page.getByText("provider-a02", { exact: false })).toBeVisible();
  await expect(page.getByText("tài khoản ••••6789", { exact: false })).toBeVisible();

  await page.goto("/admin/ledger");
  await expect(page.getByRole("heading", { name: "Sổ cái Wallet" })).toBeVisible();
  await expect(page.getByRole("cell", { name: /TopUpCredit/ })).toBeVisible();

  await page.goto("/admin/refunds");
  await page.getByRole("button", { name: "TOPUP-A02" }).click();
  await expect(page.getByText("******6789", { exact: false })).toBeVisible();
  await expect(page.getByText("123456789", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Bắt đầu xem xét" }).click();
  await page.getByLabel("Ghi chú quyết định").fill("Đã đối chiếu lot gốc");
  await page.getByRole("button", { name: "Duyệt và giữ coin" }).click();
  await page.getByLabel("Mã tham chiếu ngân hàng").fill("BANK-A02");
  await page.getByLabel("Tham chiếu bằng chứng").fill("EVIDENCE-A02");
  await page.getByLabel("Kết quả được báo cáo").selectOption("Succeeded");
  await page.getByRole("button", { name: "Ghi nhận bằng chứng" }).click();
  await page.getByLabel("Ghi chú xác nhận độc lập").fill("Đã xác minh");
  await page.getByRole("button", { name: "Xác nhận thành công" }).click();
  await expect(page.getByText("Completed · v5")).toBeVisible();
  expect(idempotencyKeys).toHaveLength(3);
  expect(idempotencyKeys.every((value) => value.length > 0)).toBe(true);
  expect(new Set(idempotencyKeys).size).toBe(3);
});
