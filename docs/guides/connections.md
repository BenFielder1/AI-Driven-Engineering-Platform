---
title: Connections and sign-in
parent: Guides
nav_order: 1
---

# Connections and sign-in

Each user keeps their own connections under **Account, Connections**: Git hosts (GitHub, GitLab, Bitbucket), LLM provider keys, issue trackers and deployment platforms. Tevrin checks a token when you add it, stores it encrypted and never shows it again.

Sign-in works with email and password. You can also add GitHub sign-in with an OAuth App (below), or with the GitHub App, which is recommended: see [GitHub App](github-app.md). The first registered user becomes Admin.

| Connection | Guide |
| --- | --- |
| GitHub | [GitHub App](github-app.md) |
| GitLab | [GitLab](gitlab.md) |
| Bitbucket Cloud | [Bitbucket](bitbucket.md) |
| LLM providers | [LLM providers](llm-providers.md) |
| Jira, Linear, GitHub Issues, GitLab Issues | [Issue trackers](issue-trackers.md) |
| Vercel, Netlify, Cloudflare, Render, Railway, Supabase | [Deployments and databases](deployments-and-databases.md) |

## GitHub sign-in with an OAuth App

1. GitHub, Settings, Developer settings, OAuth Apps, New OAuth App.
2. Homepage URL: `http://localhost:8080` (Kubernetes) or `http://localhost:3000` (dev loop).
3. Authorization callback URL: `<homepage>/api/auth/github/callback`. The code builds the callback from the host the browser used, so it must match exactly:
   - Kubernetes: `http://localhost:8080/api/auth/github/callback`
   - Dev loop: `http://localhost:3000/api/auth/github/callback` (sign in through the Next.js URL, not 5080)
   If your OAuth App takes only one callback URL, create one app per host.
4. Generate a client secret. Scopes requested: `read:user`, `user:email`.
5. Set `GitHub:OAuth:ClientId` and `GitHub:OAuth:ClientSecret` (dev: user-secrets; k8s: `GitHub__OAuth__ClientId` and `GitHub__OAuth__ClientSecret` in `secrets.env`, then re-run `deploy-local.sh`).

If either value is unset, the "Continue with GitHub" button is hidden and `/api/auth/github*` returns 404. Email and password sign-in still works. A failed sign-in redirects to `/login?error=GITHUB_AUTH_FAILED&reason=<oauth_failed|link_required|already_linked>`. A GitHub email that matches an existing unlinked account is not auto-linked: sign in, then link GitHub on the Account page.
