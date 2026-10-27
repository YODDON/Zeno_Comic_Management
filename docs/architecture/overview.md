# Architecture Overview

Comic Management is a Vietnamese manga/comic image-reading product with free, coin-locked and VIP-eligible chapters. Admin publishes content; users login via Google and can follow, rate, comment, resume reading, purchase Coins/VIP, complete Missions and request TopUp refunds.

## Stack
- Backend ASP.NET Core .NET 10 / EF Core 10 Modular Monolith; one API process, with hosted outbox worker for MVP.
- Database PostgreSQL; schemas `identity`, `catalog`, `reading`, `interaction`, `billing`, `wallet`, `membership`, `missions`, `administration`, `infrastructure`.
- Frontend React/TypeScript/Vite/Tailwind/shadcn/ui/React Router/TanStack Query.
- Docker Compose on a VPS; Caddy TLS, private Postgres, external Cloudinary and SePay.

## Production projects
`ComicManagement.Api`, `.Workflows`, `.Persistence`, `.Shared`; `.Identity`, `.Catalog`, `.Reading`, `.Interaction`, `.Billing`, `.Wallet`, `.Membership`, `.Missions`, `.Administration` = **13** projects, plus test projects. Module internals hidden through contracts; Workflows coordinate cross-module financial and entitlement activity.

## Core event flow
Verified SePay transaction -> order matching -> shared transaction -> PaidCoinLot or VIP extension + ledger where applicable + outbox -> Missions action processing. User chapter unlock -> quote check -> shared transaction of Wallet bonus/paid lot allocations + Reading unlock + ledger + outbox. Both must survive duplicate calls and worker retries.

## Non-goals
Microservices, password auth, n8n, XP/levels, recommendation ML, auto bank refunds, page-by-page reader, paid VND per chapter.

## Status
Architecture/policy frozen for specification generation; concrete provider integrations, migrations and production performance are **unverified**.
