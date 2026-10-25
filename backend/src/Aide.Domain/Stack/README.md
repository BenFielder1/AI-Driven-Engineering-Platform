# Stack catalog format (SPEC §4.16, §4.17)

The catalog is the folder `backend/src/Aide.Infrastructure/Agents/Stack/` (embedded resources), loaded once at startup by
`StackCatalogLoader` into the pure types of this folder. Any problem fails the whole catalog, and the `stack-catalog`
readiness check is then unhealthy (FR-122).

```
Stack/
  catalog.json              version, layers[], choices[], rules, recommendations[]
  packs/<pack-id>/
    pack.json               manifest (PackManifest; unknown properties are load errors)
    skill.md                adapted SKILL.md; its "## " headings are the section interface
  core/
    stack-api-contract.md   adapted stack-core skill
    composition-summary.md  template rendering the STACK.md equivalent for prompts
  agents/<key>.md           adapted stack-core agent prompts (FR-120); README.md is ignored
  legacy/                   frozen v2 preset globs and agent-key map (§4.23); not read yet
```

Every adapted file starts with a provenance comment. For `skill.md` it must be
`<!-- Adapted from stack-packs <plugin>@<version> …` matching the pack's `source`.

## catalog.json

- `version`: semver. With the SHA-256 over every file (v2 preset hash: sorted paths, `path\0content\0`, `\n` endings,
  BOM stripped) it is what release versions pin and agent runs record (`stack@<version>`).
- `layers`: the five layers, in order: `frontend`, `backend`, `data`, `vcs`, `deploy`.
- `choices`: the nine choices in evaluation order: `frontend`, `backend`, `data`, `vcs`, then the deployment choices
  `frontendHost`, `backendHost`, `databaseHost`, `local`, `originModel`. Each option is
  `{id, pack?, name?, description?, chips?, mode?, vcsHost?, selfManagedVcsHost?}`; a pack option takes its name, chips and
  description from `pack.json` unless overridden. Every choice but `vcs` and `originModel` offers `none`.
- `rules.statements`: rule id → statement (`R1`…`R12`).
- `rules.constraints[]`: `{rule, choices[], options[], state: forbidden|caveat, when?[], unless?[], text}`. The entry
  applies to each listed option of each listed choice when any `when` clause holds (or there is none) and no `unless`
  clause holds. A clause is `{fact: [values]}` (all facts must match; `!x` excludes a value). Facts are earlier choices
  plus `vcsHost` and `nextjsMode` (R2). A rule may only read facts chosen before its choice (load error otherwise).
  A clause whose facts aren't all chosen yet never fires the rule. `text` may use `{option}` and `{<earlier choice>}`
  (option names).
- `rules.originModel[]`: R10, first match wins: `{when?[], allowed[], default?, text}`; the last entry has no `when`.
- `rules.defaults[]`: `{choice, option | sameAs, when?[], implicit?}`; the first applicable, non-forbidden entry is the
  choice's default. `implicit` defaults also fill a choice a validated composition leaves out.
- `recommendations[]`: `{id, name, choices: {choice: [option ids]}}` from stack-packs `compose-stack`.

## pack.json

See `PackManifest` and SPEC §4.16. Globs may use the layer-path placeholders `{fe}`, `{be}` and `{app}` (the backend
path, else the frontend path). `agentSections` values are `*` or skill headings matched after stripping a trailing
parenthetical (`## Modelling (BaaS)` is `Modelling`); a heading the skill doesn't have, or `Agent autonomy`, fails loading.
