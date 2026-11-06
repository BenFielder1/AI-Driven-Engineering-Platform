---
title: Releases
parent: Operations
nav_order: 5
---

# Releases

Tevrin's versions are numbered [semver](https://semver.org/), `x.y.z`. Each finished version is released as a Git tag and a GitHub release, and publishing the release deploys it to Railway `production`.

## Version numbers

| Specified as | Version |
| --- | --- |
| v1 to v5 | 0.1.0 to 0.5.0 |
| v6 | 0.6.0 |
| Next feature version | 0.7.0 |
| A fix-only release of 0.6.0 | 0.6.1 |

Spec IDs (`D-V6-1`), folder names (`docs/v6/`) and these history page URLs keep `vN`, which means version 0.N.0.

The version is written in three places, and CI fails if they differ (`scripts/ci/check-version.sh`):

- `<Version>` in `backend/Directory.Build.props`, used by every assembly and both CLI packages
- `version` in `frontend/package.json` and its lock file
- the newest entry of the marketing changelog, `frontend/src/features/marketing/content/changelog.ts`

Bump all three when work on a new version starts.

## Before the first release

Before the first release, the `production` GitHub environment must exist (Settings → Environments → New environment `production`) with Ben as **required reviewer**, the `RAILWAY_TOKEN`, `RAILWAY_API_TOKEN`, `SMOKE_EMAIL` and `SMOKE_PASSWORD` secrets and the `RAILWAY_WEB_URL` variable, and **Deployment branches and tags** set to *Selected branches and tags*: branch `main` **and** tag `*.*.*`. A release run's ref is the tag, so a `main`-only rule rejects it. If the environment doesn't exist, publishing a release creates it with no reviewer.

Optionally, add a tag ruleset (Settings → Rules → Rulesets → New tag ruleset, target `*.*.*`, restrict updates and deletions) so release tags can't be moved.

## Releasing a version

The tag is `x.y.z` with no `v` prefix, on the `main` merge commit of the version. Ben makes the release, or an agent makes it after Ben confirms the version and the commit.

1. Merge the version's pull request into `main`, and wait for CI to pass on the merge commit. Before merging, the root `SPEC.md` and `PROGRESS.md` have moved to `docs/v<N>/`, and the changelog entry's `date` is set to the release day.
2. Find the newest `main` commit with a green CI run. CI skips commits that only change docs, so this may not be the head of `main`. Check the version on that commit, and tag it:

   ```bash
   git fetch origin
   gh run list --workflow ci.yml --branch main --status success --limit 1 --json headSha
   git checkout --detach <sha>
   bash scripts/ci/check-version.sh 0.6.0
   git tag -a 0.6.0 -m "Tevrin 0.6.0"
   git push origin 0.6.0
   ```

3. Create the release from the tag. Base the notes on the version's history page and changelog entry:

   ```bash
   gh release create 0.6.0 --verify-tag --latest --title "Tevrin 0.6.0" --notes-file notes.md
   ```

4. Publishing the release starts `.github/workflows/deploy.yml`. Approve the `production` environment when GitHub asks, then watch the deploy and its smoke test:

   ```bash
   gh run list --workflow deploy.yml --event release --limit 1
   gh run watch <run-id> --exit-status
   ```

Never move or delete a tag that a release uses. If the release needs a fix, release the next patch version.

## What the deploy checks

Before anything reaches production, the release deploy:

- fails if the tag isn't `x.y.z`
- skips with a notice if the release is a pre-release, or isn't the repository's latest release, so a release for an older version never rolls production back
- fails if the tagged commit isn't on `main`, has no green CI run, has a different version than the tag, or its changelog entry has no date

Promoting a pre-release to a full release doesn't start a deploy (GitHub sends `released`, not `published`). Publish a new patch version instead.

It then runs the same steps as every production deploy: the `railway config plan` drift check, `api` then `web`, the private API check and the smoke test. See [Railway](railway.md). If the smoke test fails, the job summary lists the rollback steps.

Manual dispatch of the workflow with `environment=production` still works, for example to redeploy.

## Releases for 0.1.0 to 0.5.0

Tags for the earlier versions are optional. If you add them, create their releases with `--latest=false`. The deploy skips any release that isn't the latest, so they never deploy.
