# Unverified Items and Production Release Gates

| Gate | Status before implementation | Evidence needed |
|---|---|---|
| SePay checkout/QR | VERIFICATION REQUIRED | Actual product choice & tested instructions |
| SePay webhook HMAC/payload/retry | VERIFICATION REQUIRED | Redacted real fixture + signature and contract tests |
| SePay reconciliation | VERIFICATION REQUIRED | Duplicate/unmatched/late-payment drills |
| Cloudinary protected media | VERIFICATION REQUIRED | Unauthorized/cached/transformed URL access POC |
| Cloudinary token/URL expiry | VERIFICATION REQUIRED | Supported tier and observed expiry/revocation |
| OAuth production redirects | NOT TESTED | HTTPS auth, cookie and CSRF E2E |
| PostgreSQL migrations/recovery | NOT TESTED | Fresh & upgraded DB plus backup restore |
| VPS capacity and metrics | NOT TESTED | Load/perf tests and alarms |
| Refund 5%/7d legal/privacy | REVIEW REQUIRED | Appropriate legal/compliance check |
| Manual payout evidence | PROCESS VALIDATION REQUIRED | Transfer approval/reconciliation runbook |

A documentation specification can be approved while gates remain open. A money-bearing production rollout cannot. Record test date/environment/configuration/evidence for each gate; never mark pass from reading docs alone.
