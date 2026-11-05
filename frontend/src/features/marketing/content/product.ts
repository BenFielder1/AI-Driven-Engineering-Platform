/**
 * FR-213, FR-212, FR-222: the Product page, one anchored section per feature area. Sources: the v1–v5 specs and the
 * docs guides (lifecycle §4.1–§4.5, artifacts §4.3, agents FR-23/FR-70/FR-180, Git hosts v2 §3.7 and v4 §3.5–§3.7,
 * trackers v5 §3.2, stacks v4 §3.1 and v5 §3.4, deployments v4 §3.8–§3.10 and v5 §3.5, notifications FR-47–FR-49,
 * admin FR-51, CLI FR-52 and docs/guides/cli.md).
 */
export type ProductPreview = 'gates' | 'artifacts' | 'runs' | 'deployments'

export type ProductIcon = 'git' | 'tracker' | 'layers' | 'bell' | 'shield' | 'terminal'

export interface ProductSection {
  id: string
  title: string
  body: readonly string[]
  bullets: readonly string[]
  /** A preview (FR-220) or, where none fits, an icon. */
  visual: { preview: ProductPreview } | { icon: ProductIcon }
}

export const productSections: readonly ProductSection[] = [
  {
    id: 'lifecycle-and-gates',
    title: 'Lifecycle and gates',
    body: [
      'Every project moves through Refinement, Specification, Planning, Execution, Review and Release, with an optional Analysis phase first.',
      'A deterministic state machine decides what can happen next, and gates stop the work until a person decides.',
    ],
    bullets: [
      'Strict, advisory and autonomous approval modes',
      'Spec approval, plan approval, pre-release and escalation gates',
      'Signed, single-use approval links in notifications',
      'Every transition recorded with who asked and why',
    ],
    visual: { preview: 'gates' },
  },
  {
    id: 'artifacts',
    title: 'Artifacts and versioning',
    body: [
      'Each phase writes a Markdown artifact: project request, technical specification, implementation plan and, after a review, an optimisation plan.',
      'Approving one locks it with a content hash. Changes are new versions, so you can always see what changed and why.',
    ],
    bullets: [
      'Side-by-side diffs between any two versions',
      'Edit drafts yourself, or restore an earlier version',
      'SHA-256 hashes with integrity alerts',
      'Invalidation cascades when an upstream artifact changes',
    ],
    visual: { preview: 'artifacts' },
  },
  {
    id: 'agents',
    title: 'Agents, prompts and LLM providers',
    body: [
      'Specialised agents do the work: idea refinement, specification, planning, code generation and review, plus analysts that read existing repositories.',
      'Prompts are versioned templates, every run records its prompt version, model and tokens, and all calls go through one provider abstraction.',
    ],
    bullets: [
      'Anthropic, OpenAI and Ollama, chosen per project',
      'Model per agent, and your own keys',
      'Specialists that load the skill for their stack layer',
      'Read-only repository tools with secret redaction',
      'Token budgets per project',
    ],
    visual: { preview: 'runs' },
  },
  {
    id: 'git',
    title: 'Git hosts, pull requests, review and merge',
    body: [
      'Tevrin works on a branch per version and keeps one pull request open for it, committing each step as it lands.',
      'Review reads your Git host’s checks, and the release merges, tags and publishes a release.',
    ],
    bullets: [
      'GitHub, GitLab and Bitbucket Cloud',
      'GitHub App with short-lived installation tokens',
      'Merge strategies and merge-readiness checks',
      'Co-authored-by credit for the accountable person',
      'Import an existing repository',
    ],
    visual: { icon: 'git' },
  },
  {
    id: 'issue-trackers',
    title: 'Issue-tracker imports',
    body: [
      'Start from real work: import items from your tracker as a read-only snapshot that the agents read with your idea.',
      'Re-import when the tracker changes, and see when a snapshot has drifted.',
    ],
    bullets: ['Jira, Linear, GitHub Issues and GitLab Issues', 'Import by key or by query, with comments', 'For a new project or a new version', 'Tevrin never writes to your tracker'],
    visual: { icon: 'tracker' },
  },
  {
    id: 'stacks',
    title: 'Stack composition and inference',
    body: [
      'Pick one option per layer (frontend, backend, data, Git host and deployment) from a versioned catalog. Options that don’t fit are filtered out with the reason.',
      'For an existing repository, the Stack Analyst can infer the stack for you to correct and approve.',
    ],
    bullets: [
      'Compatibility rules enforced by the backend',
      'Detection when you import a repository',
      'An optional Analysis phase with an editable stack profile',
      'Hosts chosen per component',
    ],
    visual: { icon: 'layers' },
  },
  {
    id: 'deployments',
    title: 'Deployments and managed databases',
    body: [
      'Review waits for a preview rollout, and a release triggers the production rollout: migrations, then backend, then frontend.',
      'Tevrin creates or links platform projects and databases, and writes the variables that connect them.',
    ],
    bullets: [
      'Vercel, Netlify, Cloudflare, GitHub Pages, Render and Railway',
      'Supabase, Render Postgres and Railway Postgres per environment',
      'Shared platform projects for parts on one platform',
      'Separate preview and production databases',
      'Rollback where the platform supports it',
    ],
    visual: { preview: 'deployments' },
  },
  {
    id: 'notifications',
    title: 'Notifications and live updates',
    body: [
      'Project pages update live as agents run and gates are decided, with no refresh.',
      'Notifications reach the people who need to act, in the app and on the channels each project sets up.',
    ],
    bullets: ['In-app notifications, email, Slack, Microsoft Teams and signed webhooks', 'Retries with backoff per channel', 'Mute notification types you don’t need'],
    visual: { icon: 'bell' },
  },
  {
    id: 'admin',
    title: 'Admin and audit',
    body: [
      'Every state change is an append-only event. Projects have Owners, Contributors and Viewers.',
      'Admins see the event log, agent runs and failed jobs across projects, and manage users and integrations.',
    ],
    bullets: ['Per-project activity feed', 'Cross-project event and run logs', 'Dead letters with retry', 'Git host, sign-in and provider settings in one place'],
    visual: { icon: 'shield' },
  },
  {
    id: 'cli-and-api',
    title: 'CLI and API',
    body: [
      'The tevrin command-line tool covers the lifecycle from a terminal, with personal API tokens.',
      'The web app uses the same HTTP API, described by a generated OpenAPI contract.',
    ],
    bullets: [
      'Create projects, approve artifacts, execute, review, release and roll back',
      'Start versions, set stacks and import tracker items',
      'JSON output from status for scripts',
    ],
    visual: { icon: 'terminal' },
  },
]

export type ProductSectionId = (typeof productSections)[number]['id']
