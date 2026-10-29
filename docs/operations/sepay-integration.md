# SePay Integration — VERIFICATION REQUIRED

## Approved application behavior
TopUp and VIP paid in VND through SePay, user order Pending until verified provider event, exact amount/code/receiving account/direction checks, provider transaction dedup, idempotent DB fulfillment (Paid lot or VIP extension), audit and reconciliation for mismatch/late/unmatched payments. Frontend redirect is never payment confirmation.

## Provider setup and verification checklist
1. Select actual SePay product/path (QR bank transfer vs hosted checkout) and record credentials and bank account in secure configuration.
2. Obtain authenticated webhook sample(s), headers and exact acknowledgement/retry requirements from official product docs/current account. HMAC-SHA256/raw-body+timestamp is preferred **only where that exact account/product supports it**, implement constant-time comparison and replay window.
3. Normalize financial transaction identity distinct from webhook delivery identity. Verify amount, incoming direction, destination account and unique order code.
4. Test duplicate/concurrent webhook, wrong signature, wrong amount, unknown code, two orders matched, expired-order late payment, provider timeout and DB commit rollback.
5. Implement reconciliation workflow based on provider-supported transaction lookup/manual evidence, never auto-credit an unmatched event.
6. Switch credentials test→live only after passing tests; capture redacted trace and operator runbook.

## Security
Secrets backend only; minimal logs with redaction; signed endpoint should reject invalid events and return provider-required acknowledgement for valid-but-unmatched events. No assumed payload attributes before verification; adapter normalization DTO isolates provider changes.

## Release gate
No money-bearing production TopUp/VIP until payment matching and idempotent fulfillment verified against actual SePay sandbox/test and live configuration.

The Q02 Test-mode simulation, Replay and database reconciliation procedure is
defined in [Q02 provider verification](../testing/q02-provider-security-poc.md).
The verifier records separate success/replay/unknown/mismatch/late/invalid-auth
and retry outcomes with zero-financial-side-effect checks where required.
Fixture tests and self-generated HMAC requests remain supplementary evidence,
not provider verification.
