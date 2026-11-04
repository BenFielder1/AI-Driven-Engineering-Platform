import { docsUrl } from './site-nav'

/**
 * FR-216, FR-222: the canonical self-host commands, one source for the Self-host page and the Home self-host section.
 * They must match the docs site exactly: self-host-commands.test.ts compares every line with the code blocks in
 * `docs/` (`source` names the file and the heading the block sits under), in both directions.
 */
export interface Command {
  /** The command exactly as typed. */
  command: string
  /** The trailing `# …` comment in the docs block, without the `#`. */
  note: string
}

export interface SelfHostPath {
  id: 'railway' | 'kubernetes' | 'dev-loop'
  title: string
  /** The docs page these commands come from (repo-relative) and the heading of the block. */
  source: { file: string; heading: string }
  docsHref: string
  commands: readonly Command[]
}

export const selfHostPaths: readonly SelfHostPath[] = [
  {
    id: 'railway',
    title: 'Railway',
    source: { file: 'docs/operations/railway.md', heading: 'How deploys happen' },
    docsHref: docsUrl('operations/railway/'),
    commands: [
      { command: 'railway config plan', note: 'read-only diff of Railway vs .railway/railway.ts' },
      { command: 'bash deploy/scripts/smoke-remote.sh <web-url>', note: 'smoke test a deployed environment' },
    ],
  },
  {
    id: 'kubernetes',
    title: 'Docker Desktop Kubernetes',
    source: { file: 'docs/operations/local-kubernetes.md', heading: '' },
    docsHref: docsUrl('operations/local-kubernetes/'),
    commands: [
      {
        command: './deploy/scripts/deploy-local.sh',
        note: 'build images, apply, migrate, roll out, smoke test → http://localhost:8080',
      },
      { command: './deploy/scripts/teardown-local.sh', note: 'remove app (keeps DB data unless --wipe-data)' },
    ],
  },
  {
    id: 'dev-loop',
    title: 'Dev loop',
    source: { file: 'docs/getting-started/dev-loop.md', heading: 'Each session' },
    docsHref: docsUrl('getting-started/dev-loop/'),
    commands: [
      { command: 'docker compose up -d db', note: 'dev database (PostgreSQL 18, values from .env)' },
      { command: 'dotnet run --project backend/src/Aide.Api', note: 'http://localhost:5080' },
      {
        command: 'npm --prefix frontend run dev',
        note: 'http://localhost:3000 (proxies /api + WebSockets → BACKEND_URL, default 5080)',
      },
    ],
  },
]

/** Splits a docs command line into the command and its trailing comment (`cmd   # note`). */
export function parseCommandLine(line: string): Command {
  const match = /^(.*?)\s+#\s(.*)$/.exec(line)
  return match?.[1] !== undefined && match[2] !== undefined
    ? { command: match[1].trim(), note: match[2].trim() }
    : { command: line.trim(), note: '' }
}
