---
title: Bitbucket
parent: Guides
nav_order: 4
---

# Bitbucket Cloud

- Add an OAuth consumer in Bitbucket (Workspace settings → OAuth consumers) with the callback URL `<App:PublicBaseUrl>/api/auth/bitbucket/callback` and the permissions Admin → Integrations → Bitbucket lists: Account, Email, Repositories write and admin, Pull requests write, Projects, Webhooks. Tick "This is a private consumer".
- Save its key and secret in Admin → Integrations → Bitbucket (stored encrypted), or set `Bitbucket:OAuth:ClientId` and `ClientSecret` (dev: user-secrets; k8s: the commented `Bitbucket__OAuth__*` lines in `secrets.env`; Railway: [deploy/railway/variables.md](https://github.com/BenFielder1/AI-Driven-Engineering-Platform/blob/main/deploy/railway/variables.md)). Config wins.
- Without a consumer, users can still connect with an Atlassian API token with Bitbucket scopes and their account email (Account → Connections → Bitbucket). App passwords aren't supported.
- Tevrin spends at most 900 Bitbucket requests an hour per connection (Bitbucket allows 1,000 per user), held in memory on the single API replica.
