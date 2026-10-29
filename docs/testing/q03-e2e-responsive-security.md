# Q03 E2E, responsive and security verification

Q03 is a verification and test-hardening slice. It does not add financial
business rules, provider behavior or a new authorization mechanism.

## Evidence layers

Run frontend component and contract tests:

```powershell
cd frontend
npm test
npm run lint
npm run build
```

Run deterministic browser UI journeys at desktop and mobile widths:

```powershell
npm run test:e2e
```

Run browser through real ASP.NET Core controllers and PostgreSQL Testcontainers:

```powershell
$env:DOTNET_HOST_PATH = '<path-to-dotnet>' # optional when dotnet is on PATH
npm run test:e2e:integration
```

This is a **real application E2E with test authentication**. The browser calls
real ASP.NET Core controllers, authorization policies, application workflows and
PostgreSQL; `X-R03-E2E-User` replaces the live Google login ceremony only. It is
not production OAuth E2E evidence.

The integration matrix executes all journeys at desktop 1440x900. The Q03
responsive/security journey additionally executes at mobile 390x844 and tablet
720x900. It verifies the rendered Catalog grid at 2, 4 and 5 columns,
respectively; the mobile Reader toolbar starts visible, hides while scrolling,
can be shown again by touch, and exposes a keyboard-focusable back control. A
separate mobile context sets `prefers-reduced-motion: reduce` and verifies the
Reader's transition/animation durations are reduced while the toolbar remains
usable. The journey also verifies anonymous Free Reader content,
`no-store`/`nosniff`, accessibility, User-to-Admin role denial and authenticated
CSRF rejection.

Run the focused API security profile from the repository root:

```powershell
dotnet test backend/tests/ComicManagement.Api.IntegrationTests/ComicManagement.Api.IntegrationTests.csproj `
  --settings backend/tests/q03-security.runsettings
```

The focused profile provides production-auth configuration and integration
evidence without contacting Google. It asserts the production session and
antiforgery cookie flags, PKCE/code-flow, correlation/nonce cookie settings and
token-validation settings. Separate real-pipeline tests assert anonymous `401`,
standardized insufficient-role `403`, Admin policy enforcement and unsafe-route
CSRF behavior. The deterministic token endpoint completes the framework OIDC
flow locally; it does not claim live Google/provider verification.

## Security evidence boundaries

- **Responsive browser evidence:** Chrome projects at 390px, 720px and 1440px,
  plus the 390px reduced-motion project.
- **Test-auth real E2E:** browser -> ASP.NET Core -> PostgreSQL with
  `X-R03-E2E-User`; production controllers and business paths, test login only.
- **Production-auth security/config evidence:** the focused API profile using
  the normal cookie/OIDC/authorization/antiforgery registrations and a local
  deterministic Google token endpoint.
- **Adversarial audit evidence:** A04 PostgreSQL/API tests remain the primary
  proof for nested sensitive-field redaction. The Admin Audit browser journey
  additionally reads a seeded adversarial record through the real backend and
  proves sensitive values are absent while safe metadata remains visible.

## Acceptance traceability

| Journey | PostgreSQL/HTTP evidence | Browser/responsive evidence |
|---|---|---|
| Guest Catalog/Detail/Free Reader | `Public_catalog...`, `Reader_access...`, real Q03 browser journey | 390px/720px/1440px grid, mobile toolbar, reduced motion, axe |
| Follow/read/resume | `reading-library.integration.e2e.ts` | mock desktop/mobile reading journey plus real desktop flow |
| Admin content and publication | Catalog publication/API tests and `admin-catalog.integration.e2e.ts` | Admin editor mock desktop/mobile |
| Finance/refund operator flow | RF02/RF03 persistence/API suites and `admin-finance.integration.e2e.ts` | explicit confirmations and masked evidence |
| Missions/VIP/User Admin | M02/V01/I02 suites and `admin-platform.integration.e2e.ts` | Admin mock desktop/mobile |
| Comments/moderation | focused Q03 API security profile plus Interaction persistence tests | Comment component/Admin moderation tests |
| Dashboard/audit | A04 adversarial persistence/API tests and `admin-reporting.integration.e2e.ts` | adversarial fixture values absent; safe metadata visible |
| Production auth/authorization/CSRF | focused Q03 API profile with normal production registrations | configuration plus HTTP 401/403/CSRF evidence; no live Google claim |
| Test-auth application security flow | real Q03 browser journey | User denied Admin UI, CSRF rejected, content headers asserted |

Journeys that depend on actual SePay or protected Cloudinary delivery remain
`Verification Required`. Fixture webhook, payment, VIP and protected-content
tests are supplementary and must not be presented as project-account provider
verification. Backup/restore belongs to D01.
