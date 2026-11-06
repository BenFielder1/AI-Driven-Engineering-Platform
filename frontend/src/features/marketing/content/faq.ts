import { docsUrl, GITHUB_URL, type NavLink } from './site-nav'

/**
 * FR-218, FR-212, FR-222: the FAQ. Every answer is true of 0.6.0 and comes from the specs (docs/0.1.0–0.5.0/SPEC.md, SPEC.md)
 * and the docs site. The licence wording follows SPEC D-V6-21 (PolyForm Shield; "source available", never "open source"). "Was it called AIDE?" is
 * the one marketing answer that names the old product (with the Changelog's "(as AIDE)").
 */
export interface FaqItem {
  id: string
  question: string
  /** Paragraphs. */
  answer: readonly string[]
  link?: NavLink
}

export const faqs: readonly FaqItem[] = [
  {
    id: 'what-is-tevrin',
    question: 'What is Tevrin?',
    answer: [
      'Tevrin coordinates specialised AI agents through a governed lifecycle (Refinement, Specification, Planning, Execution, Review and Release) to turn an idea into released software.',
      'Each phase produces a versioned artifact, people approve the work at gates, and every state change is recorded and can be rolled back.',
    ],
    link: { label: 'How it works', href: '/how-it-works' },
  },
  {
    id: 'who-is-it-for',
    question: 'Who is it for?',
    answer: [
      'Engineers and project owners who want AI-driven delivery with deterministic state, an audit trail and human oversight.',
      'Projects have Owners, who approve gates and change settings, Contributors, who edit drafts and run the work, and Viewers.',
    ],
  },
  {
    id: 'approval-gates',
    question: 'How do approval gates work?',
    answer: [
      'You approve each phase’s artifact before the project moves on. Gates add a recorded decision at the big steps: spec approval before Planning, plan approval before Execution, a pre-release gate before Release, and an escalation gate when a review can’t settle.',
      'The project’s approval mode sets which gates apply: strict gates both the spec and the plan, advisory gates the spec, and autonomous gates neither. The pre-release and escalation gates apply in every mode. Gate decisions are final.',
    ],
    link: { label: 'Approval modes', href: '/how-it-works#approval-modes' },
  },
  {
    id: 'undo',
    question: 'Can I undo what an agent did?',
    answer: [
      'Yes. Roll the project back to any earlier checkpoint of the version in development. Locked artifacts are never rewritten: rollback and invalidation add history instead of deleting it.',
      'Drafts can be rejected or edited before you approve them, and a released version stays closed: to change it, start a new version.',
    ],
    link: { label: 'Rollback and invalidation', href: '/how-it-works#rollback' },
  },
  {
    id: 'models-and-keys',
    question: 'Which AI models can I use, and where do my keys live?',
    answer: [
      'Anthropic, OpenAI and local models through Ollama. You choose the provider and model when you create a project, and can override the model per agent.',
      'Keys are your own connections (Account, Connections) or a system key an admin allows. Tevrin checks a key when you add it, stores it encrypted and never shows it again. Ollama needs no key, but the Railway deployment turns it off.',
    ],
    link: { label: 'LLM providers guide', href: docsUrl('guides/llm-providers/'), external: true },
  },
  {
    id: 'code-location',
    question: 'Does my code leave my infrastructure?',
    answer: [
      'Tevrin runs where you host it and keeps its data in your PostgreSQL database. It works on a copy of your repository on the Tevrin server and pushes to the Git host you connect.',
      'Prompts, including artifacts and the repository excerpts agents read, go to the LLM provider you choose. With Ollama they stay on your own machines. Deployments build on the platforms you connect.',
    ],
  },
  {
    id: 'existing-repo',
    question: 'Does it work with an existing repository?',
    answer: [
      'Yes. Import a repository from GitHub, GitLab or Bitbucket. A Repository Analyst summarises the codebase for the other agents, and the optional Analysis phase can infer its stack.',
      'After a release, start the next version and go round the lifecycle again on top of the existing code.',
    ],
  },
  {
    id: 'supported',
    question: 'Which Git hosts, trackers and platforms are supported?',
    answer: [
      'Git hosts: GitHub, GitLab (gitlab.com and self-managed instances an admin allows) and Bitbucket Cloud. Issue trackers: Jira, Linear, GitHub Issues and GitLab Issues.',
      'Deployment platforms: Vercel, Netlify, Cloudflare, GitHub Pages, Render and Railway. Databases: Supabase, Render Postgres and Railway Postgres.',
    ],
    link: { label: 'All integrations', href: '/integrations' },
  },
  {
    id: 'free',
    question: 'Is it free?',
    answer: [
      'Yes. Tevrin is free while in preview, and there is no billing. You run it yourself; the platforms and LLM providers you connect bill you under your own accounts.',
      'The source is available on GitHub under the PolyForm Shield License 1.0.0. You can use, modify and self-host it, including at work, but not sell it or offer it as a competing hosted service.',
    ],
    link: { label: 'Tevrin on GitHub', href: GITHUB_URL, external: true },
  },
  {
    id: 'self-host',
    question: 'How do I self-host it?',
    answer: [
      'Run it on Railway, on Docker Desktop Kubernetes, or as a local dev loop. You need Docker, PostgreSQL 18, and an LLM provider key or Ollama. The Railway deployment turns Ollama off, so it needs a provider key.',
    ],
    link: { label: 'Self-host', href: '/self-host' },
  },
  {
    id: 'cli-and-api',
    question: 'Is there a CLI or API?',
    answer: [
      'Yes. The tevrin command-line tool, a .NET global tool, uses a personal API token from your Account page to list and create projects, approve artifacts, run execution and review, release and roll back.',
      'The web app talks to an HTTP API described by an OpenAPI contract in the repository.',
    ],
    link: { label: 'CLI guide', href: docsUrl('guides/cli/'), external: true },
  },
  {
    id: 'aide',
    question: 'Was it called AIDE?',
    answer: [
      'Yes. Versions 0.1.0 to 0.5.0 shipped as AIDE (AI-Driven Engineering). Version 0.6.0 renamed it Tevrin.',
      'Nothing you hold breaks: existing sessions, API tokens and webhook signatures keep working, and the old aide command still runs as a deprecated alias of tevrin.',
    ],
    link: { label: 'Changelog', href: '/changelog' },
  },
]

/** FR-212: Home shows the first five. */
export const homeFaqs = faqs.slice(0, 5)

/** FR-218: "Still have questions?" */
export const faqHelpLinks: readonly NavLink[] = [
  { label: 'Read the docs', href: docsUrl(), external: true },
  { label: 'Ask on GitHub issues', href: `${GITHUB_URL}/issues`, external: true },
]
