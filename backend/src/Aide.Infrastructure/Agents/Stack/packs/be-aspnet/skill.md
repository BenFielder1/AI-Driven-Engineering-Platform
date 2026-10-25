<!-- Adapted from stack-packs be-aspnet@1.0.0 skills/be-aspnet/SKILL.md, snapshot 2026-10-25 -->

# ASP.NET Core (C#)

## Detect

Read `*.sln`/`*.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, the Api project (minimal APIs or controllers), the validation library, the auth setup and the test projects. **Mirror the nearest existing feature.** If the repo uses controllers or Razor Pages, keep using them.

## Scaffold

Write these files under the backend path (`<App>` is the product name in PascalCase):
- **Solution:** `<App>.slnx` listing `src/<App>.Api` (`Microsoft.NET.Sdk.Web`), `src/<App>.Domain` and `src/<App>.Infrastructure` (`Microsoft.NET.Sdk` class libraries), `tests/<App>.UnitTests` and `tests/<App>.IntegrationTests` (xUnit).
- **`global.json`:** the SDK version pinned. Use current stable versions you are confident exist, and pin the .NET SDK here.
- **`Directory.Build.props`:** `Nullable` enable, `TreatWarningsAsErrors` true, `ImplicitUsings` enable, analyzers on.
- **`Directory.Packages.props`:** central package management; csproj `PackageReference`s carry no versions.
- **Api:** `Program.cs` ending with `public partial class Program;` (for the test host), `appsettings.json` without secrets, `Properties/launchSettings.json` with the composition's dev port (e.g. 5080), and a `UserSecretsId` in the csproj.
- **`openapi/api.json`:** the initial contract (see Contract export).

## Conventions

- **Domain:** rules, value objects and entity behaviour, with **no** EF, ASP.NET or I/O. Inject `TimeProvider`.
- **Api:**
  - `Features/<Feature>/<Feature>Endpoints.cs` extension methods using `MapGroup("/api/<feature>")`
  - `TypedResults` with `Results<…>` return types, and a stable `.WithName("operationId")` on every endpoint
  - DTOs are `record`s; entities are never returned
- **Async:** pass a `CancellationToken` everywhere. No `.Result` or `.Wait()`.
- **Logging:** `ILogger<T>` with message templates, no PII.
- **Validation:** at the edge, with DataAnnotations (built-in minimal-API validation where available) or FluentValidation if the repo uses it. Return `ValidationProblem`.
- **Packages:** no commercially licensed packages (recent MediatR, AutoMapper and FluentAssertions versions).

## Errors

`AddProblemDetails()`, plus an `IExceptionHandler` for unexpected errors (no stack traces outside Development). Business errors use `TypedResults.Problem(statusCode: …, title: …, extensions: new Dictionary<string, object?> { ["errorCode"] = ErrorCodes.X })`. The codes are constants in a static `ErrorCodes` class mirroring the spec's error table. Optimistic concurrency failures map to 409 `CONFLICT`.

## Auth

- **Accounts, same-origin (default):** ASP.NET Core Identity with cookies (`AddIdentityApiEndpoints<AppUser>()` + `MapIdentityApi<AppUser>()`, or custom endpoints).
- **Cross-origin or external IdP:** JWT bearer validation.
- **Authorisation:** policies (`RequireAuthorization("Policy")`) plus ownership checks in handlers or the domain.
- **CORS:** only for the cross-origin model, with the origin list read from config (`CORS_ALLOWED_ORIGINS`, comma-separated). Never `AllowAnyOrigin` with credentials.

## Contract export

The generator is `Microsoft.AspNetCore.OpenApi` (`AddOpenApi()`, `MapOpenApi()`) plus `Microsoft.Extensions.ApiDescription.Server` for build-time generation. In the Api csproj:
- `<OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)/../../openapi</OpenApiDocumentsDirectory>`
- `<OpenApiGenerateDocumentsOptions>--file-name api</OpenApiGenerateDocumentsOptions>`

The export is `dotnet build src/<App>.Api`, which writes `openapi/api.json`. You can't run it, so **hand-write the change to `openapi/api.json` in the generator's shape**: keep the existing file's `openapi` version, key order, indentation and `components.schemas` naming (in a new repo, the OpenAPI version the installed generator emits). Add each new path with its `operationId` (= `.WithName`), request body, responses (including the problem-details error responses) and schemas. CI regenerates the file and fails on any difference, and that finding comes back to you.

## Config and database URL

- `IOptions<T>` with `.ValidateDataAnnotations().ValidateOnStart()`. Connection string `ConnectionStrings:Default` (env `ConnectionStrings__Default`, ADO.NET/Npgsql format).
- **Platform `DATABASE_URL`** (`postgres://user:pass@host:port/db`, from Render, Railway or Supabase): at startup, if `DATABASE_URL` is set and `ConnectionStrings:Default` is empty, convert it with `NpgsqlConnectionStringBuilder` (Host, Port, Database, Username, Password, `SSL Mode=Require` for managed hosts). Keep this in one place (`Infrastructure/DatabaseUrl.cs`) with unit tests.
- Secrets come from user-secrets in development and env vars elsewhere, never from `appsettings*.json`.

## Health

`/api/health/live` (no dependency checks) and `/api/health/ready` (including `AddDbContextCheck<AppDbContext>()` when there's a database), both anonymous.

## Testing

- **Unit:** domain rules, named by FR ID.
- **Integration:** `WebApplicationFactory<Program>` plus a Testcontainers database of the data pack's engine, with migrations applied and the container shared per collection. Cover success, 400, 401, 403, 404, each errorCode and 409.

## Gate

CI runs, from the backend path:
```bash
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
dotnet build src/<App>.Api && git diff --exit-code openapi/api.json
```
The second line is the contract freshness check.

## Containerise

`Dockerfile` in the backend path, multi-stage:
- **Build** (`mcr.microsoft.com/dotnet/sdk:<ver>`): copy the solution, props and csproj files first and restore (layer caching), then `dotnet publish src/<App>.Api -c Release -o /app/publish`. Build the EF migration bundle here too (see the data pack).
- **Runtime** (`mcr.microsoft.com/dotnet/aspnet:<ver>`): `USER $APP_UID` (non-root), `ENV ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080`, `ENTRYPOINT ["dotnet", "<App>.Api.dll"]`.
- **Behind a proxy:** enable ForwardedHeaders.
- `.dockerignore` excludes `bin/`, `obj/`, `tests/` output and local secrets.

## Deploy notes

- **Render:** Docker web service, health check path `/api/health/ready`.
- **Railway:** the Dockerfile, health check path `/api/health/ready`.
- **Local:** the local pack (Compose).
- **Not supported** on Vercel, Netlify, Cloudflare or GitHub Pages.

## Checklist

- [ ] Domain is pure and tested by FR. Endpoints are thin, use TypedResults and have stable operation IDs.
- [ ] ProblemDetails carry the spec's errorCodes, and policies plus ownership checks are on every protected endpoint.
- [ ] Options are validated on start, `DATABASE_URL` conversion is handled, and there are no secrets in appsettings.
- [ ] The contract matches the endpoints (CI freshness check), and the gate passes.
