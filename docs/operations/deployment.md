# Deployment Baseline — Single VPS

Deploy Caddy HTTPS reverse proxy, React static site, one ASP.NET Core API process with hosted outbox worker, private PostgreSQL persistent volume; Cloudinary and SePay external. Docker Compose networking: Postgres not exposed to public Internet. Same-origin proxy for API and frontend simplifies auth/cookie/CSRF.

## Prerequisites
Domain/DNS and TLS; OAuth Google redirect URI; SePay callback URL reachable and properly authenticated; Cloudinary account with *verified* protected delivery; persistent ASP.NET Data Protection keys and encryption key for bank details; UTC clock sync, VN timezone only for product cycles.

## Pipeline
Restore/build -> unit & architecture tests -> PostgreSQL integration -> frontend types/lint/build -> Docker image -> staging migrations -> staging smoke/E2E -> controlled production deploy -> readiness check. Migration executed once by controlled job. Liveness vs readiness distinct. Deploy rollback plan covers backward-compatible DB changes.

## Monitoring
API 4xx/5xx, provider webhook failed/mismatched, payment reconciliation queue, outbox backlog/poison, wallet reconciliation drift, mission grant failures and daily cap, protected manifest unauthorized attempts; no secrets in logs.

## No invented sizing
VPS RAM/CPU, capacity and bandwidth remain unknown until load test. Never mark deployed or tested based on this document.
