Rules for GitHub Actions CI, repository hygiene files, Vercel configuration and environment variables in a Next.js
frontend. AIDE creates branches, commits, pull requests and deployments itself; files you write must never push,
deploy or link projects.

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
          if git grep -lE "^['\"]use client['\"]" -- '*.ts' '*.tsx' | xargs -r grep -nP "process\.env\.(?!NEXT_PUBLIC_|NODE_ENV\b)"; then
            echo "Server-only env var referenced in a client file"; exit 1
          fi
          if git grep -nE "(sk_live_|-----BEGIN [A-Z ]*PRIVATE KEY-----)"; then
            echo "Possible secret committed"; exit 1
          fi
      - run: npm run build
  e2e:
    runs-on: ubuntu-latest
    needs: checks
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
      - name: Install
        run: if [ -f package-lock.json ]; then npm ci; else npm install; fi
      - run: npx playwright install --with-deps chromium
      - run: npm run test:e2e
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-report
          path: playwright-report
```
- Use the current major version of each action (update `@v4` if newer majors exist) and say which in the notes.
- Add `env:` placeholders on the build and e2e steps for any variable the build needs (upstream base URLs pointing at
  the mock in e2e). Never real keys.
- Add `cache: npm` to setup-node only when a lockfile is committed (it fails without one).
- Keep `checks` under about 5 minutes.

### Repository hygiene
- `.github/pull_request_template.md`: Summary, Requirements (IDs), Testing, Screenshots.
- `.github/dependabot.yml`: `npm` and `github-actions`, weekly.
- `.gitignore`: `.env*` with `!.env.example`, `.env*.local`, `.vercel`, `playwright-report`, `test-results`, `coverage`.

### Vercel
- `vercel.json` always keeps automatic deploys of AIDE branches off (AIDE deploys them explicitly; production deploys
  of the default branch stay on):
  ```json
  { "$schema": "https://openapi.vercel.sh/vercel.json", "framework": "nextjs", "git": { "deploymentEnabled": { "aide/**": false } } }
  ```
- `headers`, `rewrites` or `crons` only if the specification needs them; a cron hits a route that checks
  `Authorization: Bearer <CRON_SECRET>`.
- Env vars are set in the Vercel project (AIDE syncs them). `.env.example` lists every one with a comment for source,
  scope and environments. Upstream API keys are server-only and never get the `NEXT_PUBLIC_` prefix.

### Deploy-readiness checklist
- [ ] CI covers typecheck, lint, test, build, a secret scan and e2e.
- [ ] `.env.example` is complete, with sources and scopes.
- [ ] No secrets in the repository; no server-only env var in client code.
- [ ] `vercel.json` disables `aide/**` deploys.
