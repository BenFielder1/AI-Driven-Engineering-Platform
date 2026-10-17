Rules for GitHub Actions CI, repository hygiene files, Vercel configuration and environment variables in a Next.js +
Supabase project. AIDE creates branches, commits, pull requests and deployments itself; files you write must never
push, deploy, link projects or touch a remote database.

### CI: `.github/workflows/ci.yml`
```yaml
name: CI
on:
  pull_request:
  push:
    branches: [main]
concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true
jobs:
  checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
      - name: Install
        run: if [ -f package-lock.json ]; then npm ci; else npm install; fi
      - run: npm run typecheck
      - run: npm run lint
      - run: npm test
      - name: Secret scan
        run: |
          if git grep -nE "(service_role|SUPABASE_SERVICE_ROLE_KEY|sk_live_)" -- 'app' 'components' 'lib' ':!lib/api/**' ':!**/*.test.*'; then
            echo "Server secret referenced in app code"; exit 1
          fi
      - run: npm run build
        env:
          NEXT_PUBLIC_SUPABASE_URL: http://127.0.0.1:54321
          NEXT_PUBLIC_SUPABASE_ANON_KEY: ci-placeholder
  e2e:
    runs-on: ubuntu-latest
    needs: checks
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
      - uses: supabase/setup-cli@v1
        with:
          version: latest
      - name: Install
        run: if [ -f package-lock.json ]; then npm ci; else npm install; fi
      - run: supabase start
      - name: Export local Supabase env
        run: |
          supabase status -o env | sed -n 's/^API_URL=/NEXT_PUBLIC_SUPABASE_URL=/p; s/^ANON_KEY=/NEXT_PUBLIC_SUPABASE_ANON_KEY=/p' | tr -d '"' >> "$GITHUB_ENV"
      - run: npx playwright install --with-deps chromium
      - run: npm run test:e2e
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-report
          path: playwright-report
```
- Use the current major version of each action (update `@v4`/`@v1` if newer majors exist) and say which in the notes.
- Adjust the secret-scan exclusions to the server-only files the specification allows (webhook or cron handlers).
- Add `cache: npm` to setup-node only when a lockfile is committed (it fails without one).
- Keep `checks` under about 5 minutes.

### Repository hygiene
- `.github/pull_request_template.md`: Summary, Requirements (IDs), Testing, Screenshots.
- `.github/dependabot.yml`: `npm` and `github-actions`, weekly.
- `.gitignore`: `.env*` with `!.env.example`, `.env*.local`, `.vercel`, `supabase/.temp`, `playwright-report`,
  `test-results`, `coverage`.

### Vercel
- `vercel.json` always keeps automatic deploys of AIDE branches off (AIDE deploys them explicitly; production deploys
  of the default branch stay on):
  ```json
  { "$schema": "https://openapi.vercel.sh/vercel.json", "framework": "nextjs", "git": { "deploymentEnabled": { "aide/**": false } } }
  ```
- Cron (only if the specification needs it): `"crons": [{ "path": "/api/cron/<job>", "schedule": "0 3 * * *" }]`,
  hitting a route that checks `Authorization: Bearer <CRON_SECRET>`.
- Env vars are set in the Vercel project (AIDE syncs them). `.env.example` lists every one with a comment for source,
  scope and environments. `NEXT_PUBLIC_SUPABASE_URL` and `NEXT_PUBLIC_SUPABASE_ANON_KEY` are public; server-only
  secrets never get the `NEXT_PUBLIC_` prefix.
- Supabase Auth redirect URLs must include the production URL and the preview pattern `https://*-<team>.vercel.app/**`
  (a manual step to list in the notes).

### Supabase migrations to remote (documented, not automated)
- Option A: the Supabase GitHub integration applies `supabase/migrations` on merge to the production branch.
- Option B: an Actions job on push to `main` running `supabase link` and `supabase db push` with secrets
  `SUPABASE_ACCESS_TOKEN`, `SUPABASE_DB_PASSWORD` and `SUPABASE_PROJECT_REF`. Only if the specification asks for it.
- Never push `seed.sql` to production.

### Deploy-readiness checklist
- [ ] CI covers typecheck, lint, test, build, a secret scan and e2e.
- [ ] `.env.example` is complete, with sources and scopes.
- [ ] No secrets in the repository; no service-role key in app code.
- [ ] `vercel.json` disables `aide/**` deploys.
