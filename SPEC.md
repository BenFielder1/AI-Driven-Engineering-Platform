# Tevrin (formerly AIDE) — Specification (v6)

2026-11-04 · Ben Fielder · **v6**, built on the v5 codebase in this repo. Earlier versions: [`docs/v1/SPEC.md`](docs/v1/SPEC.md) … [`docs/v5/SPEC.md`](docs/v5/SPEC.md). Stack: [`STACK.md`](STACK.md). Source: Ben's v6 brief (2026-11-04), the interview answers in §10, a codebase survey of the frontend and the name's footprint, and UX research on developer SaaS products (sources in §13).

> **How this spec relates to v1–v5.** v6 is an increment on the running v5 app. **Everything in the earlier specs stays in force unless this document amends it.** Amended items are listed in §3.0 and marked *(amends …)*. New requirements continue from **FR-196**, domain rules from **§4.29**, decisions use `D-V6-n`, and milestones continue from **M38**. Earlier tests stay green unless an amendment changes what they assert; then update the test and name the amendment in it.

## 1. Overview

v6 renames the product to **Tevrin**, gives it a brand and design system that read as one SaaS product, adds a public marketing site, and reorganises the repo documentation into a published docs site. There are no schema changes and no new API endpoints.

- **Rename (product-facing).** Everything a user, reader or Git host sees says Tevrin: the web app, emails and chat notifications, agent prompts, API problem text, the OpenAPI title, the GitHub App name for new registrations, the CLI command and the docs. Internal identifiers that would break sessions, tokens, stored data or infrastructure keep the `aide` name (§4.29).
- **Brand and design system.** A refined version of the hexagon mark in `docs/v6/`, an indigo→cyan brand gradient on neutral slate surfaces, Geist type, and light and dark themes that follow the OS by default.
- **App refresh.** A tidier shell, a new **Home** dashboard with an onboarding checklist, and consistent page headers, states, badges and empty states.
- **Marketing site.** Home, Product, How it works, Integrations, Self-host, Security, FAQ and Changelog, reachable signed out, with Get started and Sign in on every page. Redesigned sign-in and sign-up.
- **Documentation.** A short README, and the rest moved into a structured `docs/` site published to GitHub Pages.

Success means:
- A signed-out visitor at `/` understands what Tevrin does from the hero, reads how the lifecycle and gates work, and creates an account in two clicks.
- A new user lands on Home, sees a four-item checklist, and finishes setup without guessing where things are.
- No visible "AIDE" remains in the app, notifications or docs outside history, and every existing session, API token and webhook keeps working.
- The README fits on two screens, and every topic it dropped is one link away on the docs site.

**In scope (v6):** rename (§3.1) · brand and design system (§3.2) · app shell and Home (§3.3) · marketing site (§3.4) · auth pages (§3.5) · documentation (§3.6).

**Out of scope (v6):**
- Renaming internal identifiers: C# projects and namespaces, database and user names, cookies, `aide_pat_` tokens, `X-AIDE-*` webhook headers, environment variables other than the CLI's, stored formats (`aide-plan`, `aide-stack-profile`, `aide_merges`, `aide/<slug>/v<ver>` branches, `.aide/`), Kubernetes, Docker and Railway resource names (§4.29).
- Renaming the GitHub repo, the custom domain or an already-registered GitHub App (manual, Q1–Q2).
- Billing, pricing tiers, a contact form, newsletter, analytics or any third-party script.
- Password reset, passkeys and Google sign-in.
- Terms and Privacy pages until Ben supplies the text (Q4).
- A CMS. Marketing copy lives in typed content modules in the repo.

## 2. Users and permissions

The v1–v5 roles are unchanged. v6 adds the anonymous visitor.

| Role | Can (new in v6) | Cannot / behaviour |
| --- | --- | --- |
| Visitor (signed out) | View every marketing page, `/login`, `/register`, `robots.txt`, `sitemap.xml` | Open any app route: redirected to `/login?returnUrl=…` (unchanged) |
| User (any signed-in role) | Everything a visitor can. Open **Home** (`/home`). Dismiss or restore their onboarding checklist. Choose Light, Dark or System theme | `/` sends them to `/home` (FR-211). `/login` and `/register` send them to `returnUrl` or `/home` |
| Admin | Unchanged | — |

**Auth:** unchanged (email + password, GitHub/GitLab/Bitbucket sign-in when configured). Registration stays open and the first account becomes Admin (v1 FR-3).

## 3. Functional requirements

### 3.0 Amended earlier requirements (summary)

| Earlier | What changes | Where |
| --- | --- | --- |
| Product name everywhere (v1–v5 copy) | "AIDE" becomes "Tevrin" in everything user-facing. Stored history is not rewritten | FR-197–FR-199, §4.29 |
| v2 brand (`docs/v2/brand/`) | Replaced by the Tevrin mark. `docs/v2/brand` stays frozen as history | FR-196 |
| v2 §7.1/§7.2 design tokens and look | New palette, type and component styling. The WCAG AA token test stays and covers the new values | FR-202–FR-205 |
| v2/v3 theme (dark default) | Default is **System**. Light and Dark are explicit choices | FR-204, D-V6-3 |
| `/` redirect to `/projects`; post-sign-in landing `/projects` | `/` is the marketing home for visitors. Signed-in users land on `/home` | FR-207, FR-211 |
| v1 FR-52 CLI | Command is `tevrin`; `aide` stays as a deprecated alias | FR-200 |
| v1 FR-24 prompts | Prompts name Tevrin; each changed template's version increases | FR-199 |
| Login and register pages (v1, v4) | New layout and copy | FR-223 |
| v1 FR-34 commit subject | Reads `[Tevrin][Phase:…][Step:N]`. Nothing parses it: step detection uses the kept `AIDE-Step-Id` trailer | D-V6-15 |
| Stack catalog 1.2.0 | 1.2.1: agent text renamed, because it feeds the codegen prompt | D-V6-15 |
| README | Condensed; detail moves to the docs site | FR-224–FR-226 |

