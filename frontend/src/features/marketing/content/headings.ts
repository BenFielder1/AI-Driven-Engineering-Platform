/**
 * FR-222: page h1s and section headings (eyebrow, title, lead) for the marketing pages, so the copy lives in content
 * modules rather than in the components. The h1s are pinned by marketing-routes.test.tsx and e2e/a11y.spec.ts.
 */
export interface SectionHeading {
  eyebrow?: string
  title: string
  lead?: string
}

export const pageTitles = {
  product: 'Everything Tevrin does, from idea to release',
  howItWorks: 'How Tevrin takes an idea to a release',
  integrations: 'Works with the tools you already use',
  selfHost: 'Run Tevrin on your own infrastructure',
  security: 'Security at Tevrin',
  faq: 'Frequently asked questions',
  changelog: 'Changelog',
} as const

export const homeHeadings = {
  worksWith: { title: 'Works with' },
  lifecycle: {
    eyebrow: 'How it works',
    title: 'Six phases, with a gate between each',
    lead: 'Each phase has its own agent and artifact. Work moves on only when its gate is cleared.',
  },
  analysisNote: 'Optional first step: Analysis, which infers the stack of an existing repository.',
  lifecycleLink: 'Walk through every phase',
  why: { eyebrow: 'Why Tevrin', title: 'AI does the work. You stay in control.' },
  bento: { eyebrow: 'Features', title: 'Everything between the idea and the release' },
  selfHost: {
    eyebrow: 'Self-host',
    title: 'Run it yourself',
    lead: 'Tevrin is free while in preview. Deploy it to Railway, run it on Docker Desktop Kubernetes, or start the dev loop.',
  },
  selfHostNeed: 'You need',
  selfHostLink: 'All three ways to self-host',
  changelog: { eyebrow: 'Changelog', title: 'Latest changes' },
  changelogLink: 'Full changelog',
  faq: { eyebrow: 'FAQ', title: 'Questions, answered' },
  faqLink: 'All questions',
} as const satisfies Record<string, SectionHeading | string>

export const howItWorksHeadings = {
  phases: {
    eyebrow: 'The lifecycle',
    title: 'Phase by phase',
    lead: 'Every phase has an input, an agent, an artifact you can read and edit, and a gate before the next one.',
  },
  approvalModes: { eyebrow: 'Gates', title: 'Approval modes' },
  modesCaption: 'Which gates each approval mode adds',
  reviewCycles: { eyebrow: 'Review', title: 'Review cycles' },
  versions: { eyebrow: 'Release', title: 'Release versions and checkpoints' },
  rollback: { eyebrow: 'Undo', title: 'Rollback and invalidation' },
} as const satisfies Record<string, SectionHeading | string>

export const selfHostHeadings = {
  requirements: 'Requirements',
  install: { eyebrow: 'Install', title: 'Three ways to run it' },
  configure: { eyebrow: 'Configure', title: 'What you set up' },
  faq: { eyebrow: 'FAQ', title: 'Upgrades and data' },
} as const satisfies Record<string, SectionHeading | string>

export const faqHeadings = {
  more: {
    title: 'Still have questions?',
    lead: 'The docs cover setup and every feature in detail. For anything else, open an issue.',
  },
} as const satisfies Record<string, SectionHeading>

export const changelogNote = 'Versions 1 to 5 shipped under the name AIDE.'

/** The marketing 404 (SPEC §7). */
export const notFoundCopy = {
  eyebrow: '404',
  title: 'Page not found',
  lead: 'The page you’re looking for doesn’t exist.',
  home: 'Go to the home page',
  docs: 'Read the docs',
} as const

/** Copy-button feedback (FR-216), announced in a polite live region. */
export const copyFeedback = {
  copied: 'Copied',
  failed: 'Couldn’t copy, select the text instead',
} as const
