# Preset format (FR-74–FR-77, SPEC §4.11, D-V2-12)

A preset is a folder `backend/src/Aide.Infrastructure/Agents/Presets/<id>/`, embedded into `Aide.Infrastructure`
(every file under it). It is loaded once at startup by `PresetCatalog` → `PresetLoader.Load` (this folder).
**Any problem fails the whole preset**: it is left out of `GET /api/presets`, logged as an error, and the readiness
check `/api/health/ready` reports `presets` unhealthy. The unit test `FR74_Every_shipped_preset_loads` lists the reasons.

```
<id>/
  preset.json          the manifest (below)
  conventions.md       optional, when specConventions references it
  agents/<key>.md      one prompt per agent (any path works; it is the agent's "prompt" value)
  skills/<name>.md     one file per skill name used by any agent
```

Files are UTF-8 text. Prompts, skills and conventions are used verbatim (trimmed; no front matter needed).
Unreferenced files are allowed and still count towards the content hash.

## Content hash and version

- `version` is the preset's own version (≤ 50 chars, no whitespace or `@`; semver recommended). Bump it whenever
  the content changes in a way users should see. Runs record `<id>@<version>` (`Preset.RunLabel`).
- `contentHash` = SHA-256 (lowercase hex) over all files sorted by relative path (ordinal), each contributing
  `path \0 content \0` (UTF-8, line endings normalised to `\n`, BOM stripped). It is pinned on the release version
  (`preset_hash`) next to `preset_id` / `preset_version`.

## `preset.json`

JSON (comments and trailing commas allowed; **unknown properties are errors**, so typos are caught). camelCase.

| Field | Type | Rules |
| --- | --- | --- |
| `$schema` | string? | ignored |
| `id` | string | lowercase kebab-case, ≤ 100, **equals the folder name** |
| `name` | string | ≤ 100 |
| `version` | string | see above |
| `description` | string | ≤ 2000 |
| `stack` | string[] | ≥ 1 chip, e.g. `"Next.js (App Router)"` |
| `deployTargets` | string[] | each `vercel` (may be empty) |
| `requiredEnv` | object[] | `{key, targets, sensitive, description}`: `key` UPPER_SNAKE_CASE and unique; `targets` ⊆ `production`, `preview`, `development` (≥ 1); `sensitive` bool (default false); `description` required |
| `specSections` | object[] | `{title, guidance}`: titles unique (case-insensitive) ≤ 200; guidance ≤ 4000. Spec validation then requires each title as a section (FR-75) |
| `specConventions` | string? | inline Markdown, **or** a file reference: a single-line relative path ending in `.md` (e.g. `"conventions.md"`), which must exist |
| `planRules` | object? | see below |
| `agents` | object[] | ≥ 1, see below |
| `review` | object? | see below |
| `protectedPaths` | (string \| object)[] | see below |

### `agents[]`

| Field | Rules |
| --- | --- |
| `key` | lowercase kebab-case, unique, not `generalist` |
| `name`, `description` | required (≤ 100 / ≤ 2000) |
| `owns` | ≥ 1 glob; `!`-prefixed globs exclude (`["app/**", "!app/api/**"]`). Files outside → `AGENT_SCOPE` warning |
| `skills` | names (kebab-case, unique per agent) → `skills/<name>.md` must exist; rendered in this order |
| `prompt` | path of the prompt file inside the folder (e.g. `agents/db-engineer.md`); must exist and not be empty |

Specialist prompt order (`SpecialistPrompt.Compose`): base `code_generator.md` system prompt → `## Stack conventions`
(specConventions) → `## Your role: <name> (<key>)` with the owned globs, then the agent prompt → `## Skill: <name>` per
skill → the step input (the base template's user message).

### `planRules` (checked after the v1 plan rules, `PresetPlanValidator`)

| Field | Meaning |
| --- | --- |
| `scaffoldFirstWhenEmpty` | bool. On an empty repository step 1 must be assigned the scaffolder (error) |
| `scaffolder` | agent key, default `project-scaffolder`; must be in the catalogue when `scaffoldFirstWhenEmpty` |
| `order[]` | `{before: glob[], after: glob[], soft?: bool, reason?: string}`: a step declaring a file matching `before` must come before every step declaring a file matching `after`. Error (`INVALID_PLAN`), or `PLAN_ORDER` warning when `soft`. Steps assigned the scaffolder are exempt (its `app/layout.tsx` or `lib/supabase/**` don't trigger order rules) |
| `exclusive[]` | `{agent, notWithGlobs: glob[], reason?}`: a step assigned `agent` (must be in the catalogue) must not declare files matching `notWithGlobs` (error) |
| `maxFilesPerStep[]` | `{glob, max}` (max 1–100): a step declares at most `max` files matching `glob` (error) |
| `oneMigrationPerStep` | `{glob}`: shorthand for a `maxFilesPerStep` entry with `max: 1` |

Unknown agent keys in a plan, or a missing `agent`, are `INVALID_PLAN` naming the agent (v1 `PlanValidator`).

### `review`

| Field | Rules |
| --- | --- |
| `checklist[]` | `{category, item, severity}`: category ∈ `feature_coverage`, `api_contracts`, `data_model`, `architecture`, `security`, `testing`; severity ∈ `minor`, `major`, `critical` |
| `deterministic[]` | rule ids ∈ `rls_on_every_table`, `no_secret_env_in_client` (`PresetVocabulary.DeterministicRules`) |

### `protectedPaths[]`

Each entry is a glob string, or `{glob, existingOnBase?: bool, except?: glob[], reason?: string}`:
- `existingOnBase: true` protects only files that already exist on the version's base commit (immutable migrations).
- `except` lists globs that stay writable (`{"glob": ".env*", "except": [".env.example"]}`).

Protected paths add to the Guardian's list with the v1 "unless declared in this step" exception.

## Globs (`PathGlob`)

Matched against normalised repository paths (forward slashes, case-insensitive, like v1 `PathRules`).
- A glob **with** a `/` is anchored at the repository root: `app/**`, `supabase/migrations/**`, `lib/*/**`.
- A glob **without** a `/` matches that name in any folder (like `.gitignore`): `.env*`, `package.json`.
- `**` (a whole segment) = zero or more folders; `*` = any characters except `/`; `?` = one character except `/`;
  `{a,b}` = either alternative (not nested). Everything else is literal. No leading `/`, `.` or `..` segments.

## Adding a preset

Add a folder with the files above and a test (`FR74_*`); the loader, the catalogue and `GET /api/presets` pick it up.
No schema change (D-V2-12).
