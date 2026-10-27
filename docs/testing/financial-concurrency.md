# Financial & Concurrency Test Matrix (PostgreSQL Required)

| ID | Setup/action | Must hold |
|---|---|---|
| W01 | New user wallet | All balances 0 |
| W02 | TopUp 100,000 VND | Granted lot 10,000 Paid exactly once |
| W03 | 30 Bonus + Paid, unlock cost 80 | Debit 30 Bonus, 50 Paid |
| W04 | Paid lots 20 then 100, spend 70 | FIFO consumes 20+50 |
| W05 | Insufficient available | Zero debit/entitlement |
| W06 | Two simultaneous unlocks same chapter | One entitlement/debit |
| W07 | Refund reserve 10,000 | PaidAvailable−10k, PaidReserved+10k, lot.reserved+10k |
| W08 | Confirm successful refund | Reserved−10k, refunded+10k, never spent+10k |
| W09 | Confirm failed payout | Reserved→Available, refunded unchanged |
| W10 | Failure after a debit write, before commit | Full rollback |
| B01 | TopUp 9k / 10.5k / 2,001k | Validation reject |
| B02 | Duplicate/different-delivery same provider transaction | One Paid grant |
| B03 | Wrong amount/account/signed event failure | No fulfillment |
| B04 | Paid event after order expired | Reconciliation record, no silent credit |
| V01 | VIP first purchase | fulfilledAt +720h |
| V02 | VIP existing + two concurrent orders | Both 720h intervals, no lost extension |
| V03 | Same VIP order IPN retry | One VIP transaction |
| V04 | 101 coin chapter with VIP discount | 81 coins |
| RF01 | Refund within 7 days unspent lot | Requested; reserve only on Admin approve |
| RF02 | Refund after 7 days | Refuse |
| RF03 | Refund second request after Rejected | Conflict, unique order |
| RF04 | Lot 9,999/10,000 available due to spent | Reject full refund |
| RF05 | Bonus/other Paid lot available | Not eligible if original lot insufficient |
| RF06 | Concurrent Approve | One reservation |
| RF07 | Transfer result unknown | Hold reservation, NeedsReconciliation |
| RF08 | Repeated Complete | No double refunded debit/transfer |
| M01 | Daily 250 granted + proposed 60 | DeniedDailyCap all 60 |
| M02 | Two grants concurrent near 300 | Cap never exceeded |
| M03 | Action 23:59 processed 00:01 VN | Charged to action date, correct revision |
| M04 | Retry after worker crash | No duplicate Bonus ledger/grant |

Test actual locks/uniques using concurrent PostgreSQL sessions, not just in-memory mock. Include assertion that `SUM(lotAvailable)=wallet.paid_available` and `SUM(lotReserved)=wallet.paid_reserved` after every high-risk scenario.
