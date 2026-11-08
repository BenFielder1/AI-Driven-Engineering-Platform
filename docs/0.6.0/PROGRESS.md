# Build progress (0.6.0)

Spec: `SPEC.md` (0.6.0; 0.1.0–0.5.0 in `docs/`) · Stack: `STACK.md` · Branch: `build/tevrin-v6` · Started: 2026-11-04

**Gate:** GitHub Actions CI on the pushed head commit, after a static `stack-reviewer` pass (CLAUDE.md "Commit gate").
Agents never run the full gate, Docker, Testcontainers or E2E locally.

## Environment
- Tools: .NET SDK 10.0.402, Node 24.14.1.
- Packs (STACK.md): `fe-nextjs`, `be-aspnet`, `data-efcore-postgres`, `vcs-github`, `deploy-railway`, `local-docker-desktop-k8s`. All installed.
- Local: Docker Desktop Kubernetes is **manual checks only** (Ben).
- VCS CLI auth: gh yes (BenFielder1). Railway deploys run only through `.github/workflows/deploy.yml`.

## Requirements
| ID | Summary | Spec § | Layer(s) | Phase | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| FR-196 | Brand assets (mark, lockup, favicon, OG, `BrandMark`) | 3.1 | frontend + docs | M38 | done | CI 41855800502 |
| FR-197 | App copy says Tevrin | 3.1 | frontend | M39 | done | CI 41859664771 |
| FR-198 | Server-generated text, OpenAPI title | 3.1 | backend | M39 | done | CI 41855800502 |
| FR-199 | Agent prompts name Tevrin, versions bumped | 3.1 | backend | M39 | done | CI 41855800502 |
| FR-200 | CLI `tevrin`, `aide` alias, `TEVRIN_*` env fallback | 3.1, 4.29 | backend (cli) | M39 | done | CI 41855800502 |
| FR-201 | Compatibility (cookies, tokens, headers, branches, theme key) | 3.1 | backend + frontend | M39 | done | CI 41855800502 |
| FR-202 | Tokens / palette, AA test, no orbs | 3.2, 4.31 | frontend | M38 | done | CI 41855800502 |
| FR-203 | Geist typography, self-hosted, type scale | 3.2 | frontend | M38 | done | CI 41855800502 |
| FR-204 | Theme Light/Dark/System (default System) | 3.2 | frontend | M38 | done | CI 41899659376 |
| FR-205 | Restyled shared components | 3.2 | frontend | M38 | done | CI 41855800502 |
| FR-206 | App shell refinements | 3.3 | frontend | M40 | done | CI 41883105618 |
| FR-207 | Home `/home` + checklist | 3.3, 4.30 | frontend | M40 | done | CI 41883105618 |
| FR-208 | Page consistency, Projects filter/sort | 3.3 | frontend | M40 | done | CI 41883105618 |
| FR-209 | Command palette actions | 3.3 | frontend | M40 | done | CI 41883105618 |
| FR-210 | Marketing shell | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-211 | Root/auth routing | 3.4, 4.32 | frontend | M41 | done | CI 41899659376 |
| FR-212 | Marketing Home `/` | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-213 | Product page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-214 | How it works page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-215 | Integrations page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-216 | Self-host page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-217 | Security page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-218 | FAQ page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-219 | Changelog page | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-220 | Product previews | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-221 | SEO, robots, sitemap, noindex | 3.4 | frontend | M41 | done | CI 41899659376 |
| FR-222 | Content modules, truthfulness, shared self-host commands | 3.4 | frontend + docs | M41 | done | CI 41899659376 |
| FR-223 | Sign in / sign up pages | 3.5 | frontend | M41 | done | CI 41899659376 |
| FR-224 | README ≤ 150 lines | 3.6 | docs | M42 | done | CI 41855800502 |
| FR-225 | Docs site content | 3.6, 4.33 | docs | M42 | done | CI 41855800502, docs 41926888441 (155 internal links) |
| FR-226 | GitHub Pages workflow + link check | 3.6 | deploy (CI) | M42 | done | docs 41926888441 |
| FR-227 | Repo docs name Tevrin | 3.6 | docs | M42 | done | CI 41855800502 |
| FR-228 | GitHub App slug follows a rename on GitHub | 3.1, 4.34 | backend | post-0.6.0 fix | done | [CI 42210821139](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42210821139) |
| FR-229 | Semver version numbers (0.1.0–0.6.0), one version in three sources, CI check | 3.7 | deploy + backend + frontend + docs | M44 | done | [CI 42289439086](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42289439086) |
| FR-230 | Tag `x.y.z` + GitHub release on the `main` merge commit (CLAUDE.md "Releases") | 3.7 | deploy + docs | M44 | done | [CI 42289439086](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42289439086) |
| FR-231 | `release: published` deploys `production` | 3.7 | deploy | M44 | done | [CI 42289439086](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42289439086); first real run: the 0.6.0 release |