### 3.1 Rename to Tevrin

- **FR-196** *(frontend + docs)* **Brand assets.** The final mark, lockup and icons live in `docs/v6/brand/` (source SVGs) and `frontend/public/` (served copies). They are built on the `docs/v6` concept: six stroked hexagon segments for the six phases, the gaps between them for the approval gates, and a `T` monogram (D-V6-6). Refinements:
  - The `T` and wordmark use `currentColor` in the inline React component, so one asset works in both themes. Static files ship as light (`#0F172A` ink) and dark (`#F8FAFC` ink) variants.
  - A **favicon** variant (`favicon.svg`, plus `icon-32.png`, `apple-touch-icon.png` 180 px) uses heavier strokes and no gaps narrower than 2 px at 16 px. A **mono** variant (single colour, no gradient) exists for places that can't render gradients.
  - Gradient: `#4F46E5 → #06B6D4` on light, `#818CF8 → #22D3EE` on dark. Text is never placed on the gradient.
  - An **Open Graph image** (`og.png`, 1200×630) shows the lockup and the tagline on the dark surface.
  - `components/brand.tsx` exports `BrandMark` and `BrandLockup` (mark + "Tevrin"; no subtitle).
  - Given the app in either theme, when the sidebar, auth card and marketing header render, then the mark and wordmark are visible with at least 3:1 contrast against their background (asserted in the token test).
  - Given the favicon at 16×16, then all six segments and the `T` are distinguishable (manual check, recorded in PROGRESS.md).
- **FR-197** *(frontend)* **App copy.** Every visible string, `aria-label`, alt text, document title and metadata that names the product says Tevrin. Document titles are `<page> · Tevrin`; the root title is `Tevrin`. Error-code copy (the single mapping file) is updated where it names the product.
  - Given a test that greps rendered text across the Vitest page tests, then "AIDE" appears nowhere (an `FR197_` test over the rendered DOM of each top-level page).
  - Given the existing tests that assert old copy ("Sign in to AIDE", "Create your AIDE account" and so on), then they assert the new copy and cite FR-197.
- **FR-198** *(backend)* **Server-generated text.** Every string the backend shows to people names Tevrin:
  - ProblemDetails titles and details; email subjects (`[Tevrin] …`); "Open in Tevrin" in email, Slack and Teams messages; pull/merge request and release bodies Tevrin writes; the system actor's display name (`ActorResolver.SystemDisplayName = "Tevrin"`).
  - The GitHub App manifest's default name becomes `Tevrin ({host})` for **new** registrations. An existing App keeps its name (Q2).
  - The OpenAPI `info.title` becomes `Tevrin API`. The contract and client are regenerated.
  - Stored data (events, artifacts, past PR bodies, notifications already sent) is not rewritten (v1 rule 9).
  - Given a problem response that used to say "The AIDE GitHub App isn't installed…", then it says "The Tevrin GitHub App isn't installed…" with the same `errorCode` and status.
  - Given an email notification, then its subject starts `[Tevrin]` (test `FR198_…`).
- **FR-199** *(backend)* **Agent prompts.** The seven templates in `Agents/Prompts/` say "agent of Tevrin". Each changed template's version increases (FR-24), so runs record the new version. Built versions: idea 6, spec 7, plan 6, codegen 5, review 8, repo analyst 3, stack analyst 3. References to `.aide/` paths stay (§4.29).
  - Given a new run of any agent, then the rendered system prompt contains "Tevrin" and not "AIDE", and the run's prompt version is the new one.
- **FR-200** *(backend)* **CLI.** The .NET tool's command is `tevrin`; help, errors and output say Tevrin.
  - `aide` remains as a deprecated alias for v6: it prints `aide is now tevrin; this alias will be removed in a later version.` to stderr, then behaves identically. Built as two packages from the same sources: `Tevrin.Cli` (command `tevrin`, `src/Aide.Cli`) and the deprecated `Aide.Cli` (command `aide`, project `src/Aide.Cli.Alias`) (D-V6-14).
  - Environment variables: `TEVRIN_TOKEN`, `TEVRIN_API_URL`, `TEVRIN_CONFIG` and `TEVRIN_GIT_TOKEN` are read first, then `AIDE_TOKEN`, `AIDE_API_URL`, `AIDE_CONFIG` and `AIDE_GIT_TOKEN` (§4.29 worked example). The config file location is unchanged. Tokens still start `aide_pat_`.
  - Given only `AIDE_TOKEN` is set, when `tevrin projects list` runs, then it authenticates. Given both are set, then `TEVRIN_TOKEN` wins.
- **FR-201** *(backend + frontend)* **Compatibility.** Nothing a user already holds breaks.
  - Given a browser signed in before the v6 deploy, then it stays signed in (cookie names unchanged).
  - Given an `aide_pat_` token issued before v6, then it authenticates. Given a webhook consumer that verifies `X-AIDE-*` headers, then deliveries still verify.
  - Given a project mid-version on an `aide/<slug>/v<ver>` branch, then execution, review and release continue unchanged.
  - Given a user's saved theme in localStorage `aide.theme`, then it is honoured (FR-204).
