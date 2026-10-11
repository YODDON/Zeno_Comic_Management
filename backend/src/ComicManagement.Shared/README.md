# ComicManagement.Shared

`ComicManagement.Shared` contains only business-neutral contracts used by more than one production project.

- `Errors`: transport-neutral error categories and success/failure results. API code is responsible for mapping these categories and stable domain error codes to the documented ProblemDetails response.
- `Time`: an injectable UTC clock. Domain-specific local calendar rules, including `Asia/Ho_Chi_Minh`, remain in their owning modules.
- `Identity`: the current authenticated-user contract. The API host will supply it after Google authentication; the contract itself does not authenticate users or enforce authorization policies.

The project must not reference ASP.NET Core, persistence, or a business module.
