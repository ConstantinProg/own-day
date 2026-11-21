---
name: ownday-release
description: Prepare, dry-run, deploy, verify, or publish an OwnDay production release through GitHub Actions and an annotated SemVer tag. Use for release planning and execution.
---

# Release OwnDay

Use Russian for the plan and report, and English for GitHub Release notes. Follow [AGENTS.md](../../../AGENTS.md) and [the release guide](../../../docs/deployment/releases.md). Read [Deploy Production](../../../.github/workflows/deploy.yml), [the VPS setup guide](../../../docs/deployment/GITHUB_SECRETS.md), [production Compose](../../../deploy/compose.yaml), and the [Dockerfile](../../../Dockerfile). Check the workflow at the candidate commit and each ref involved. Confirm the effective `production` rules in GitHub and check the VPS Compose file through authorized access when its contents affect the plan. The workflow uses an existing VPS `compose.yaml`; a repository change alone does not update it.

## Dry run

For a dry run, skill installation, or planning-only request, do read-only discovery and present a simulated approval plan. Inspect local and remote refs, commit and migration diffs, and available GitHub Actions metadata. Leave Git history, the index, and refs unchanged. Skip commits, tags, pushes, workflow dispatches or reruns, Telegram messages, production writes, and all GitHub Release mutations. Mark simulated and unavailable checks explicitly.

## Prepare the candidate

1. Check the worktree, index, untracked files, remotes, and live `origin/main`. Confirm the branch relationship and protections, the previous relevant release tag and any published GitHub Release, and the full candidate SHA. Review the changes between the previous tag and the candidate. Use an explicit clean commit for the release. Commit local changes only with explicit authorization for the selected files and follow the repository's Conventional Commits guidance.
2. Propose an available `v<SemVer>` tag from the compatibility impact and the actual changes. Validate explicit version overrides and the documented .NET numeric limits. The tag supplies the release version. Skip version-only commits and change `CHANGELOG.md` only on explicit request. Draft English notes from the candidate diff, including material operational changes or limitations.
3. Use [ownday-verify-change](../ownday-verify-change/SKILL.md) for release-candidate validation. Include PostgreSQL integration tests when the candidate needs them. Check that tests cover the target version and SHA as CI does. Record actual results and any gaps.

## Review PostgreSQL migrations

Use authorized read-only EF migration history to determine what the bundle will apply on production. Inspect the pending `Up` operations, raw SQL, and any data changes, including locks, validation scans, rewrites, and compatibility with the running application. The workflow updates `.env`, starts PostgreSQL as needed, runs the same image's one-shot EF bundle, and then replaces `app`. The old app can keep serving during migration. Assess both that overlap and whether the old app could safely read data written by the new version.

For material schema or data risks, rehearse the upgrade on isolated PostgreSQL with representative existing data and confirm a usable backup, recovery plan, and any required maintenance window before approval. Pause if migration state or material risk remains unresolved. Use the existing forward bundle in GitHub Actions. Destructive SQL, migration downgrades, restores, and application rollbacks require a separate, explicitly approved recovery plan. An application rollback requires proof of compatibility with the current schema and data.

## Approve the plan and deploy

Present the version, full target SHA, branch and working-tree state, planned ref changes and deployment sequence, actual check results, pending migrations and risks, English notes, and production verification criteria. Identify access gaps that would block execution or verification. Obtain explicit user approval before any release action that writes to GitHub or can trigger production, including a push or merge to `main`. Include GitHub Release publication as conditional on verification. Reconfirm material changes to the target SHA, tag, migration risks, or sequence. If a merge will generate an unknown SHA, obtain approval for that merge and its deployment effect, then validate and obtain confirmation for the resulting SHA before tagging. Follow any separate GitHub environment reviewer rules for each run.

To resume verification or publication for an existing tag, confirm the remote annotated tag resolves to the intended full SHA. Trace its exact tag-push run and go directly to production verification. Keep the existing tag and deployment intact, and apply the same publication approval and evidence requirements.

1. Confirm remote refs, tag uniqueness, and the allowed branch and tag patterns for `production`. Coordinate a release window without competing pushes, merges, manual dispatches, or reruns. Check for queued, waiting, and in-progress production runs. The workflow shares the `production` concurrency group across `main`, `v*`, and manual runs. Serialization does not guarantee dispatch order.
2. If the approved commit must reach `main`, push only the approved branch ref or use the authorized merge mechanism. A `main` run builds `0.0.0-dev` and may deploy it. Match that push to its exact `main` run and wait for the run to finish before the tag push. Require successful tests and resolve unexpected failures first. If verified environment rules intentionally disallow deploys from `main`, confirm the run reached that expected gate after tests passed. If `main` already points at the approved SHA, skip the branch push and check the existing run and current rollout state. Resolve any unexpected failure or partial deployment before tagging.
3. Recheck that `origin/main` is the approved SHA, the deployment queue is clear, and the tag is still unused. Create an annotated tag pointing to the full approved SHA and push only its explicit `refs/tags/...` ref. Use separate pushes for `main` and the tag and disable Git's `push.followTags` for each. Preserve published tags at their original commits.
4. Monitor the exact tag-push `Deploy Production` run by event, tag ref, and SHA through tests, image publish, migration, rollout, and health verification. Record the published image digest. A later branch or manual deployment can replace the release even at the same SHA.

## Handle failures

Inspect sanitized logs and determine whether the failure occurred before or after changes to the image reference, database, or running containers. Before publication, make only focused fixes within the authorized task and rerun affected checks. Use [ownday-implement-change](../ownday-implement-change/SKILL.md) when a code fix is needed. A code fix after tag publication needs a new commit, a new version and tag, and renewed approval. Consider at most one retry of the exact failed run for a diagnosed transient fault when the resulting state and retry safety are known. A retry that can deploy must be covered by explicit approval. Pause after a failed retry or an uncertain migration or rollout state. Leave CI/CD changes, production edits, and recovery actions for separately authorized work.

## Verify production and publish

Confirm the expected tag run succeeded, including the EF bundle. Verify current `/health` and PostgreSQL connectivity through the `postgresql` readiness check in `/health/ready` or equivalent authorized evidence. `/health/live` only confirms liveness. Check the expected applied migrations against read-only EF history when available and review accessible post-deploy logs for new critical failures. Agree the observation window and evidence for these checks in the release plan.

Read live HTTP `/version` and match both `version` and full `commitSha`. If HTTP access is unavailable, obtain equivalent runtime evidence, such as the deployed instance's startup log. The bot's `/version` only confirms the version. Match the running `app` container's image to the digest from the tag run. A configured `.env` reference and the mutable `latest` and `sha-<commit>` aliases are insufficient. Check again immediately before publication for competing runs and verify the approved release is still live. Pause publication if a required check fails or lacks evidence.

Check for an existing GitHub Release and verify the published annotated tag still resolves to the approved SHA. Publish against that existing tag with the approved English notes, using `--verify-tag` if using `gh release create`. Mark SemVer prereleases as prereleases. Report the version, SHA, digest, verification results, links to the run, tag and Release, and any unresolved gap.
