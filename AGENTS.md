# Repository engineering instructions

## Architecture and scope

- Preserve production dependency direction: `Host -> Application, Infrastructure`; `Infrastructure -> Application`; `Application -> Domain`; Domain has no solution dependencies.
- Put domain invariants and meaningful state transitions in Domain. Keep it independent of ASP.NET Core, Telegram, persistence frameworks, and external provider SDKs.
- Put use-case orchestration and workflow policy in Application, organized by specific use-case slices as described in `docs/OwnDay-MVP-Architecture.md`. Keep external adapters and their implementations in Infrastructure.
- Keep Host responsible for composition, configuration, HTTP authentication, endpoints, and middleware. Keep business rules out of controllers and transport routers.
- Use code and project files as the source of truth for current implementation. Consult relevant `docs/` requirements when changing behavior; distinguish proposed architecture from implemented capabilities.
- Keep changes within the requested scope. Do not introduce unrelated refactoring, new subsystems, or generalized abstractions to implement future roadmap items.
- Inspect for an existing equivalent abstraction before introducing a service, DTO, wrapper, or architectural concept.
- Do not commit, amend, rebase, reset, or otherwise rewrite history unless explicitly requested. Do not discard or overwrite pre-existing user changes.

## C# and dependencies

- Preserve the shared settings in `Directory.Build.props`: .NET 10, nullable reference types, implicit usings, warnings as errors, and build-time code-style enforcement. Fix warnings rather than weakening these settings.
- Follow surrounding code: file-scoped namespaces matching folders, Allman braces, PascalCase members, `_camelCase` private fields, and readonly dependency fields. Prefer constructor injection; follow surrounding code when choosing explicit or primary constructors.
- Follow existing use of sealed implementation classes and records for immutable commands and results.
- Keep I/O asynchronous. Accept and propagate `CancellationToken` through handlers and external calls; do not replace an available caller token with `CancellationToken.None` or silently omit it.
- Declare explicit package versions in the consuming `.csproj`, following the existing project format. Keep provider dependencies in their owning layer and avoid unrelated package additions or upgrades.

## ASP.NET Core, logging, and secrets

- Keep application wiring in Host and group adapter registrations in `IServiceCollection` extension methods. Choose lifetimes compatible with dependencies; singleton services must be safe for concurrent requests.
- Follow the existing attribute-routed controller pattern for webhook handling and minimal endpoint pattern for operational endpoints. Preserve the public partial `Program` entry point used by integration tests.
- Bind configuration through typed options and validate required values on startup. Use the existing centralized exception handling and Problem Details pipeline.
- Use the configured Serilog logging pipeline. Never commit or log real credentials, bot tokens, webhook secrets, or private keys; supply secrets through environment/runtime configuration and use dummy values in tests.

## Telegram boundaries

- Authenticate webhook requests before dispatching updates. Preserve rejection of missing, invalid, or multiple secret-header values and fixed-time secret comparison.
- Keep Telegram parsing/routing separate from outbound delivery. Do not let `Telegram.Bot` types leak into Domain contracts or core behavior.
- When implementing future domain-changing Telegram flows, ensure update deduplication, domain mutation, and outbound message intent commit together, with delivery through a transactional outbox, as required by the architecture documentation. This is a design constraint, not an assertion that the mechanism already exists.

## Tests and validation

- Add or update tests for changed behavior. Organize unit tests by feature/responsibility and use xUnit facts/theories with `Method_Scenario_ExpectedBehavior` names and built-in assertions.
- Prefer small recording fakes at external boundaries. Use `WebApplicationFactory<Program>` for HTTP, serialization, configuration, and DI integration tests; replace outbound Telegram services so tests do not contact the live API or require production secrets.
- Cover relevant rejection/ignore paths and cancellation propagation, as well as successful behavior. Use realistic Telegram JSON when testing webhook serialization.
- For code, dependency, or build changes, normally run the CI sequence from the repository root:

  ```sh
  dotnet restore OwnDay.slnx
  dotnet build OwnDay.slnx --configuration Release --no-restore
  dotnet test OwnDay.slnx --configuration Release --no-build
  ```

- Review the final diff for scope and whitespace errors. For documentation-only changes, review content and run `git diff --check`; builds and tests are unnecessary. Report validation performed and any checks that could not run.
