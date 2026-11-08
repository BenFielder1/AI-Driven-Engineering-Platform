---
title: Notifications
parent: Guides
nav_order: 9
---

# Notifications

- **Email:** set `Smtp__Host` plus `Smtp__Username`, `Smtp__Password`, and optionally `Smtp__Port`, `Smtp__From`, `Smtp__EnableSsl`, `Smtp__Timeout` (at most 60 seconds). Without a host, email deliveries are marked skipped and a warning is logged.
- **Approval links:** `Approvals__SigningKey` must be at least 32 characters. `deploy-local.sh` generates it, and replaces a missing, `change-me` or short value. Changing it invalidates links already sent. Without it, notifications link to the project page only.
- **Links in messages:** `App__PublicBaseUrl` is set to `http://localhost:8080` for Kubernetes.
- **Slack, Teams, webhook:** configured per project (Settings, Notifications tab). Webhook URLs are stored encrypted. Targets must be public https hosts. For a receiver on your own machine, set `Notifications__AllowPrivateTargets=true`. Use it for local testing only.
- **Webhook receivers:** a webhook channel needs a signing secret. Each POST has these headers: `X-AIDE-Signature: sha256=<lowercase hex>` (HMAC-SHA256 of the raw body keyed with the secret), `X-AIDE-Timestamp`, `X-AIDE-Delivery`, `X-AIDE-Event` and `Idempotency-Key`. Recompute the HMAC over the raw bytes, compare in constant time, and reject old timestamps.
- **0.2.0 types:** release awaiting merge and deployment failed (Owners), preview ready (members), connection invalid (the connection's owner only, in-app and account email), LLM binding cleared (Owners). Channels opt in to the new events.
- **0.4.0 types (Owners):** Git binding cleared, database provisioning failed, database expiring (7 days before a Render free database expires, or when Supabase pauses a project), migration failed, deployment target needs attention. A channel subscribed to `deployment_failed` also receives `migration_failed`.
- **Retries:** 5 attempts per channel with 1 min, 5 min, 30 min, 2 h backoff. A channel that succeeded is not re-sent.
