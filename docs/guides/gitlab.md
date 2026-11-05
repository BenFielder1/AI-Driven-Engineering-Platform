---
title: GitLab
parent: Guides
nav_order: 3
---

# GitLab (gitlab.com or self-managed)

- Register an OAuth application on gitlab.com (User or Group settings → Applications) or on the self-managed instance, with the redirect URI `<App:PublicBaseUrl>/api/auth/gitlab/callback` (`http://localhost:3000/...` in the dev loop, `http://localhost:8080/...` on Docker Desktop, the environment's web URL on Railway) and the scopes Admin → Integrations → Git hosts shows (SPEC §4.19). GitLab labels `http://localhost` URIs "development" but accepts them.
- Then add the instance in Admin → Integrations → Git hosts (stored encrypted), or set `GitLab:Instances:<n>:*` (dev: user-secrets; k8s: the commented `GitLab__Instances__0__*` lines in `secrets.env`, then re-run `deploy-local.sh`; Railway: [deploy/railway/variables.md](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/variables.md)). Config wins over a DB row with the same host, and a bad instance stops the API at start-up.
- Hosts on private networks are allowed locally (`GitLab:AllowPrivateNetworks=true`) and refused on Railway (`false`), where those addresses are Tevrin's own services.
