# Unverified Items and Production Release Gates

| Gate | Status before implementation | Evidence needed |
|---|---|---|
| SePay checkout/QR | VERIFICATION REQUIRED | Actual product choice & tested instructions |
| SePay webhook HMAC/payload/retry | HARNESS READY / VERIFICATION REQUIRED | Project Test-mode delivery + Replay, redacted trace and signature/ack evidence; see `docs/testing/q02-provider-security-poc.md` |
| SePay reconciliation | HARNESS READY / VERIFICATION REQUIRED | Project Test-mode duplicate/unmatched/mismatch/late-payment drills |
| Cloudinary protected media | HARNESS READY / VERIFICATION REQUIRED | Project-account unsigned/guessed/tampered denial plus authorized same-origin browser/API drill |
| Cloudinary token/URL expiry | DESIGN NOT SELECTED / VERIFICATION REQUIRED | The candidate server proxy exposes no provider URL; record tier/cache/revocation behavior before accepting it |
| OAuth production redirects | NOT TESTED | HTTPS auth, cookie and CSRF E2E |
| PostgreSQL migrations/recovery | NOT TESTED | Fresh & upgraded DB plus backup restore |
| VPS capacity and metrics | NOT TESTED | Load/perf tests and alarms |
| Refund 5%/7d legal/privacy | REVIEW REQUIRED | Appropriate legal/compliance check |
| Manual payout evidence | PROCESS VALIDATION REQUIRED | Transfer approval/reconciliation runbook |

A documentation specification can be approved while gates remain open. A money-bearing production rollout cannot. Record test date/environment/configuration/evidence for each gate; never mark pass from reading docs alone.