## Phases
- [x] M0 Baseline (base: b349c5b): CI green on `main` ([run 41828267279](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41828267279)); 0.5.0 docs moved to `docs/0.5.0/`; no re-scaffold
- [x] M38 Brand and design system: FR-196, FR-202–FR-205 ([CI 41855800502](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41855800502), head 134f907); ThemeSelector placement in M40/M41
- [x] M39 Rename: FR-197–FR-201 ([CI 41859664771](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41859664771), head 965dc73)
- [x] M40 App shell and Home: FR-206–FR-209 ([CI 41883105618](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41883105618), head d674af3)
- [x] M41 Marketing site and auth: FR-210–FR-223 ([CI 41899659376](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41899659376), head bdebfdf)
- [x] M42 Documentation: FR-224–FR-227 (content in 134f907; `docs.yml` runs first on the PR)
- [x] M43 Integrate and harden: E2E (marketing routing §4.32, above-the-fold on every route, keyboard header/footer/FAQ/mobile menu, reduced motion, same-origin, LCP in HTML, Home), axe in both themes, JS budget 192.6/200 KB, acceptance audit over 0.1.0–0.6.0 (29 Done, 3 Partial → closed, docs.yml green on PR #29 [run 41926888441](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41926888441); 1 0.4.0 toast regression → safeguarded), SPEC synced (D-V6-14–19) ([CI 41905207049](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41905207049), head 08bc7b1)
- [x] M44 Versioning and releases: FR-229–FR-231, semver folders and names (D-V6-26) ([CI 42289439086](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42289439086), head 43193a1). Docs workflow (redirect check) first runs on the PR
- [x] M45 Release 0.6.0 (branch `release/0.6.0`, PR #37): changelog date 2026-11-08, app domains (D-V6-27), SPEC and PROGRESS moved to `docs/0.6.0/` ([CI 42484575231](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42484575231), head 770d577). Next: the `0.6.0` tag and release on the merge commit (FR-230, FR-231)

## Decisions
- 2026-11-04 (M38): palette values shifted to pass AA (roles unchanged, §4.31 allows it): light danger rose-700 (rose-600 is 4.49:1 on card), dark primary indigo-300 (indigo-400 fails on its 15% tint), light sidebar-accent slate-100. `text-lg` is 20 px (the FR-203 scale has no 18 px step). `aide.theme` now stores `system` explicitly (0.5.0 deleted the key); stored `light`/`dark` still honoured. Brand assets come from one generator (`npm run brand:assets`, `src/components/brand-geometry.ts`); a test fails on drift. `metadataBase` comes from the validated request Host (switch to a configured public origin if a CDN is added).
- 2026-11-04 (M38 review): `ThemeSelector` placement in the user menu moves to M40 (FR-206 owns the user menu); the marketing footer placement is M41. Feature pages' frosted panels and sub-12 px `text-[10px|11px]` sizes are restyled in M40 (FR-208).
- 2026-11-04 (M39 backend): CLI is package `Tevrin.Cli` (command `tevrin`, `src/Aide.Cli`); the deprecated `aide` command is package `Aide.Cli` from `src/Aide.Cli.Alias` (same sources). `TEVRIN_GIT_TOKEN` → `AIDE_GIT_TOKEN` added for consistency with the other three variables. Stack catalog agent text named AIDE and feeds the codegen prompt, so it was renamed and the catalog bumped 1.2.0 → 1.2.1. Commit subjects read `[Tevrin][Phase:…]` (nothing parses them; step detection uses the `AIDE-Step-Id` trailer, kept). Prompt versions: idea 6, spec 7, plan 6, codegen 5, review 8, repo analyst 3, stack analyst 3.
- 2026-11-04 (M40): the user menu's theme control is menu radio items built from `ThemeSelector`'s `THEME_OPTIONS` and `useTheme()` (native radios can't take Tab focus inside a menu). FR-208's Projects "client-side name filter" is met by the existing server-side `search` (debounced, in the URL) and the default `sortBy=updated desc`; no second filter. Collapsed-rail tooltips are native `title` attributes (sr-only labels name each item). Checklist rules: Git done = OAuth GitHub link, GitHub App link or usable installation, or any GitLab/Bitbucket connection; AI provider needs a saved LLM credential (keyless providers don't count); Jira/Linear aren't deployment platforms; "member of a project" = `myRole` set on any of the 6 most recently updated projects (no member filter exists on `/api/projects`; an Admin whose memberships are all older sees the item as not done — accepted, no API change in 0.6.0). Signed-in `/` redirects to `/home` until M41's marketing Home replaces it. `?source=import` on `/projects/new` preselects Import.
- 2026-11-04 (M41): the root `not-found.tsx` renders the marketing shell for every unmatched URL (signed-in users see Open app); `notFound()` inside the app still uses `(app)/not-found.tsx`. robots.txt and sitemap.xml are route handlers built from the validated request Host (malformed → `http://localhost`). `X-Robots-Tag: noindex` on `/home`, `/projects`, `/account`, `/connections`, `/notifications`, `/approvals`, `/admin`. Signed-in `/` redirects to `/home` client-side once `me` resolves; content is never blocked.
- 2026-11-04 (M41 CI): Zod's `new Function` probe tripped the strict CSP (`script-src eval`) on project pages once chunk order changed; `src/instrumentation-client.ts` now sets Zod jitless before hydration. To meet the 200 KB first-load budget for `/`, the root layout no longer renders `Providers`: `(app)` and `(public)` group layouts wrap themselves (one browser QueryClient shared across groups), marketing pages read `me` via `marketing-session.tsx` (generated client, no TanStack), the root `error.tsx` is plain markup, the mobile sheet is lazy. Budget enforced in CI by `scripts/ci/check-js-budget.mjs` (first measured 192.6 KB gz of 200 KB, CI 41899659376 — little headroom). Home lifecycle labels the Execution→Review gap "Guardian checks on every step" (no human gate exists there). `/integrations` also lists Local Git and PostgreSQL. Simple Icons lacks OpenAI, Slack and Teams marks (neutral icons used).
- 2026-11-04 (M42): docs link check skips the frozen `docs/0.1.0`–`0.5.0` pages as link *sources* (their repo-root links break on the site; fixing them is allowed later), but checks links into them from `history/`.
- 2026-11-06 (D-V6-22): docs site moved to the custom domain `https://docs.tevrin.dev/`: `_config.yml` `url` set and `baseurl` emptied, link check `swap_urls` dropped, `DOCS_URL`, README, SPEC (FR-226, §9, Q1) and CLAUDE.md updated. No `docs/CNAME`: with the GitHub Actions Pages source the domain lives in repo settings. CI green: https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/42209095309
- 2026-11-06 (D-V6-23, FR-228): the GitHub App was renamed from AIDE to Tevrin, which changed its slug, and the stored slug left install links pointing at the old App. `GitHubAppStore` now takes the slug from `GET /app` (same App id and a usable slug, §4.34) through `GitHubAppInfoCache`: one cached answer (10 min) serves both the slug and the FR-80 details, a failed lookup is retried after 1 min, and the call times out after 5 s. A registered App's row is updated (`ExecuteUpdate`, no migration). A configured App logs a warning to set `GitHub__App__Slug`. The FR-81 "no Administration" test now sets its `GET /app` answer before the first request, because that answer is cached once asked.
- 2026-11-06 (D-V6-24, D-V6-25, M44): versions are semver; the versions first specified as v1–v6 are 0.1.0–0.6.0. Labels changed first (changelog, FAQ, README, docs history titles, CLAUDE.md); folders, URLs and internal mentions followed in D-V6-26 (next entry). One `<Version>0.6.0</Version>` in `backend/Directory.Build.props` replaces the CLI projects' `0.2.0`; `frontend/package.json` 0.0.0 → 0.6.0; `scripts/ci/check-version.sh` (CI `scripts` job) checks they match the newest changelog entry. Releases: annotated tag `x.y.z` on the `main` merge commit and a GitHub release; `deploy.yml` runs on `release: published`, resolves the tag (annotated or lightweight) to its commit, skips pre-releases and any release that isn't the repo's latest, checks the tag against the version in that commit, then runs the existing production job (environment approval, drift check, deploy, FR-111, smoke). Agents tag and release only after Ben confirms. Review: `check-version.sh <tag>` also requires the changelog date and checks the lock's root package; `scripts/ci/check-version.test.sh` (CI) covers the FR-229/FR-231 negative paths; `release: published` only, so a promoted pre-release doesn't deploy (documented; adding `released` would deploy twice per normal publish); the release steps tag the newest green `main` commit (CI skips docs-only commits). The tag-resolution branch (`gh api git/ref/tags`, needs `jq`) was only simulated locally; its first real run is the 0.6.0 release.
- 2026-11-06 (D-V6-26, M44): semver for folders and internal mentions too. `docs/v1`–`docs/v6` → `docs/0.1.0`–`docs/0.6.0`, `docs/history/vN.md` → `docs/history/0.N.0.md`; old docs-site URLs redirect (`jekyll-redirect-from`: `redirect_from` on history pages, per-file `_config.yml` defaults for the frozen pages, which can't take front matter; `check-docs-links.rb` fails if a redirect page is missing). Frozen 0.1.0–0.5.0 docs changed only in link paths. Comments, test names, log/warning strings, OpenAPI summaries (contract + client regenerated), the CLI preset message ("removed in 0.4.0") and docs prose use semver. Renames: E2E `web-server.spec.ts`, `support/lifecycle.ts`, `support/tracker-inference.ts`, `a11y-{lifecycle,stack,import}-targets.ts`, `scripts/perf/nfr-perf2.mjs`, `Performance_0_4_0_Tests`/`Performance_0_5_0_Tests`, ~40 test methods (`_0_N_0_`), CI E2E shards `core`/`flows`/`a11y`. Not changed: IDs (`D-V2-11`, `D-V6-n`, `DEP-V2`; from 0.7.0 decisions are `D-<x.y.z>-n`), committed migrations (CLAUDE.md rule 5), the hashed stack catalog, artifact versions, release tags, API paths and third-party versions. Changed Vitest files: 51 files, 765 tests passed locally.

- 2026-11-08 (D-V6-27, M45): app domains: `production` at `https://tevrin.dev`, `preview` at `https://aide.benfielder.com`. Railway (read-only check): preview `web` has the custom domain `aide.benfielder.com` and a generated domain; production `web` had no domain at all and production hasn't been deployed. `App__PublicBaseUrl` moved from the IaC reference `https://${{web.RAILWAY_PUBLIC_DOMAIN}}` to `operatorSetApiVariables` (set per environment), since IaC is one definition for both environments; ignored names aren't diffed, so no `railway config apply` is needed and preview keeps its current value until Ben sets it. `tevrin.dev` currently serves the registrar's parking page (Apache), so production needs the custom domain and DNS before its first deploy. GitHub `production` environment checked: required reviewer BenFielder1 (self-review allowed), deployments from `main` and tags `*.*.*`, the four secrets, `RAILWAY_WEB_URL` = `https://tevrin.dev`; tag ruleset "release tags" on `*.*.*` blocks deletion, update and force-push. Tags and releases 0.1.0–0.5.0 created on the earlier merge commits (`--latest=false`; GitHub still labels 0.5.0 Latest until 0.6.0 is published).
## Known issues
- Cosmetic: a handed-off toast's timer restarts if a Toaster remounts within 4 s (`lib/toast-handoff.ts`).
- `/` first-load JS is 192.6 KB of the 200 KB budget: little headroom for marketing additions.
- Home checklist item 3 checks membership only across the 6 most recent projects (Admin edge case, D-V6-16).

## Manual checks (Ben)
- Enable GitHub Pages (Settings → Pages → Source "GitHub Actions").
- Docs custom domain (D-V6-22): DNS `CNAME docs.tevrin.dev → benfielder1.github.io`, Settings → Pages → Custom domain `docs.tevrin.dev` verified, "Enforce HTTPS" on; after the next docs deploy, `https://docs.tevrin.dev/` loads with styles and search, and the old github.io URL redirects.
- Favicon legibility at 16 px (FR-196).
- Lighthouse performance and accessibility ≥ 90 on `/` against preview.
- Docker Desktop redeploy and smoke.
- **Before the 0.6.0 release deploys production (D-V6-27, M45):**
  - Railway `production` → `web` → Settings → Networking → Custom Domain `tevrin.dev` (port 8080), the DNS records Railway shows at the domain's DNS provider (an apex domain needs CNAME flattening or ALIAS/ANAME), until Railway shows it verified and `https://tevrin.dev` stops serving the parking page.
  - `App__PublicBaseUrl=https://tevrin.dev` on `api` in `production`, and the rest of the first-time setup for `production` in `deploy/railway/README.md` step 3 (signing key, optional secrets, backups).
  - Preview: set `App__PublicBaseUrl=https://aide.benfielder.com` on `api` in `preview` (it still holds the old reference value).
  - After the first production deploy: register the first Admin on `https://tevrin.dev` straight away, then the smoke account (`SMOKE_EMAIL`/`SMOKE_PASSWORD`); the first deploy's smoke test fails until it exists, so re-run the deploy job after registering it.
  - Register the production GitHub App (or OAuth App) against `https://tevrin.dev` (README step 7).
- Release 0.6.0 after this branch merges: confirm the commit, then tag `0.6.0` on the `main` merge commit and publish the release (CLAUDE.md "Releases"), approve the production deploy, and check the summary names the release.
- An existing session and an `aide_pat_` token still work after the preview deploy (FR-201).
- GitHub App rename (Q2): after the deploy, Admin → Integrations → GitHub shows the new slug and its install link opens the Tevrin App (FR-228). If the App is configured through `GitHub__App__*`, set `GitHub__App__Slug` to the new slug to clear the warning in the API log.
