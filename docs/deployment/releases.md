# OwnDay releases

1. Choose a [SemVer 2.0](https://semver.org/) version. Increase MAJOR for incompatible changes,
   MINOR for compatible features, or PATCH for compatible fixes. Use a prerelease suffix such
   as `-rc.1` when appropriate. Each numeric component of MAJOR.MINOR.PATCH must fit the .NET
   assembly version range of 0 through 65534.
2. Create an annotated tag on the commit you want to release. The commit must contain the
   release workflow and versioning changes.

   ```sh
   git tag -a v0.7.0 <release-commit-sha> -m "Release 0.7.0"
   git push origin refs/tags/v0.7.0
   ```

3. Follow the [Deploy Production workflow](../../.github/workflows/deploy.yml). CI validates
   the tag, checks that it points to the workflow commit and strips the leading `v`.
   Tests and Docker publish use that commit and the same version. The image records both
   in its assembly and OCI labels. The VPS pulls the image by digest and runs the existing
   migration and startup steps. The GitHub `production` environment rules apply.
4. Send `/version` to the bot and check for `OwnDay 0.7.0`. The startup log and HTTP
   `/version` endpoint also report the commit SHA.
5. Fix a release on a new commit and publish a new tag, for example `v0.7.1` or `v0.8.0-rc.2`.
   Leave published tags at their original commits.

Branch builds use `0.0.0-dev`. A subsequent push to `main` still deploys automatically and
can replace a release with a dev build, even when both runs use the same commit. Image tags
`latest` and `sha-<commit>` are aliases. The deployed image digest identifies the exact build.

The .NET SDK appends the SHA once to `InformationalVersion`, for example
`0.0.0-dev+<sha>` or `0.8.0-rc.1+<sha>`. A local `dotnet build` defaults to
`0.0.0-dev` and the SDK reads the checkout's SHA.
For a local Docker build or a source archive, supply the source SHA explicitly to retain it
(`OWNDAY_SOURCE_REVISION_ID` for Docker, `-p:SourceRevisionId=<sha>` for .NET).
Without Git metadata or an explicit SHA, the revision is unknown.