- **FR-228** *(backend)* **GitHub App rename.** Renaming the GitHub App (Q2) changes its slug, which install links, the App page and the bot commit identity (`<slug>[bot]`) are built from. Tevrin uses the App's current slug from GitHub (`GET /app` with the App JWT) when GitHub answers for the same App id with a usable slug (§4.34); otherwise it uses the stored or configured slug.
  - The answer is cached in memory for 10 minutes and shared with the FR-80 App details. A failed lookup (unreachable, rate limited, refused, over 5 seconds, unreadable key) is remembered for 1 minute, so an unreachable GitHub isn't asked on every request.
  - A registered App's row (`github_apps.slug`, `html_url`) is updated in place, with no migration. A configured App keeps its configuration, and the API logs a warning to set `GitHub__App__Slug`.
  - Given a configured App renamed on GitHub, when an Admin opens Admin → Integrations → GitHub, then the slug, install URL and App page show the new slug, and so does the connection's install link (`FR228_` tests).
  - Given a registered App renamed on GitHub, then the stored slug and page are updated.
  - Given GitHub is unreachable, or answers for another App id, then the stored slug is used.

### 3.2 Brand and design system

- **FR-202** *(frontend)* **Tokens.** `lib/ui/tokens.ts` and `index.css` hold the new palette (§4.31). Neutral slate surfaces, an indigo primary, cyan as the secondary accent and focus colour, emerald, amber and rose for success, warning and danger. The brand gradient appears only on the mark, the marketing hero glow, the active lifecycle stage and decorative borders.
  - Given the token test, then every text pair (including tinted pairs) meets WCAG AA 4.5:1 and every UI pair 3:1, in both themes.
  - Given the app, then the v2/v3 "ambient orbs" are gone from the app and auth shells.
- **FR-203** *(frontend)* **Typography.** Geist Sans for UI and headings, Geist Mono for IDs, hashes, versions, code and eyebrow labels. Self-hosted through `@fontsource` (CSP stays `font-src 'self'` by default). DM Sans and Outfit are removed. A type scale (12/14/16/20/24/30/36/48/60 px) is defined once as tokens; `text-lg` is 20 px (there is no 18 px step).
  - Given any page, then no font loads from another origin.
- **FR-204** *(frontend)* **Theme.** Light, Dark and System; **System is the default**. The inline nonce'd init script applies the resolved theme before first paint and follows OS changes live while System is chosen. The choice is stored in `aide.theme` (`light` | `dark` | `system`); `system` is stored explicitly (v5 deleted the key), and stored `light`/`dark` are still honoured.
  - Given no stored value and an OS in light mode, then the page renders light with no flash of dark.
  - Given System and the OS switches to dark, then the page switches without reload.
  - The selector is in the user menu (app) and the footer (marketing).
- **FR-205** *(frontend)* **Components.** The shared components (`components/ui`, `components/shared`) are restyled to the new tokens:
  - Status badges show a dot and a label, never colour alone. Running pulses, and the pulse stops under `prefers-reduced-motion`.
  - Empty states have a title, one sentence on what the area is for, and a primary action where one exists ("Create project", "Add connection").
  - Loading uses skeletons shaped like the content; errors are per panel with Retry.
  - Interactive targets are at least 24×24 px. Focus rings are 2 px, offset, in the focus colour. Toasts sit bottom-right.
  - Motion is 150–250 ms opacity/translate only and is disabled under `prefers-reduced-motion`.
  - Given each restyled component's test, then it renders in both themes and axe reports no violations.

### 3.3 App shell and Home

- **FR-206** *(frontend)* **Shell.** The sidebar and header from v2/v3 stay (`nav-config.ts` remains the single source) and are refined:
  - Sidebar 240 px, collapsible to a 56 px icon rail with tooltips (native `title` attributes; sr-only labels name each item); project switcher at the top; Workspace items **Home**, Projects, Notifications, Connections; Admin and project sections unchanged in content.
  - User menu: name, email, Account, Connections, Theme (Light/Dark/System, as menu radio items built from `THEME_OPTIONS` and `useTheme()`), Docs ↗, Sign out.
  - Header: breadcrumbs, a primary-action slot filled by the page (e.g. "New project"), ⌘K search, notifications bell. Sticky with a 1 px border; `scroll-padding-top` keeps focused elements clear of it (WCAG 2.4.11).
  - Given a viewport below `lg`, then the drawer replaces the sidebar with the same items.
- **FR-207** *(frontend)* **Home (`/home`).** The default signed-in landing page (after sign-in, `/`, and the logo link in the app):
  - Greeting with the user's name; **Get set up** checklist (§4.30); **Recent projects** (up to 6 by `updatedAt`, with phase badge, status and versions; "View all"); **Unread notifications** (up to 5, "View all").
  - Built only from existing endpoints. Dismissing the checklist stores `aide.home.checklist-dismissed` in localStorage; "Show setup checklist" on Home restores it.
  - Given a user with no projects, then Recent projects shows an empty state with "Create project" and "Import a repository". `?source=import` on `/projects/new` preselects Import (used by "Import a repository" and the palette).
  - Given every checklist item done, then the checklist collapses to "You're all set" and can be dismissed.
- **FR-208** *(frontend)* **Page consistency.** Every in-app page uses `PageHeader` (title, optional description, actions) and the shared loading, empty, error and forbidden states. The Projects list gains a name filter and sorts by last updated. The existing server-side `search` (debounced, kept in the URL) and the default `sortBy=updated desc` meet this; there is no second client-side filter.
  - Given each app route's page test, then it renders a single `h1` and the states named above.
