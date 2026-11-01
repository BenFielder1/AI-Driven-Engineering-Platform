# Build progress (v5)

Spec: `SPEC.md` (v5; v1–v4 in `docs/`) · Stack: `STACK.md` · Branch: `build/aide-v5` · Started: 2026-11-01

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
| FR-163 | AIDE's own Postgres on 18 outside Railway | 3.1 | deploy + data | M32 | todo | — |
| FR-164 | Existing local data survives the upgrade | 3.1 | deploy | M32 | todo | — |
| FR-165 | Issue trackers section; Jira connection | 3.2 | backend + frontend + data | M35 | todo | — |
| FR-166 | Linear connection | 3.2 | backend + frontend | M35 | todo | — |
| FR-167 | GitHub Issues via App (`issues: read`) | 3.2 | backend + frontend | M35 | todo | — |
| FR-168 | GitLab Issues | 3.2 | backend + frontend | M35 | todo | — |
| FR-169 | Browse and select dialog | 3.2 | backend + frontend | M35 | todo | — |
| FR-170 | Imports are snapshots (`source_items`) | 3.2 | backend + data | M35 | todo | — |
| FR-171 | Import entry points (wizard, new version, Refinement) | 3.2 | frontend + backend | M35 | todo | — |
| FR-172 | Agents use the snapshot; traceability | 3.2 | backend | M35 | todo | — |
| FR-173 | Re-import, remove, drift check | 3.2 | backend + frontend | M35 | todo | — |
| FR-174 | Co-author trailer on every commit, merge, tag | 3.3 | backend | M33 | todo | — |
| FR-175 | Who is credited | 3.3 | backend | M33 | todo | — |
| FR-176 | Which email per host | 3.3 | backend + data | M33 | todo | — |
| FR-177 | Project and personal credit settings | 3.3 | frontend + backend + data | M33 | todo | — |
| FR-178 | Stack step: Infer option | 3.4 | frontend + backend | M36 | todo | — |
| FR-179 | Analysis phase | 3.4 | backend | M36 | todo | — |
| FR-180 | Stack Analyst agent | 3.4 | backend | M36 | todo | — |
| FR-181 | Blanks, unsupported stacks | 3.4 | backend + frontend | M36 | todo | — |
| FR-182 | Analysis page | 3.4 | frontend + backend | M36 | todo | — |
| FR-183 | Edit, approve, reject the profile | 3.4 | frontend + backend | M36 | todo | — |
| FR-184 | Hosts and deployment setup on the page | 3.4 | frontend + backend | M36 | todo | — |
| FR-185 | Continue to Refinement | 3.4 | frontend + backend | M36 | todo | — |
| FR-186 | Inference for new versions | 3.4 | frontend + backend | M36 | todo | — |
| FR-187 | Deployment groups | 3.5 | backend + data | M34 | todo | — |
| FR-188 | Wizard group windows | 3.5 | frontend | M34 | todo | — |
| FR-189 | Separate projects option | 3.5 | frontend + backend | M34 | todo | — |
| FR-190 | Group setup job | 3.5 | backend | M34 | todo | — |
| FR-191 | Settings → Deployment group cards | 3.5 | frontend + backend | M34 | todo | — |
| FR-192 | Platform mapping | 3.5 | backend | M34 | todo | — |
| FR-193 | Events and notifications | 3.6 | backend + frontend | M37 | todo | — |
| FR-194 | CLI additions | 3.6 | cli + backend | M37 | todo | — |

## Phases
- [x] M0 Baseline (base: 391a81d): CI green on `main` ([run 41312655301](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41312655301)); no re-scaffold
- [ ] M32 PostgreSQL 18: FR-163, FR-164
- [ ] M33 Commit co-authorship: FR-174–FR-177
- [ ] M34 Deployment groups: FR-187–FR-192
- [ ] M35 Tracker connections and imports: FR-165–FR-173
- [ ] M36 Stack inference and the Analysis phase: FR-178–FR-186
- [ ] M37 Integrate and harden: FR-193, FR-194, E2E, axe, perf, docs, verifier over v1–v5

## Decisions
- 2026-11-01 (M32): FR-163's "v4 §8 test note" stays unchanged because `docs/` is frozen; SPEC §8 for v5 carries the test scope.
- 2026-11-01 (M32, Q5): Testcontainers.PostgreSql 4.15.0 needs no change for `postgres:18` (it sets no PGDATA or data mount; from reading the package). `readOnlyRootFilesystem` looks fine from reading the image's entrypoint; unverified at runtime.
- 2026-11-01 (M32): the Kubernetes upgrade renames `pgdata` to `pgdata-17` on the same volume, which clears the `deploy-local.sh` guard without deleting anything.

## Known issues

## Manual checks (Ben)
- **M32 PostgreSQL 18 (nothing here has run against a real cluster):** on a cluster with 17 data, `deploy-local.sh` exits non-zero and the volume is unchanged. Run the README's Kubernetes upgrade end to end, confirming the rename works as uid 999 on the mount root, 18 starts with the read-only root filesystem, and the restore's "expected errors" list is complete. Then run the compose procedure and a fresh-cluster deploy.
