# Build progress (0.6.1)

Spec: `SPEC.md` (0.6.1; 0.1.0–0.6.0 in `docs/`) · Stack: `STACK.md` · Branch: `build/tevrin-0.6.1` (part 1, PR #38), `build/tevrin-0.6.1-review` (part 2) · Started: 2026-11-11

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

## Environment
- Packs (STACK.md): `fe-nextjs`, `be-aspnet`, `data-efcore-postgres`, `vcs-github`, `deploy-railway`, `local-docker-desktop-k8s`. All installed.
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben).
- VCS CLI auth: gh yes (BenFielder1). Railway deploys run only through `.github/workflows/deploy.yml`.

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-232 | `tevrin/` prefix for new branches, recorded branches kept | 3.1, 4.35 | backend | M46 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-233 | Branch control requires every prefix the project uses | 3.1, 4.36 | backend | M46 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-234 | Tevrin-written config blocks `tevrin/**` and `aide/**` | 3.1 | backend (packs) | M46 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-235 | Themed CSS scrollbar | 3.2, 4.39 | frontend | M47 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-236 | CI failure log excerpts (GitHub, GitLab, Bitbucket) | 3.3, 4.38 | backend | M49 | done | [CI 43209207203](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43209207203) |
| FR-237 | Failure findings with excerpts in review prompt | 3.3 | backend | M49 | done | [CI 43209207203](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43209207203) |
| FR-238 | `code_review.md` v9 failure-step rules | 3.3 | backend (prompt) | M49 | done | [CI 43209207203](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43209207203) |
| FR-239 | Failure evidence block appended to fix steps | 3.3 | backend | M49 | done | [CI 43209207203](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43209207203) |
| FR-240 | Repeated-failure marking across cycles | 3.3, 4.37 | backend | M49 | done | [CI 43209207203](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43209207203) |
| FR-241 | `requiredFiles` in deploy packs, catalog 1.2.2 | 3.4, 4.40 | backend (packs) | M48 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-242 | `plan_generator.md` v7 names required files | 3.4 | backend (prompt) | M48 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-243 | Deterministic missing-file steps | 3.4, 4.41 | backend (domain) | M48 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-244 | "Added by Tevrin" badge, additive step fields | 3.4 | backend + frontend | M48 | done | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-245 | Version 0.6.1, history page, docs | 3.5 | deploy + docs | M46, M50 | partial (version bump done) | [CI 43183692831](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/43183692831) |
| FR-246 | Stable finding ids for deterministic findings | 3.6, 4.42 | backend (domain) | M51 | todo | |
| FR-247 | `code_review.md` v10 step-or-waiver, carried/waived marks | 3.6 | backend (prompt) | M52 | todo | |
| FR-248 | Tevrin adds steps for unaddressed findings | 3.6, 4.43 | backend (domain) | M52 | todo | |
| FR-249 | Valid waivers lift the penalty; guarded | 3.6, 4.43 | backend + frontend | M52 | todo | |
| FR-250 | Report shows each finding's handling | 3.6 | frontend + backend | M52 | todo | |
| FR-251 | Catch-all spec paths read whole and covered by fallbacks | 3.7, 4.44 | backend (domain) | M51 | todo | |
| FR-252 | File-based routes and `public/` count as endpoints | 3.7, 4.45 | backend (domain) | M51 | todo | |
| FR-253 | Runtime dependencies only | 3.7, 4.46 | backend (domain) | M51 | todo | |
| FR-254 | Build check starts after execution | 3.8, 4.47 | backend (job) | M53 | todo | |
| FR-255 | Build check waits for preview + required checks | 3.8, 4.47 | backend | M53 | todo | |
| FR-256 | Build-fix agent and `build_fix` steps | 3.8, 4.48 | backend | M53 | todo | |
| FR-257 | Guardian rejects error suppression in build fixes | 3.8, 4.48 | backend (domain) | M53 | todo | |
| FR-258 | `maxBuildFixAttempts`, exhausted → review | 3.8, 4.47 | backend + data | M53 | todo | |
| FR-259 | Build check panel, Build fix badge, setting | 3.8 | frontend + backend | M53 | todo | |

## Phases
- [x] M46 Version and branch prefix (FR-232–FR-234, FR-245 bump)
- [x] M47 Scrollbars (FR-235)
- [x] M48 Deployment files in plans (FR-241–FR-244)
- [x] M49 Failure evidence in review (FR-236–FR-240)
- [ ] M51 Check accuracy and finding ids (FR-246, FR-251–FR-253) — branch `build/tevrin-0.6.1-review`
- [ ] M52 Findings in the plan (FR-247–FR-250)
- [ ] M53 Build check before review (FR-254–FR-259)
- [ ] M50 Release 0.6.1 (after M53) (FR-245): build PR #38 open; after Ben tests on preview, `release/0.6.1` PR with the release prep and docs (docs stashed locally as "0.6.1 docs for release branch"); then tag and release after Ben confirms

## Decisions
See SPEC §10 (D-0.6.1-1 … D-0.6.1-10). Build-time decisions are added below.

## Known issues
- CI integration shards hit the runner's inotify limit (128) once the rest shard grew; CI now raises it and turns off config reload in test hosts (commit 50f20bb).
- Cloudflare: a transient failure reading Workers triggers reports all patterns missing (Error) rather than Unknown (review Minor, M46).
- Cloudflare Pages: verify now writes preview excludes (sets previews to `custom` if a user switched back to `all`); link already did (D-0.6.1-9).
- Changelog 0.6.1 `specHref` → `docs/history/0.6.1.md`, added in M50.

## Manual checks (Ben)
- GitHub job-log redirect: confirm the blob-storage request carries no `Authorization` header (relies on .NET's cross-host redirect stripping; not unit-tested).
- Live log fetches on a real failing check: GitHub Actions job log (redirect to blob storage), GitLab job trace, Bitbucket Pipelines step log (status URL → build number; redirects not followed).
- A real Vercel project end to end: a missing `vercel.json` gets an added step, and a broken build produces a specific fix step with the log excerpt.
- Scrollbar in Chrome, Firefox and Safari, light and dark.
- Cloudflare Workers Builds `PATCH /builds/triggers/{uuid}` with only `branch_excludes` against a real account (stubbed in tests).
- Docker Desktop redeploy and smoke test.