- **FR-209** *(frontend)* **Command palette.** Adds actions: Go to Home, New project, Import repository, Switch theme (Light/Dark/System), Open docs ↗. Same shortcut opens and closes; focus returns to the trigger.

### 3.4 Marketing site

- **FR-210** *(frontend)* **Marketing shell** (route group `(marketing)`):
  - Header, sticky and translucent: logo (→ `/`), Product, How it works, Integrations, Self-host, Docs ↗, Changelog; right side GitHub ↗, **Sign in**, **Get started** (→ `/register`). Signed in, the right side shows **Open app** (→ `/home`) instead of Sign in and Get started. Below `md`, a menu button opens a sheet with the same links.
  - Footer columns: Product (Features, How it works, Integrations, Changelog), Developers (Docs, CLI, API reference, GitHub), Self-host (Install, Requirements, Upgrade — docs links), Trust (Security, FAQ). Bottom row: © year, theme selector.
  - Skip link to main content. Every page has one `h1`.
  - Given a visitor on any marketing page, then Get started and Sign in are visible without scrolling at 375 px and 1440 px widths.
- **FR-211** *(frontend)* **Routing** (§4.32): `/` renders Home for visitors and redirects signed-in users to `/home`. Other marketing routes render for both. The session check never blocks the first render of marketing content (the header swaps its buttons when `me` resolves).
- **FR-212** *(frontend)* **Home page (`/`)**, sections in order:
  1. Hero: headline "Ship software with AI agents, under human control."; subhead naming the six phases, approval gates, versioned artifacts and rollback; **Get started** (primary) and **Self-host** (secondary); a product preview (FR-220) of a project page with the lifecycle rail, a gate awaiting approval and an artifact diff.
  2. Integrations strip: GitHub, GitLab, Bitbucket, Jira, Linear, Railway, Render, Vercel, Netlify, Cloudflare, Supabase, Anthropic, OpenAI, Ollama — labelled "Works with", not customers.
  3. Lifecycle: the six phases (plus optional Analysis) as numbered steps with a gate between each. The Execution→Review gap is labelled "Guardian checks on every step", since no human gate exists there.
  4. Why Tevrin: three cards — Governed (gates and approval modes), Versioned (hashed, immutable artifacts with diffs), Reversible (rollback and audit trail).
  5. Feature bento (6 tiles): approval gates, artifacts and diffs, agents and providers, Git hosts and PRs, issue-tracker imports, deployments and databases. Each links to its Product section.
  6. Self-host: short requirements list and a copyable command block, linking to Self-host.
  7. Latest changes: the two newest Changelog entries.
  8. FAQ: the five first questions, linking to `/faq`.
  9. Closing CTA repeating the hero buttons.
- **FR-213** *(frontend)* **Product (`/product`).** One anchored section per feature area: lifecycle and gates; artifacts and versioning; agents, prompts and LLM providers; Git hosts, PRs, review and merge; issue-tracker imports; stack composition and inference; deployments and managed databases; notifications and live updates; admin and audit; CLI and API. Each has a heading, 2–4 sentences, 3–5 bullets and a preview or icon.
- **FR-214** *(frontend)* **How it works (`/how-it-works`).** A walk-through from idea to release: each phase's input, agent, artifact and gate; approval modes (strict, advisory, autonomous); review cycles; release versions and checkpoints; rollback and invalidation. An SVG lifecycle diagram whose animation runs only without reduced motion and has a text equivalent.
- **FR-215** *(frontend)* **Integrations (`/integrations`).** Grouped cards: Git hosts (including Local Git), issue trackers, LLM providers, deployment platforms, databases (including PostgreSQL), notification channels. Each card states what Tevrin does with it in one line. Names match the catalog; nothing unimplemented is listed.
- **FR-216** *(frontend)* **Self-host (`/self-host`).** "Free while in preview. No billing." Requirements (Docker, PostgreSQL 18, an LLM provider key or Ollama); three paths — Railway, Docker Desktop Kubernetes, dev loop — each with its key commands and a docs link; what you configure (Git sign-in, GitHub App, providers, notifications); FAQ on upgrades and data. Commands match the docs site exactly (one shared source, FR-222).
- **FR-217** *(frontend)* **Security (`/security`).** Factual statements only: secrets encrypted at rest and never shown again, personal connections, short-lived GitHub App tokens, read-only repo tools with secret redaction, immutable locked artifacts with content hashes, audit events, CSP and security headers, no third-party scripts on the site, agents faked in tests. Links to the relevant docs.
- **FR-218** *(frontend)* **FAQ (`/faq`).** An accessible accordion (button + region, keyboard operable), with a "Still have questions?" link to the docs and GitHub issues. Questions at least: What is Tevrin? · Who is it for? · How do approval gates work? · Can I undo what an agent did? · Which AI models can I use, and where do my keys live? · Does my code leave my infrastructure? · Does it work with an existing repository? · Which Git hosts, trackers and platforms are supported? · Is it free? · How do I self-host it? · Is there a CLI or API? · Was it called AIDE? Answers come from the specs and must be true of v6.
- **FR-219** *(frontend)* **Changelog (`/changelog`).** One entry per version v1–v6, newest first: version, date (the merge date of its PR; the v6 entry shows "In preview" until its PR merges), title, 3–6 highlights, link to that version's spec on the docs site. v1–v5 entries say "(as AIDE)".
- **FR-220** *(frontend)* **Product previews.** Static React components that reuse the real UI components with fixture data (no API calls), sized for the hero and Product sections, theme-aware, `aria-hidden` with a visible caption or an equivalent text alternative. No raster screenshots.
  - Given reduced motion, then previews are static.
