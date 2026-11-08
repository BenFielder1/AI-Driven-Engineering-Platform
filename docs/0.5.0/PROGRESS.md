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
| FR-163 | AIDE's own Postgres on 18 outside Railway | 3.1 | deploy + data | M32 | done | CI 41323663409 |
| FR-164 | Existing local data survives the upgrade | 3.1 | deploy | M32 | done (upgrade path: manual check) | CI 41323663409 |
| FR-165 | Issue trackers section; Jira connection | 3.2 | backend + frontend + data | M35 | done | CI 41402440433 |
| FR-166 | Linear connection | 3.2 | backend + frontend | M35 | done | CI 41402440433 |
| FR-167 | GitHub Issues via App (`issues: read`) | 3.2 | backend + frontend | M35 | done | CI 41402440433 |
| FR-168 | GitLab Issues | 3.2 | backend + frontend | M35 | done | CI 41402440433 |
| FR-169 | Browse and select dialog | 3.2 | backend + frontend | M35 | done | CI 41402440433 |
| FR-170 | Imports are snapshots (`source_items`) | 3.2 | backend + data | M35 | done | CI 41402440433 |
| FR-171 | Import entry points (wizard, new version, Refinement) | 3.2 | frontend + backend | M35 | done | CI 41402440433 |
| FR-172 | Agents use the snapshot; traceability | 3.2 | backend | M35 | done | CI 41402440433 |
| FR-173 | Re-import, remove, drift check | 3.2 | backend + frontend | M35 | done | CI 41402440433 |
| FR-174 | Co-author trailer on every commit, merge, tag | 3.3 | backend | M33 | done | CI 41326693467 |
| FR-175 | Who is credited | 3.3 | backend | M33 | done | CI 41326693467 |
| FR-176 | Which email per host | 3.3 | backend + data | M33 | done | CI 41326693467 |
| FR-177 | Project and personal credit settings | 3.3 | frontend + backend + data | M33 | done | CI 41326693467 |
| FR-178 | Stack step: Infer option | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-179 | Analysis phase | 3.4 | backend | M36 | done | CI 41526801694 |
| FR-180 | Stack Analyst agent | 3.4 | backend | M36 | done | CI 41526801694 |
| FR-181 | Blanks, unsupported stacks | 3.4 | backend + frontend | M36 | done | CI 41526801694 |
| FR-182 | Analysis page | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-183 | Edit, approve, reject the profile | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-184 | Hosts and deployment setup on the page | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-185 | Continue to Refinement | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-186 | Inference for new versions | 3.4 | frontend + backend | M36 | done | CI 41526801694 |
| FR-187 | Deployment groups | 3.5 | backend + data | M34 | done | CI 41342366096 |
| FR-188 | Wizard group windows | 3.5 | frontend | M34 | done | CI 41342366096 |
| FR-189 | Separate projects option | 3.5 | frontend + backend | M34 | done | CI 41342366096 |
| FR-190 | Group setup job | 3.5 | backend | M34 | done | CI 41342366096 |
| FR-191 | Settings → Deployment group cards | 3.5 | frontend + backend | M34 | done | CI 41342366096 |
| FR-192 | Platform mapping | 3.5 | backend | M34 | done | CI 41342366096 |
| FR-193 | Events and notifications | 3.6 | backend + frontend | M37 | done | CI 41702869067 |
| FR-194 | CLI additions | 3.6 | cli + backend | M37 | done | CI 41702869067 |
| FR-195 | Generated apps' Postgres on 18 (Railway, Render, Compose) | 3.1 | backend + deploy | post-M37 | done | CI 41713212094 |

## Phases
- [x] M0 Baseline (base: 391a81d): CI green on `main` ([run 41312655301](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41312655301)); no re-scaffold
- [x] M32 PostgreSQL 18: FR-163, FR-164 ([CI 41323663409](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41323663409), head c4710e8)
- [x] M33 Commit co-authorship: FR-174–FR-177 ([CI 41326693467](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41326693467), head f6b74f1)
- [x] M34 Deployment groups: FR-187–FR-192 ([CI 41342366096](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41342366096), head b2dfe44)
- [x] M35 Tracker connections and imports: FR-165–FR-173 ([CI 41402440433](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41402440433), head 65ff4ee)
- [x] M36 Stack inference and the Analysis phase: FR-178–FR-186 ([CI 41526801694](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41526801694), head 01bce28)
- [x] M37 Integrate and harden: FR-193, FR-194, E2E, axe, perf, docs, acceptance audit over v1–v5 (74 Done, 11 Partial closed by the audit-gap commit, 0 Missing) ([CI 41702869067](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/actions/runs/41702869067), head 06cf591)

