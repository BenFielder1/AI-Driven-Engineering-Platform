# Tech stack presets (FR-74–FR-77, §4.11, §4.12, D-V2-12)

Each folder here is one preset: a versioned, content-hashed bundle of **data** (no code) that tells AIDE's agents how to
build on a particular stack. The folder name is the preset id.

| Preset | Stack | Specialists |
| --- | --- | --- |
| `nextjs-supabase/` | Next.js (App Router), TypeScript, Tailwind, Supabase (Postgres, Auth, RLS, Realtime), Vitest, Playwright, GitHub Actions, Vercel | project-scaffolder, db-engineer, domain-logic-engineer, api-engineer, ui-engineer, test-engineer, devops-engineer |
| `nextjs-frontend/` | The same without Supabase; data from external APIs (through BFF route handlers) or mocks | the same without db-engineer |

## Provenance

The agent prompts and skills are **adapted, not copied**, from `BenFielder1/claude-kits@14ada86`
(`plugins/nextjs-supabase-kit`: `agents/*.md` and skills `supabase-migration`, `nextjs-api-route`, `nextjs-ui`,
`domain-logic`, `github-vercel-delivery`). `nextjs-frontend` is derived from the same kit minus Supabase.

The kit's agents run tools, shells and multi-file phases. AIDE specialists produce **one plan step's files as structured
output** (the base `Prompts/code_generator.md` contract), so the adaptation:
- drops instructions about running commands, Git, branches, pull requests, deploys and reviews;
- keeps each agent's ownership, rules, checklist and "done when", rewritten as requirements on the output ("would pass",
  "list in `notes`");
- drops the kit's orchestrator, code-reviewer, spec-auditor and docs-writer agents (AIDE's own Plan Generator, Code
  Review agent and Guardian do those jobs; review criteria moved into `review.checklist`);
- adds AIDE specifics: `vercel.json` `git.deploymentEnabled` `"aide/**": false` (FR-89), CI that installs without a
  lockfile (AIDE never commits lockfiles), and the deterministic review rules.

## Folder layout

```
<id>/
  preset.json          the manifest (format: backend/src/Aide.Domain/Presets/README.md and PresetLoader)
  conventions.md       specConventions: stack conventions for the Spec Generator and every specialist
  agents/<key>.md      one specialist prompt per catalogue entry (prompt fragment, no front matter)
  skills/<name>.md     condensed skill rules, referenced by name from agents[].skills
```

Prompt files are fragments: the renderer (`SpecialistPrompt`) puts them under its own `##` headings after the base code
generator prompt, so they start with prose and use `###` headings. Don't write `{{UPPER_SNAKE}}` text in them (it is
template placeholder syntax).

## Adding a preset

1. Create `<id>/` (lowercase kebab-case) with `preset.json`, `conventions.md`, `agents/` and `skills/`. Start from the
   closest existing preset.
2. Give it `"version": "1.0.0"`. Bump the version whenever any file in the folder changes: the content hash changes
   either way, and runs record `<id>@<version>`.
3. Every `agents[].prompt`, every `skills/<name>.md` named in `agents[].skills` and the `specConventions` file must
   exist; a missing file fails loading at startup (health check) and in the unit test that loads every preset.
4. Use only the vocabulary the loader accepts: review categories `feature_coverage`, `api_contracts`, `data_model`,
   `architecture`, `security`, `testing`; severities `minor`, `major`, `critical`; deploy targets `vercel`; env
   targets `production`, `preview`, `development`; deterministic rules as listed in `PresetVocabulary`.
5. Add tests for any new plan rule or deterministic rule the preset relies on. No schema change is needed (D-V2-12).