- **FR-221** *(frontend)* **SEO and metadata.** Each marketing route has a server `layout.tsx` exporting `metadata` (title, description, Open Graph and Twitter card with `og.png`), keeping pages as client components (D-V3-2). `robots.txt` allows marketing and auth routes and disallows app routes; `sitemap.xml` lists the marketing routes with absolute URLs built from the request host. Both are route handlers using the validated request Host (malformed → `http://localhost`). `X-Robots-Tag: noindex` is sent on `/home`, `/projects`, `/account`, `/connections`, `/notifications`, `/approvals` and `/admin`.
  - Given `GET /robots.txt` and `GET /sitemap.xml`, then both return 200 with the content above (tests in `src/server` or route tests).
- **FR-222** *(frontend)* **Content source and truthfulness.** All marketing copy lives in typed modules under `src/features/marketing/content/`. Copy describes only behaviour that exists in v1–v6; no customer logos, testimonials, invented metrics or "open source" claims (Q3, D-V6-21). Integration marks use monochrome brand icons (Simple Icons, CC0) beside the name; OpenAI, Slack and Teams have no Simple Icons mark and use neutral icons. The Self-host commands come from one module also used to check the docs (a Vitest test compares them with `docs/` snippets).

### 3.5 Auth pages

- **FR-223** *(frontend)* **Sign in and sign up.**
  - `/login`: centred card on a subtle brand glow; logo (→ `/`); h1 "Sign in to Tevrin"; provider buttons (GitHub, GitLab, Bitbucket, only when configured) above an "or" divider and the email form; "Don't have an account? Create one".
  - `/register`: split screen at `lg` and up — left panel with the lockup, "Ship software with AI agents, under human control." and three bullets (gates, versioned artifacts, rollback) over a static lifecycle diagram; right panel with h1 "Create your Tevrin account", providers, divider, form, "Already have an account? Sign in". Below `lg`, the left panel is hidden.
  - Password fields allow paste and password managers (`autocomplete` set), meeting WCAG 3.3.8. Errors use the existing error-code copy.
  - Given `/register?returnUrl=/projects/new`, then after sign-up the user lands on `/projects/new`; with no `returnUrl`, on `/home`.

### 3.6 Documentation

- **FR-224** *(docs)* **README.** At most 150 lines: name and one-paragraph overview, a 6–8 bullet feature summary, stack table, architecture diagram (mermaid) with 4–6 bullets, repo layout, deployment (Railway and local Kubernetes, one short paragraph each), and a **Documentation** table linking each docs section. The Tevrin lockup at the top, light or dark to follow the reader's GitHub theme (`#gh-light-mode-only` / `#gh-dark-mode-only`, D-V6-20).
  - Given the README, then every section removed from it has a home on the docs site (the mapping in §4.33), and no link in it is broken.
- **FR-225** *(docs)* **Docs site content.** `docs/` becomes the site source with this structure (front matter for navigation):
  - `index.md` (overview, where to start) · `getting-started/` (prerequisites, dev loop, first project) · `guides/` (connections and sign-in, GitHub App, GitLab, Bitbucket, LLM providers, issue trackers, stacks and inference, deployments and databases, notifications, CLI) · `operations/` (local Kubernetes and troubleshooting, Railway, PostgreSQL 17→18 upgrade, CI and the commit gate) · `reference/` (configuration and environment variables, API and contract, architecture, testing, error codes, repo layout) · `history/` (one page per version linking the frozen `docs/v1`–`docs/v6` specs and build logs) · `brand.md` (logo files, colours, usage rules).
  - `deploy/railway/README.md` and `variables.md` move under `operations/railway/` only if every reference is updated; otherwise they stay and are linked.
  - Frozen `docs/v1`–`docs/v5` files are not edited except to fix links. Adding front matter to them isn't allowed, so the site config includes them as plain pages (`jekyll-optional-front-matter`).
  - Given any page, then its relative links resolve (link check in FR-226). The check skips frozen `docs/v1`–`v5` pages as link sources (their repo-root links break on the site), but checks links into them.
- **FR-226** *(deploy)* **GitHub Pages.** `.github/workflows/docs.yml` builds the site with Jekyll and the `just-the-docs` theme (mermaid enabled, search on) from `docs/`:
  - On pull requests touching `docs/**`, `README.md` or the workflow: build plus an offline internal link check (html-proofer, external links skipped). On push to `main` touching the same: build, check, then deploy with `actions/deploy-pages`.
  - Site URL: `https://benfielder1.github.io/AI-Driven-Engineering-Platform/` until a custom domain is chosen (Q1). The frontend's docs links use a build-time constant `DOCS_URL` in `src/lib/site.ts`.
  - Given a PR that adds a broken relative link under `docs/`, then the docs job fails.
- **FR-227** *(docs)* **Repo docs.** CLAUDE.md, STACK.md and `deploy/railway/README.md` name Tevrin (keeping the internal `aide` identifiers they document). CLAUDE.md gains the v6 Versions row, the docs-site layout and the `docs.yml` workflow.

## 4. Domain rules

### 4.29 Rename boundary

Renamed (visible to people or Git hosts): UI copy and titles, emails, Slack/Teams text, ProblemDetails text, PR/MR/release bodies, system actor name, GitHub App default name (new registrations), OpenAPI title, agent prompts, CLI command and messages, CLI env vars (with fallback), README and docs.