## Decisions
- 2026-11-01 (M32): FR-163's "v4 §8 test note" stays unchanged because `docs/` is frozen; SPEC §8 for v5 carries the test scope.
- 2026-11-01 (M32, Q5): Testcontainers.PostgreSql 4.15.0 needs no change for `postgres:18` (it sets no PGDATA or data mount; from reading the package). `readOnlyRootFilesystem` looks fine from reading the image's entrypoint; unverified at runtime.
- 2026-11-01 (M32): the Kubernetes upgrade renames `pgdata` to `pgdata-17` on the same volume, which clears the `deploy-local.sh` guard without deleting anything.

- 2026-11-01 (M33): "person who started the release" is the actor of that attempt's `RELEASE_STARTED` event, and "project creator" is the `PROJECT_CREATED` event actor, because neither table has a column. A connection with invalid status still counts as an identity (the id and login remain true). The personal opt-out records no event. GitHub `auto_init` and GitLab `initialize_with_readme` commits are written by the host, so they carry no trailer. A Bitbucket user linked through the confirmed pending-link path gets their commit email on the next refresh or re-test, and is skipped until then.

- 2026-11-01 (M34, spec sync in M37): `deployment_targets.platform_project_id` is added to §5, so a created Railway project is resumed rather than refused. Additive beyond §6: `windows[]` and `caveats[]` on `/stack/options`, `?type=` pickers, nullable `link.resourceId` for Vercel. A group's database setup runs as its own `database_provisioning` jobs. A group `plan` applies to services only. DELETE on a group clears its targets and databases in AIDE, and platform resources stay. A part with its own resource blocks group creation (D-V5-9). A lone Render database keeps v4's two wizard windows; a lone Railway database has one, as v4 offered no Railway preview. Catalog version 1.2.0.
- 2026-11-01 (M34, Q5, public docs and schema only, not a live account): Render documents `POST /v1/environments/{id}/resources {resourceIds}`, used here, and a failure is non-blocking. Railway's schema has `TCPProxyCreateInput {applicationPort, environmentId, serviceId}` and `TCPProxy.domain/proxyPort`.

- 2026-11-01 (M35, spec sync in M37): `tracker_import.supersedes_import_id` is added to §5 (the retry chain). Re-import while a retry is pending returns that retry. Removing an import also removes its unfinished and failed retries. A Jira token without read access saves as `insufficient_scope` per the v4 rule, and browsing gives `CONNECTION_SCOPE_INSUFFICIENT`. The prompts use the optional `{{SOURCE_ITEMS?}}` syntax so projects with no imports render. `POST /sources` also returns `TRACKER_NOT_CONNECTED` and 404. Item fetch time isn't rendered, so an unchanged re-import keeps its hash. A failed import counts toward `source_items_unavailable`. PATCH on a non-tracker with tracker-only fields is `VALIDATION`.
- 2026-11-01 (M35, Q5): no live accounts, so Jira's granular scope names (`read:jira-work`, `read:jira-user` in the UI are inferred), the `_edge/tenant_info` shape, JRACLOUD-94547 and Linear Free-plan limits are all unverified. The Jira and Linear logos were drawn from memory.

- 2026-11-02 (M36): FR-183 wins over FR-17: rejecting a `stack_profile` always re-runs the Stack Analyst, with the rejected draft as context, even when a person edited it. PUT /stack stays allowed during analysis, because entering refinement still needs the locked profile. `Phase.Analysis` is first in the enum; phases are stored as strings everywhere, which was checked. Profile drafts and approval are allowed only in analysis. Unknown or conflict layers block approval even with `generalist`. Infer stays offered for URL and Local imports the wizard can't see into; `STACK_INFER_NO_CODE` covers empty repos at create. Detection pre-fills layers whichever stack kind is chosen.

