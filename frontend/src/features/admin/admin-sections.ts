/** The four admin sections (SPEC §7), in nav order. */
export const adminSections = [
  {
    to: '/admin/events',
    label: 'Events',
    description: 'Every project’s audit events in one log. Filter by project, type, category, outcome and date.',
  },
  {
    to: '/admin/runs',
    label: 'Agent runs',
    description: 'Every agent run across projects, with model, tokens, cost, duration and status.',
  },
  {
    to: '/admin/dead-letters',
    label: 'Dead letters',
    description: 'Background jobs that failed every attempt, with their last error. Retry re-queues the job.',
  },
  {
    to: '/admin/users',
    label: 'Users',
    description: 'Everyone with an account, and their system role. Admins can make other users Admins.',
  },
] as const
