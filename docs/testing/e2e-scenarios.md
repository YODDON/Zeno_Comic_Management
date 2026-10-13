# End-to-End Journeys

1. Guest browses Home/Catalog/Detail, reads Free Chapter, cannot rate/comment until Google login.
2. Google user follows Comic, opens Reader, scrolls and resumes at saved page; qualify only after verified ≥90% AND ≥15s in the absolute 30-minute session.
3. Admin drafts Comic+Chapter, uploads pages, publishes Chapter while Comic Draft: public not visible; publishes Comic and appears; attempt to hide last Published Chapter blocked.
4. User visits locked Chapter, sees quote; after TopUp Pending sees no balance change; verified SePay simulation confirms Paid Coins; quote refresh then Bonus-first Paid FIFO unlock; duplicate submit doesn't double debit.
5. User buys VIP 29,000 VND default, receives 720h after backend fulfillment; eligible Chapter free under VIP while active; other 101-Coin Chapter unlocks for 81.
6. Mission DailyLogin/qualified read/follow/unlock/topup grant correct Bonus, cap 300 and `DeniedDailyCap` shown; delayed worker action uses correct prior day.
7. User requests refund for unspent TopUp, sees 5% fee and one-request warning, submits masked bank recipient. Admin approval reserves original lot, manual transfer recorded; Completed requires evidence. Attempt while spent rejects; second refund after rejection impossible.
8. Admin moderates Hidden comment and restores it with audit; User role blocked from all Admin routes; Admin can't grant own role via UI.
9. UI mobile/desktop: Dark/Light User, Light-only Admin, vertical Reader, responsive Library, Featured reorder by mouse and keyboard.
10. Failure drills: Cloudinary protected URL guessed/copied, SePay duplicate/late webhook, API crash post-transaction, DB backup restored in isolated staging.

Do not assert journeys 4/5/10 pass until provider accounts/configuration are actually tested.
