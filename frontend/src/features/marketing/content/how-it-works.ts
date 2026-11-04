/**
 * FR-214, FR-212, FR-222: the walk-through from idea to release. Sources: v1 SPEC §4.1–§4.5 (phases, gates by approval
 * mode, artifacts, invalidation, review decision), v2 §3.2 (release versions and checkpoints), v4 (specialists),
 * v5 §3.4 (Analysis), docs/guides. Approval modes are the domain's `ApprovalMode` (strict, advisory, autonomous).
 */
export interface PhaseDetail {
  id: string
  name: string
  optional?: boolean
  input: string
  agent: string
  artifact: string
  /** What has to happen before the next phase. */
  gate: string
}

export const phaseDetails: readonly PhaseDetail[] = [
  {
    id: 'analysis',
    name: 'Analysis',
    optional: true,
    input: 'An existing repository, when you choose to infer its stack.',
    agent: 'Stack Analyst',
    artifact: 'Stack profile: the layers, hosts and structure it found, with blanks for you to fill.',
    gate: 'An Owner edits and approves the profile, which pins the stack.',
  },
  {
    id: 'refinement',
    name: 'Refinement',
    input: 'Your idea, plus any imported tracker items and, for an existing repo, a codebase summary.',
    agent: 'Idea Refinement',
    artifact: 'Project request',
    gate: 'You approve the project request.',
  },
  {
    id: 'specification',
    name: 'Specification',
    input: 'The approved project request.',
    agent: 'Spec Generator, with the stack’s specialist knowledge',
    artifact: 'Technical specification',
    gate: 'Spec approval gate (strict and advisory modes).',
  },
  {
    id: 'planning',
    name: 'Planning',
    input: 'The locked specification.',
    agent: 'Plan Generator',
    artifact: 'Implementation plan: ordered steps, the files each touches and their dependencies.',
    gate: 'Plan approval gate (strict mode).',
  },
  {
    id: 'execution',
    name: 'Execution',
    input: 'The locked plan and a working copy of the repository.',
    agent: 'Code Generator, as the specialist for each step’s layer',
    artifact: 'Code, one step at a time, committed to the version’s branch and pull request.',
    gate: 'Every step passes the Execution Guardian: one step at a time, within the plan and file limits, no secrets.',
  },
  {
    id: 'review',
    name: 'Review',
    input: 'The code, your Git host’s checks and, when deployments are set up, the preview.',
    agent: 'Code Review',
    artifact: 'Review report with a compliance score and drift, and an optimisation plan when another cycle is needed.',
    gate: 'Pre-release gate when the score clears the threshold, in every mode.',
  },
  {
    id: 'release',
    name: 'Release',
    input: 'The approved pre-release gate.',
    agent: 'None: Tevrin does the release itself',
    artifact: 'Merged pull request, a version tag, a release on your Git host and, when set up, the production deployment.',
    gate: 'Released. Start the next version when you’re ready.',
  },
]

/** The label on the gate between two phases on Home's numbered steps (FR-212). Index i sits after phase i. */
export const phaseGates: readonly string[] = [
  'Approve the project request',
  'Spec approval',
  'Plan approval',
  'Guardian checks on every step',
  'Pre-release approval',
]

export interface ApprovalMode {
  id: 'strict' | 'advisory' | 'autonomous'
  name: string
  summary: string
  /** Gates this mode adds before Planning and Execution. */
  spec: boolean
  plan: boolean
}

export const approvalModes: readonly ApprovalMode[] = [
  { id: 'strict', name: 'Strict', summary: 'A person approves both the specification and the plan before work moves on.', spec: true, plan: true },
  { id: 'advisory', name: 'Advisory', summary: 'The default. The specification needs a gate; an approved plan goes straight to execution.', spec: true, plan: false },
  { id: 'autonomous', name: 'Autonomous', summary: 'Approved artifacts move on without extra gates.', spec: false, plan: false },
]

export const approvalModesNote =
  'In every mode, the release waits for the pre-release gate and problems raise an escalation gate. Gate decisions are final and recorded.'

/** Review cycles (v1 §4.5), in order. */
export const reviewRules: readonly string[] = [
  'A score below 0.75, or severe drift from the spec, escalates to a person.',
  'A score at or above the project’s threshold (0.90 by default) makes the version release-eligible.',
  'Otherwise the review writes an optimisation plan, and execution runs another cycle.',
  'Reaching the cycle limit (3 by default), or a cycle that doesn’t improve the score, escalates.',
  'An escalation gate is decided with continue, roll back or accept.',
]

export const versionsCopy: readonly string[] = [
  'Each release has a semver version, such as 1.2.0. Inside it, the project moves through numbered checkpoints: every forward phase change creates one.',
  'After a release, start the next version with a new request and go round the lifecycle again on the existing code. A released version is closed.',
]

export const rollbackCopy: readonly string[] = [
  'Roll back to any earlier checkpoint of the version in development. The project returns to that phase, and later artifacts are marked invalidated, never deleted.',
  'Approving a new version of an earlier artifact invalidates what was built from it. Approve a new spec, and the plan and its steps become invalid and the project falls back to Planning.',
  'Deployments can be rolled back per platform where the platform supports it.',
]
