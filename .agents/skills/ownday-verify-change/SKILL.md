---
name: ownday-verify-change
description: Verify an OwnDay change before completion, including code, configuration, documentation, or skills, by reviewing the final diff and reporting checks actually performed.
---

# Verify an OwnDay change

1. Check the working tree and review the complete final diff, including new files. Look for accidental scope creep, temporary or debug code, whitespace errors, and committed or logged secrets. Do not overwrite pre-existing user changes.
2. Choose validation based on the changed files and risks. Narrower tests are useful during iteration. For production code, build, dependency, or runtime configuration changes, normally run from the repository root:

   ```sh
   dotnet restore OwnDay.slnx
   dotnet build OwnDay.slnx --configuration Release --no-restore
   dotnet test OwnDay.slnx --configuration Release --no-build
   ```

3. For documentation-only or skill-only changes, review the content and run `git diff --check`; a full .NET build is unnecessary unless a concrete risk calls for it. Never disable warnings, analyzers, nullable checks, or code-style enforcement to make validation pass.
4. Confirm that tests exercise changed behavior at the right level, including relevant rejection and cancellation cases, rather than merely mirroring implementation. For architecturally significant changes, use or recommend `ownday-architecture-review` when available.
5. Report exactly which commands and checks ran, their results, and any checks that could not run. Do not claim validation passed for a command that was not executed.
