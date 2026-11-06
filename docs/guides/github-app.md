---
title: GitHub App
parent: Guides
nav_order: 2
---

# GitHub App

## Register the Tevrin GitHub App (recommended)

1. Admin → Integrations → GitHub → Register: Tevrin posts a manifest to GitHub (your account or an organisation). The App is named `Tevrin (<host of App:PublicBaseUrl>)`, has its webhook off, and asks for Contents, Pull requests, Workflows and Administration (write), Metadata, Checks and Commit statuses (read), and email addresses (read). Untick Administration to disable "Create repository".
2. GitHub redirects back to `/api/admin/integrations/github/callback`; Tevrin stores the App id, slug, client id and the client secret and private key encrypted (Data Protection, one purpose per secret). Neither is ever returned.
3. Install the App on the repositories (or organisations) Tevrin should use; the install link is on the Integrations page and in the repo picker.

Once an App exists, "Continue with GitHub" uses its user authorization (PKCE) and also connects GitHub (Account → Connections); v1 links keep working because accounts are matched by GitHub user id. Projects default to the `github_app` mode: background Git work uses installation tokens minted per job (scoped to the repository, cached at most 55 minutes, never on disk, in remote URLs or logs), commits are made by `<slug>[bot]`, and the PR links back to the project. Instead of the manifest flow, set all of `GitHub__App__AppId`, `__ClientId`, `__ClientSecret`, `__PrivateKey` and `__Slug` in `secrets.env` (they take precedence). GitHub rate limits answer `429 GITHUB_RATE_LIMITED` with `Retry-After`; background jobs wait for the reset without counting a failed attempt.

### Renaming the App

Renaming a GitHub App changes its slug (the `github.com/apps/<slug>` link). Tevrin asks GitHub for the App's current slug (`GET /app`, cached for 10 minutes) and uses it for install links, the App page and the bot commit identity, so a rename is picked up without a restart. A registered App's stored slug is updated in place. For an App set through `GitHub__App__*`, the API logs a warning until you set `GitHub__App__Slug` to the new slug. When GitHub can't be reached, the stored slug is used. Existing installations keep working: they're tied to the App id, not the name.

## Use GitHub with a personal access token

- In the project wizard or Settings, Git tab, choose GitHub, enter `https://github.com/OWNER/REPO` and the default branch, then paste a personal access token. The token is stored encrypted and never returned. Use "Test connection" to check it.
- Fine-grained token, on that repository: Contents read and write (clone, push, tag), Pull requests read and write (open, update, merge). Classic token: `repo`.
- Branch protection that blocks the merge will make a release fail. The test reports reachable, writable and default branch present.
- `GitHub:GitBaseUrl`, `GitHub:ApiBaseUrl` and `GitHub:ReleaseMergeMethod` are system settings. A project only supplies owner and repo. Repo URLs must start with `https://github.com/` in v1.
