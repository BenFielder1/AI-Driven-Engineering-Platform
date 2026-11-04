# Build progress (v6)

Spec: `SPEC.md` (v6; v1–v5 in `docs/`) · Stack: `STACK.md` · Branch: `build/tevrin-v6` · Started: 2026-11-04

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
| FR-196 | Brand assets (mark, lockup, favicon, OG, `BrandMark`) | 3.1 | frontend + docs | M38 | todo | |
| FR-197 | App copy says Tevrin | 3.1 | frontend | M39 | todo | |
| FR-198 | Server-generated text, OpenAPI title | 3.1 | backend | M39 | todo | |
| FR-199 | Agent prompts name Tevrin, versions bumped | 3.1 | backend | M39 | todo | |
| FR-200 | CLI `tevrin`, `aide` alias, `TEVRIN_*` env fallback | 3.1, 4.29 | backend (cli) | M39 | todo | |
| FR-201 | Compatibility (cookies, tokens, headers, branches, theme key) | 3.1 | backend + frontend | M39 | todo | |
| FR-202 | Tokens / palette, AA test, no orbs | 3.2, 4.31 | frontend | M38 | todo | |
| FR-203 | Geist typography, self-hosted, type scale | 3.2 | frontend | M38 | todo | |
| FR-204 | Theme Light/Dark/System (default System) | 3.2 | frontend | M38 | todo | |
| FR-205 | Restyled shared components | 3.2 | frontend | M38 | todo | |
| FR-206 | App shell refinements | 3.3 | frontend | M40 | todo | |
| FR-207 | Home `/home` + checklist | 3.3, 4.30 | frontend | M40 | todo | |
| FR-208 | Page consistency, Projects filter/sort | 3.3 | frontend | M40 | todo | |
| FR-209 | Command palette actions | 3.3 | frontend | M40 | todo | |
| FR-210 | Marketing shell | 3.4 | frontend | M41 | todo | |
| FR-211 | Root/auth routing | 3.4, 4.32 | frontend | M41 | todo | |
| FR-212 | Marketing Home `/` | 3.4 | frontend | M41 | todo | |
| FR-213 | Product page | 3.4 | frontend | M41 | todo | |
| FR-214 | How it works page | 3.4 | frontend | M41 | todo | |
| FR-215 | Integrations page | 3.4 | frontend | M41 | todo | |
| FR-216 | Self-host page | 3.4 | frontend | M41 | todo | |
| FR-217 | Security page | 3.4 | frontend | M41 | todo | |
| FR-218 | FAQ page | 3.4 | frontend | M41 | todo | |
| FR-219 | Changelog page | 3.4 | frontend | M41 | todo | |
| FR-220 | Product previews | 3.4 | frontend | M41 | todo | |
| FR-221 | SEO, robots, sitemap, noindex | 3.4 | frontend | M41 | todo | |
| FR-222 | Content modules, truthfulness, shared self-host commands | 3.4 | frontend + docs | M41 | todo | |
| FR-223 | Sign in / sign up pages | 3.5 | frontend | M41 | todo | |
| FR-224 | README ≤ 150 lines | 3.6 | docs | M42 | todo | |
| FR-225 | Docs site content | 3.6, 4.33 | docs | M42 | todo | |
| FR-226 | GitHub Pages workflow + link check | 3.6 | deploy (CI) | M42 | todo | |
| FR-227 | Repo docs name Tevrin | 3.6 | docs | M42 | todo | |

## Phases
- [x] M0 Baseline (base: b349c5b): CI green on `main` ([run 41828267279](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41828267279)); v5 docs moved to `docs/v5/`; no re-scaffold
- [ ] M38 Brand and design system: FR-196, FR-202–FR-205
- [ ] M39 Rename: FR-197–FR-201
- [ ] M40 App shell and Home: FR-206–FR-209
- [ ] M41 Marketing site and auth: FR-210–FR-223
- [ ] M42 Documentation: FR-224–FR-227
- [ ] M43 Integrate and harden: E2E, axe both themes, JS budget, audit over v1–v6

## Decisions

## Known issues

## Manual checks (Ben)
- Enable GitHub Pages (Settings → Pages → Source "GitHub Actions").
- Favicon legibility at 16 px (FR-196).
- Lighthouse performance and accessibility ≥ 90 on `/` against preview.
- Docker Desktop redeploy and smoke.
- An existing session and an `aide_pat_` token still work after the preview deploy (FR-201).
- GitHub App rename (Q2).
