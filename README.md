# Comic Management

This repository contains the approved product blueprint and the .NET 10 backend foundation. Begin with [`AGENTS.md`](AGENTS.md), then [`docs/architecture/decisions.md`](docs/architecture/decisions.md), [`docs/implementation/roadmap.md`](docs/implementation/roadmap.md), and the relevant domain/API/ERD/UI files.

Frozen MVP decisions originated from the user's approved planning rounds. Detailed implementation-specific fields and provider integration shapes still require validation. **SePay and Cloudinary remain Verification Required before paid production.**

Run task-by-task using [`.agent/PLANS.md`](.agent/PLANS.md).

## Backend foundation

The backend is one ASP.NET Core modular monolith composed from 13 production projects under `backend/src`. Project-reference boundaries are enforced by `ComicManagement.ArchitectureTests`.

```powershell
dotnet build ComicManagement.slnx --configuration Release
dotnet test backend/tests/ComicManagement.ArchitectureTests/ComicManagement.ArchitectureTests.csproj --configuration Release
```