Kept as `aide` (identifiers): `Aide.*` projects and namespaces, `AideDbContext`; database and user `aide`; cookies `aide.*`; localStorage keys `aide.*`; token prefix `aide_pat_`; `DataProtection` application name; `ConnectionStrings:Aide`; `AIDE_RUN_MIGRATIONS` and other server env vars; `X-AIDE-*` webhook headers; fences `aide-plan`, `aide-stack-profile`; enum `aide_merges`; branches `aide/<slug>/v<ver>`; paths `.aide/`, `.aide-fake/`; advisory-lock key; Kubernetes namespace, images, PVC; Railway project and services; CI image names.

CLI env resolution: `value = TEVRIN_X ?? AIDE_X ?? config file ?? default`.

| TEVRIN_TOKEN | AIDE_TOKEN | Config token | Used |
| --- | --- | --- | --- |
| `aide_pat_new` | `aide_pat_old` | — | `aide_pat_new` |
| unset | `aide_pat_old` | `aide_pat_cfg` | `aide_pat_old` |
| unset | unset | `aide_pat_cfg` | `aide_pat_cfg` |
| `""` (empty) | `aide_pat_old` | — | `aide_pat_old` (empty counts as unset) |

### 4.30 Onboarding checklist

Pure function `checklist(connections, llmConnections, platformConnections, projects)` in `features/home/`, unit-tested with these cases. Items, in order:
1. **Connect a Git host** — done when the user has an OAuth GitHub link, a GitHub App link or usable installation, or any GitLab or Bitbucket connection. Action → Connections.
2. **Add an AI provider** — done when the user has at least one saved LLM credential (keyless providers don't count). Action → Connections.
3. **Create or import a project** — done when `myRole` is set on any of the 6 most recently updated projects. `/api/projects` has no member filter, so an Admin whose memberships are all older sees this item as not done (accepted, D-V6-16). Action → New project.
4. **Connect a deployment platform** *(optional)* — done when the user has a deployment-platform connection (Jira and Linear aren't deployment platforms). Action → Connections.

`complete = items 1–3 done` (optional items don't block). Progress shows `done/4`.

| Git | LLM | Projects | Platform | Shows | complete |
| --- | --- | --- | --- | --- | --- |
| no | no | 0 | no | 0/4, item 1 highlighted | false |
| yes | yes | 0 | no | 2/4, item 3 highlighted | false |
| yes | yes | 2 | no | 3/4, "You're all set" (optional left) | true |
| no | yes | 1 | yes | 3/4, item 1 highlighted | false |

### 4.31 Palette

Defined in `tokens.ts` (oklch), approximated here in hex. Final values may shift to pass the AA test; the roles may not. Built shifts: light danger rose-700 (rose-600 is 4.49:1 on card), dark primary indigo-300 (indigo-400 fails on its 15% tint), light sidebar-accent slate-100 (D-V6-17).

| Role | Light | Dark |
| --- | --- | --- |
| background | `#FFFFFF` | `#020617` (slate-950) |
| card / surface | `#F8FAFC` | `#0B1120` |
| border | `#E2E8F0` | `#1E293B` |
| foreground | `#0F172A` | `#F8FAFC` |
| muted-foreground | `#475569` | `#94A3B8` |
| primary (buttons, links) | `#4F46E5` (white text) | `#A5B4FC` indigo-300 (`#020617` text) |
| accent / ring | `#0891B2` | `#22D3EE` |
| success / warning / danger | emerald-700 / amber-500 fill / rose-700 | emerald-400 / amber-400 fill / rose-400 |

Rules: no text on the gradient; warning is never text; status badges pair colour with a dot and label (Locked slate, In review amber, Approved emerald, Failed rose, Running indigo).

### 4.32 Root and auth routing

| Request | Signed in? | Result |
| --- | --- | --- |
| `/` | no | Marketing Home |
| `/` | yes | redirect `/home` |
| `/product`, `/faq`, … | either | page renders; header shows Sign in/Get started or Open app |
| `/login` | yes | redirect to `returnUrl` if same-origin, else `/home` |
| `/login?returnUrl=https://evil.example` | no | after sign-in → `/home` (open-redirect guard unchanged) |
| `/projects` | no | redirect `/login?returnUrl=%2Fprojects` |
| `/home` | no | redirect `/login?returnUrl=%2Fhome` |
| any unmatched URL | either | root 404 in the marketing shell (signed in shows Open app). `notFound()` inside the app keeps the app 404 |

### 4.33 README → docs mapping

| README section today | New home |
| --- | --- |
| Key features, "Added in v2…v5" | README summary + `history/` + `/changelog` |
| Stack, Architecture, Repo layout | README (short) + `reference/architecture.md`, `reference/repo-layout.md` |
| Prerequisites, Dev loop | `getting-started/` |
| Local Kubernetes, Troubleshooting | `operations/local-kubernetes.md` |
| Upgrade PostgreSQL 17 → 18 | `operations/postgres-18-upgrade.md` |
| Deploy to Railway | README (short) + `operations/railway.md` |
| Configuration reference (a–d) | `reference/configuration.md` + `guides/*` per integration |
| Testing, CI | `reference/testing.md`, `operations/ci.md` |
| CLI | `guides/cli.md` |

### 4.34 Current GitHub App slug

`CurrentSlug(appId, storedSlug, liveAppId, liveSlug)` = `liveSlug` when `liveAppId == appId` and `liveSlug` is usable (1–100 ASCII letters, digits, `-`, `_` or `.`), else `storedSlug` (FR-228).

| GitHub's answer | Used |
| --- | --- |
| same App id, slug `tevrin` | `tevrin` |
| same App id, slug unchanged | stored slug |
| another App id | stored slug |
| none (failed lookup) | stored slug |
| same App id, slug empty or with `/`, space or non-ASCII | stored slug |

## 5. Data

No schema change and no migration. Browser-only state: `aide.theme` (`light|dark|system`, `system` stored explicitly), `aide.home.checklist-dismissed` (`"1"` when dismissed). Marketing content and the changelog are code.

## 6. API

No new endpoints. Changes: OpenAPI `info.title` → `Tevrin API` (contract and client regenerated, additive); ProblemDetails and notification text per FR-198. `/api/auth/me` is called from marketing pages to choose header buttons; its 401 for visitors is expected and not logged as an error in the browser console.

### Error codes

No new error codes. The frontend's error-code copy is reworded where it names the product (FR-197).

## 7. Pages and UI

| Route | Page | Content | States |
| --- | --- | --- | --- |
| `/` | Home (marketing) | FR-212 | signed-in redirect |
| `/product` · `/how-it-works` · `/integrations` · `/self-host` · `/security` · `/faq` · `/changelog` | Marketing | FR-213–FR-219 | static content |
| `/login` · `/register` | Auth | FR-223 | submitting · error · provider unavailable |
| `/home` | Home (app) | FR-207 | loading · empty · error |
| existing app routes | unchanged content | restyled, FR-205/206/208 | loading · empty · error · forbidden |
| `/robots.txt` · `/sitemap.xml` | SEO | FR-221 | — |
| root 404 | Not found | marketing shell for every unmatched URL, signed in or not; links Home and Docs | — |

Devices: responsive from 360 px; marketing designed mobile-first, app desktop-first. Accessibility: WCAG 2.2 AA in both themes.

## 8. Non-functional requirements

- **Accessibility:** Playwright + axe reports zero violations on every marketing page, `/login`, `/register` and `/home`, in light and dark (extends `a11y.spec.ts`). Keyboard-only walk-through of the marketing header, mobile menu, FAQ accordion and theme selector (E2E). Target size ≥ 24 px; focus not obscured.
- **Performance:** marketing pages ship no raster image over 100 KB; the hero LCP element is text or inline SVG in the server HTML; no client data fetch blocks marketing content. Budget: first-load JS for `/` ≤ 200 KB gzipped (checked from the Next build output in CI by `scripts/ci/check-js-budget.mjs`; first measured 192.6 KB, little headroom). Lighthouse performance and accessibility ≥ 90 on `/` against preview is a manual check.
- **Client architecture:** providers live in the `(app)` and `(public)` group layouts, not the root layout; one browser QueryClient is shared across groups. Marketing pages read `me` through `marketing-session.tsx` (generated client, no TanStack Query). The root `error.tsx` is plain markup and the mobile menu sheet is lazy. Zod runs jitless, set in `src/instrumentation-client.ts` before hydration, because its `new Function` probe violates the strict CSP (D-V6-18).
- **Security:** CSP unchanged (no new origins). No third-party scripts, fonts, images or analytics. Marketing pages set the same security headers as the app.
- **Motion:** every animation honours `prefers-reduced-motion` (tested by emulating it in E2E).
- **Observability:** unchanged; marketing requests are logged like other pages (static assets excluded).
- **Browsers:** last two versions of Chrome, Edge, Firefox, Safari.

## 9. Environments

Local dev loop, Docker Desktop Kubernetes, Railway `preview` and `production` as in v5. New: GitHub Pages for the docs site (public repo; Pages source "GitHub Actions" must be enabled in repo settings by Ben). No new secrets or services. The production custom domain stays `aide.benfielder.com` until Q1 is answered.

## 10. Decisions and assumptions

| # | Decision / assumption | Source | Status |
| --- | --- | --- | --- |
| D-V6-1 | Product-facing rename only; internal identifiers keep `aide` (§4.29) | Interview | agreed |
| D-V6-2 | CLI becomes `tevrin` with a deprecated `aide` alias and `TEVRIN_*` env vars with `AIDE_*` fallback | Interview + spec | agreed |
| D-V6-3 | Theme follows the OS by default; light and dark both first-class | Interview | agreed |
| D-V6-4 | Docs published as a GitHub Pages site (Jekyll, just-the-docs) from `docs/` | Interview | agreed |
| D-V6-5 | No pricing page; a Self-host page with "Free while in preview" | Interview | agreed |
| D-V6-6 | Keep the `docs/v6` hexagon concept (segments = phases, gaps = gates) with the refinements in FR-196, rather than a new mark. The research flagged hexagons as common; the gate gaps make it specific to Tevrin | Research + brief | assumed |
| D-V6-7 | Palette: slate neutrals, indigo primary, cyan accent, gradient used sparingly (Linear-style restraint) | Research | assumed |
| D-V6-8 | Geist Sans + Geist Mono replace DM Sans, Outfit and the existing Geist Mono setup | Research | assumed |
| D-V6-9 | Marketing pages live in the same Next.js app (route group `(marketing)`), remain client components per D-V3-2, with server layouts for metadata | Codebase | assumed |
| D-V6-10 | Signed-in users land on a new `/home` dashboard; `/` redirects them there | Research | assumed |
| D-V6-11 | No testimonials, customer logos or invented metrics; integration marks are "Works with" | Research | assumed |
| D-V6-12 | Previews are React components with fixture data, not screenshots | Research + CSP | assumed |
| D-V6-13 | Frozen docs stay at `docs/v1`–`docs/v5` and are linked from `docs/history/`, not moved | CLAUDE.md | agreed |
| D-V6-14 | CLI ships as `Tevrin.Cli` (`tevrin`) plus deprecated `Aide.Cli` (`aide`, `src/Aide.Cli.Alias`); `TEVRIN_GIT_TOKEN` → `AIDE_GIT_TOKEN` added to the env fallback (FR-200) | Build | agreed |
| D-V6-15 | Commit subjects read `[Tevrin][Phase:…][Step:N]` (step detection uses the kept `AIDE-Step-Id` trailer); stack catalog 1.2.0 → 1.2.1; prompt versions idea 6, spec 7, plan 6, codegen 5, review 8, repo analyst 3, stack analyst 3 | Build | agreed |
| D-V6-16 | Checklist rules in §4.30: Git, AI provider and platform definitions; membership checked over the 6 most recently updated projects, no API change in v6 | Build | agreed |
| D-V6-17 | Palette values shifted to pass AA (§4.31); `text-lg` is 20 px; `aide.theme` stores `system` explicitly; `metadataBase` comes from the validated request Host | Build | agreed |
| D-V6-18 | Providers move to group layouts, Zod jitless, `marketing-session.tsx`, JS budget in CI (§8), to meet the 200 KB first-load budget and the CSP | Build | agreed |
| D-V6-19 | Shell and page details: native `title` rail tooltips, theme radio items, server-side search meets FR-208, `?source=import`, root 404 in the marketing shell, robots and sitemap handlers, neutral icons for OpenAI, Slack and Teams, link check skips frozen pages as sources, "In preview" for v6 | Build | agreed |
| D-V6-20 | Post-v6 fixes: the README logo uses GitHub's `#gh-*-mode-only` fragments, which follow the GitHub theme setting (`<picture>` follows the OS); the docs site logo is the v6 light lockup; the How it works lifecycle diagram sits under the page intro, horizontal at `lg` and vertical below (FR-214, FR-224, FR-225) | Build | agreed |
| D-V6-21 | Licence: PolyForm Shield License 1.0.0 (`LICENSE.md`, with `Required Notice` and `Licensor Line of Business` lines). Anyone may use, modify and self-host Tevrin, but not provide a competing product (selling it or offering it as a hosted service). Single author: outside pull requests are not accepted. The name and logo are not licensed (stated in `LICENSE.md`, the README and the brand page): a copy you run may keep them and say it's based on Tevrin, but they can't brand a product or service offered to others or suggest endorsement. The CLI packages ship `LICENSE.md` (`PackageLicenseFile`); copy stays "source available", never "open source" (FR-218, FR-222, FR-224, Q3) | Ben | agreed |
| D-V6-23 | Post-v6 fix: the GitHub App's slug comes from GitHub (`GET /app`, 10-minute cache shared with the FR-80 details, 1-minute retry after a failure, 5-second timeout), so a rename is picked up without a redeploy. A registered App's row is updated; a configured App logs a warning to set `GitHub__App__Slug` (FR-228, Q2) | Ben | agreed |

## 11. Open questions

| # | Question | Who can answer | Blocks |
| --- | --- | --- | --- |
| Q1 | New domain (e.g. `tevrin.<tld>`) and repo rename? Both change URLs in the docs site, README and `DOCS_URL`. Proposed: later, as a one-line constant change | Ben | nothing |
| Q2 | Rename the existing GitHub App and Railway-visible names to Tevrin? Manual in GitHub/Railway settings. The App was renamed after v6 shipped; Tevrin follows the new slug (FR-228, D-V6-23) | Ben | nothing (manual check) |
| Q3 | ~~Add a licence?~~ **Resolved (D-V6-21):** PolyForm Shield License 1.0.0 in `LICENSE.md`. Copy says "source available", never "open source" | Ben | nothing |
| Q4 | Terms and Privacy text for the footer and sign-up? Omitted until supplied | Ben | nothing |

## 12. Milestones

**M0 Baseline:** the repo, CI and v5 app exist. **Do not re-scaffold.** v5's SPEC and PROGRESS are already moved to `docs/v5/`. Add the v6 row to CLAUDE.md, create root `PROGRESS.md`, work on `build/tevrin-v6`, confirm CI is green on the baseline.

1. **M38 Brand and design system** (first; everything after renders with it): assets, tokens, type, theme, components. FR-196, FR-202–FR-205.
2. **M39 Rename** (independent of M38 except the brand component): frontend copy, backend text, prompts, OpenAPI, CLI, compatibility tests. FR-197–FR-201.
3. **M40 App shell and Home** (depends on M38): shell, Home and checklist, page consistency, palette actions. FR-206–FR-209.
4. **M41 Marketing site and auth** (depends on M38, M39): shell, routing, eight pages, previews, SEO, content modules, auth pages. FR-210–FR-223.
5. **M42 Documentation** (depends on M39 for names; can run beside M40–M41): docs site, README, Pages workflow, repo docs. FR-224–FR-227.
6. **M43 Integrate and harden:** E2E (marketing navigation, sign-up from Home, routing table §4.32, reduced motion, theme), axe in both themes, JS budget, spec verifier over **v1–v6**. Manual checks for Ben: enable GitHub Pages, favicon legibility at 16 px, Lighthouse on preview, Docker Desktop redeploy and smoke, an existing session and `aide_pat_` token still working after the preview deploy, GitHub App rename (Q2).

## 13. Research sources

Linear (linear.app; "How we redesigned the Linear UI"), Devin (devin.ai), Factory, Supabase, Vercel Geist, NN/g on empty states, W3C "What's new in WCAG 2.2", web.dev "Optimize LCP", MDN `prefers-reduced-motion`, Google sign-in branding guidelines, and developer-tools landing-page guides. Main takeaways: two hero CTAs repeated at the end, a real product preview, a numbered workflow section, verifiable facts instead of social proof, restrained brand colour on neutral surfaces, ⌘K and collapsible sidebars in the app, and empty states that teach and act.
