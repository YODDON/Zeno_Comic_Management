# Q02 SePay and Cloudinary provider verification

Q02 is an external-provider release gate, not a replacement for the automated
adapter tests. A passing fixture proves application behavior; only a trace from
the project-owned SePay Test-mode and Cloudinary product environment proves the
provider contract. Never commit credentials, raw banking payloads, account
numbers, provider URLs, public IDs or signed tokens.

## Automated baseline

Run the focused fixture profile from the repository root:

```powershell
dotnet test ComicManagement.slnx `
  --settings backend/tests/q02-provider-security.runsettings
```

This profile covers the real HTTP webhook controller, raw-body HMAC
verification, timestamp rejection, payload normalization, PostgreSQL
dedup/fulfillment/reconciliation, explicit zero-financial-side-effect checks,
authenticated asset upload parameters, the Cloudinary denial-status matrix,
evidence path/redaction rules, namespace/MIME/size guards, and the Reading
fail-closed boundary. It is supplementary evidence and does **not** mark either
provider verified.

## Cloudinary real-account probe

The selected candidate is the existing server-side proxy: the browser receives
only a same-origin content route; the API authorizes every request and fetches
an `authenticated` Cloudinary asset using a signed URL that is never returned
to the client. This avoids treating an ordinary, non-expiring signed URL as a
reader entitlement token.

Set secrets only in the process environment and run:

```powershell
$env:Q02_CLOUDINARY_CLOUD_NAME = '<project environment>'
$env:Q02_CLOUDINARY_API_KEY = '<secret-store value>'
$env:Q02_CLOUDINARY_API_SECRET = '<secret-store value>'
$env:Q02_EVIDENCE_PATH = '<outside-repository>/cloudinary-q02.json'
$env:Q02_HTTP_TIMEOUT_SECONDS = '20'
dotnet run --project backend/tools/ComicManagement.ProviderVerification -- cloudinary
```

The probe uploads a disposable `authenticated` one-pixel asset, verifies that
unsigned, guessed and signature-tampered requests return only an approved
denial status (`401`, `403` or `404`), verifies an internal signed request
succeeds with raster content, then requests invalidating cleanup. `429`, `5xx`,
timeout, DNS/TLS and other transport failures are
`ProviderUnavailable/Inconclusive`, never a protection pass. Its evidence has a
random run ID and status/outcome data only; it contains no provider environment,
public ID, URL or reproducible identifier digest. It deliberately does not
claim browser authorization, cache revocation, token/cookie availability or an
acceptable exposure window.

`Q02_EVIDENCE_PATH` is normalized and rejected if it resolves anywhere inside
the repository. If omitted, the tool creates a new JSON file under the user's
temporary directory. Existing files are never overwritten.

Before accepting the Cloudinary gate, additionally capture a real browser/API
drill for all of the following:

1. entitled CoinUnlock and active VIP can read through the same-origin route;
2. anonymous, wrong-user and expired-VIP requests fail without provider IDs;
3. hide, reprice and asset replacement fail closed on the next content request;
4. copied API URLs require the recipient's current application authorization;
5. responses are `no-store`, `nosniff`, raster-only and within the byte limit;
6. no Cloudinary signed URL appears in manifest, HTML, browser logs or evidence;
7. product tier, CDN behavior, cleanup latency and cost are recorded.

Until that drill is accepted, `VipIncluded` and `CoinUnlocked` continue to
return `503 PROVIDER_UNAVAILABLE`.

## SePay Test-mode trace

Use the official SePay Test mode, not a locally generated HMAC request:

1. Create an isolated Test-mode account and HMAC-SHA256 webhook targeting the
   deployed `/api/v1/webhooks/sepay/ipn` endpoint.
2. Configure the exact receiving account and secret through deployment secret
   storage; never paste either into an evidence file.
3. Create a Pending TopUp order, then simulate an incoming transaction with the
   exact code and amount. Confirm SePay records HTTP 200 and
   `{"success":true}`.
4. Use SePay's Replay action for the same transaction. Confirm both deliveries
   are acknowledged and only one fulfillment exists.
5. Set the non-secret selectors below and run the database verifier for the
   `success-replay` scenario:

```powershell
$env:Q02_POSTGRES_CONNECTION_STRING = '<secret-store value>'
$env:Q02_SEPAY_ORDER_CODE = '<test order code>'
$env:Q02_SEPAY_PROVIDER_TRANSACTION_ID = '<SePay test transaction id>'
$env:Q02_SEPAY_HTTP_STATUS = '200'
$env:Q02_EVIDENCE_PATH = '<outside-repository>/sepay-success-q02.json'
dotnet run --project backend/tools/ComicManagement.ProviderVerification -- sepay success-replay
```

The verifier requires at least two authenticated deliveries and reconciles the
single order/provider transaction/PaidCoinLot/credit operation/ledger/outbox.
It emits no order/provider identity or deterministic identifier hash.

Repeat the real controller/provider drill and invoke the matching verifier name:

```text
sepay unknown-order
sepay amount-mismatch
sepay late-payment
sepay invalid-signature
sepay retry
```

For `invalid-signature`, set `Q02_SEPAY_HTTP_STATUS=401`. For `retry`, set the
final acknowledgement in `Q02_SEPAY_HTTP_STATUS` and the captured first
non-success response (for example `503`) in
`Q02_SEPAY_INITIAL_HTTP_STATUS`. A retry record cannot pass unless the first
attempt is non-2xx, the final attempt is 2xx, multiple deliveries are durable,
and financial artifacts still exist exactly once.

Each JSON record contains a scenario outcome and explicit lot, operation,
ledger and success-outbox assertions. `unknown-order`, `amount-mismatch`,
`late-payment` and `invalid-signature` require zero financial side effects;
authenticated reconciliation events remain durable, while invalid HMAC must
leave no provider transaction or delivery. `retry` requires multiple deliveries
and one final lot/operation/ledger/outbox result. Run each scenario against an
isolated test order/environment so order-scoped zero counts are authoritative.

The automated fixture profile exercises these actual application
controller/adapter/store paths. The command-line verifier does not reimplement
their decisions; it only reconciles persisted authoritative state after the
named project-account drill.

Official references used for this gate:

- https://developer.sepay.vn/en/sepay-webhooks/xac-thuc
- https://developer.sepay.vn/en/tien-ich-khac/test-mode/mo-phong-giao-dich
- https://cloudinary.com/documentation/control_access_to_media

## Gate status

The repository contains automated fixture evidence, live probes and the
redacted project-account procedure. Provider outage evidence is inconclusive,
not a pass. The gate remains **VERIFICATION REQUIRED** until all project-owned
provider traces are executed, reviewed and stored in the approved external
evidence location. A local harness pass or generated JSON by itself is not
deployment proof.