- 2026-11-02 (M37, spec sync): new event `CONNECTION_EXPIRING` sent through the personal `connection_invalid` rules; `TRACKER_IMPORT_FAILED` gains `importedBy`. The seed's Fake tracker import sits on the new analysis demo ("Demo: Weather Board", Vite + React skeleton), as no seeded demo is in refinement. `aide project create` also gains `--import-version`, `--released-tag` and `--local-path`, because inference needs an imported repository. Channels can subscribe to every selectable event (21; the old cap of 14 is lifted). New config: `Trackers__Provider` (unset = real; Fake only in Development/Testing or with `Deployments__AllowFake`), `Trackers__SearchTimeout` (15 s), `Notifications__ConnectionExpirySweepInterval` (1 h), optional `Llm__Providers__<P>__Models__StackAnalyst`.

- 2026-11-03 (audit gaps): `GET /analysis` takes optional `releaseVersionId` for past versions (read-only). New optional setting `Trackers:CheckTimeout` (default 10 s; startup refuses more than 10 s) so tests can shorten the `sources/check` cap. The §8 co-author trailer check is an integration test, not an E2E, because E2E uses Local Git (SPEC §8 updated).

- 2026-11-03 (FR-195, Ben): generated apps' Railway (`postgres-ssl:18`, volume at `/var/lib/postgresql/data`, `PGDATA=/var/lib/postgresql/data/pgdata`, as AIDE's own Railway database), Render (`version: "18"`) and Compose (`postgres:18`) databases move to 18. Existing databases are never changed. A linked Railway Postgres keeps the Owner's image. Only an `aide-preview` instance with no volume is re-imaged to 18; adopting a production service never changes its image. No catalog bump (skill text only; no hash is pinned).

## Known issues
- Test fidelity (minor): the frontend MSW mock returns no pinned composition for a past version's Analysis page (the real API does, and an integration test covers it), so Vitest can't show it.
- Wizard (found by M37 E2E, minor UX): the form validates on touch, so after fixing a focused field that showed an error, its error box disappears as the field loses focus; the form moves up and the first Next click can miss the button. Possible fix: `reValidateMode: 'onChange'` or an error area of fixed height.
- M34 (minor): the Railway public-versus-private `DATABASE_URL` is chosen when the database is set up, so changing the stack afterwards needs that database set up again.

## Manual checks (Ben)
- **FR-195 on real accounts:** create a Railway Postgres from AIDE and check it starts on 18. Next to a production Postgres on 17, create the preview and check the preview runs 18 while production is untouched; the `serviceInstanceUpdate {source:{image}}` call is unverified against a live account. Create a Render Postgres and check it's version 18.
- **Railway (M37):** in each environment run `railway config plan` (preview first) and expect only `variables.ignore` updates, with no variable changes or destroys; warnings that new ignore names match no variable are expected. Then `railway config apply` (production only when you decide). `Trackers__Provider` must stay unset there. No secrets to set.
- **M35 trackers against real accounts (CI uses stubs):** update the GitHub App to add `issues: read` and accept on each installation. Connect a Jira site with a scoped read-only token (check the scope names in the dialog are right) and a Linear key. Import from each of Jira, Linear, GitHub and GitLab, check ADF renders, re-import, remove, and the drift check. Eyeball the Jira and Linear logos.
- **M34 groups against real APIs (CI uses stubs):** on Railway, create a group for all three parts and check one project with a preview environment; split the database and check the TCP-proxy `DATABASE_URL` works; link an existing project with Postgres and check the preview database comes up. On Render, create a group and check services and Postgres are placed in the project's environments. On Vercel, check `<name>-web` and `<name>-api`.
- **M33 co-authorship:** on real GitHub, confirm a step commit shows both avatars and counts on the contributions graph once on the default branch. Check the GitLab noreply address credits the user on gitlab.com and self-managed, and how Bitbucket displays the line (SPEC Q5).
- **M32 PostgreSQL 18 (nothing here has run against a real cluster):** on a cluster with 17 data, `deploy-local.sh` exits non-zero and the volume is unchanged. Run the README's Kubernetes upgrade end to end, confirming the rename works as uid 999 on the mount root, 18 starts with the read-only root filesystem, and the restore's "expected errors" list is complete. Then run the compose procedure and a fresh-cluster deploy.
