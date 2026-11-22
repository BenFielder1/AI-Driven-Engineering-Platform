---
title: Stacks and inference
parent: Guides
nav_order: 7
---

# Stacks and inference

## Stack composition

Pick one option per layer in the wizard's Stack step. The options come from a versioned catalog of frontend, backend, data, version control and deployment hosts, adapted from the stack-packs. An Owner can change the stack until the implementation plan is locked, and again when starting a version.

- The catalog is data under `backend/src/Aide.Infrastructure/Agents/Stack/` (`catalog.json`, `packs/`, `agents/`, `core/`). It is content-hashed.
- The backend owns the compatibility rules. `POST /api/stack/options` and `/validate` serve the wizard, Settings and the API.
- Each release version pins the composition and the catalog version it was built with. Each run records the packs used.
- Generic specialist agents load the skills of the chosen packs. Spec sections, plan rules, protected paths and the review checklist come from the composition.
- Each deploy pack lists the config files it requires. Plans must create them, and Tevrin adds a step marked **Added by Tevrin** when one is missing. See [Deployments and databases](deployments-and-databases.md).
- Projects that used the 0.2.0 presets were converted to equivalent compositions.

This catalog describes the stacks of the projects Tevrin builds. It is separate from [Tevrin's own stack](../reference/architecture.md).

## The origin model

The origin model says how the browser reaches the backend: `same-origin`, `proxy` or `cross-origin`. The options depend on the chosen frontend, backend and hosts. Changing the frontend, backend or their hosts resets it, with a warning.

## Stack inference and the Analysis phase

For a repository that already has code, the wizard can infer the stack instead of asking you to choose.

1. The version starts in an **Analysis** phase before Refinement.
2. A Stack Analyst agent reads the repository and writes a `stack_profile`.
3. An Owner fills the blanks, edits it and approves it. This is required in every approval mode.
4. Hosts and deployment setup live on the Analysis page.

An approval is refused while layers are unresolved (`STACK_PROFILE_INCOMPLETE`). A repository with no code can't be inferred (`STACK_INFER_NO_CODE`). Scripts can call the same endpoints (`/api/stack/detect`, `/api/projects/{id}/analysis`) with an API token.

## Database migrations for generated projects

- Tevrin itself runs only Supabase BaaS migrations, just before the merge.
- For ORM stacks, the platform runs the data pack's migrate command: pre-deploy, at container start, or in the production build when `AIDE_RUN_MIGRATIONS=true`.
