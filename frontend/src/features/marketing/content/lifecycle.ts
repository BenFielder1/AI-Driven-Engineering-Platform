/** FR-212, FR-214, FR-223: the six lifecycle phases in order (an approval gate sits between each pair). */
export const lifecyclePhases = ['Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release'] as const

/** The text equivalent of a lifecycle diagram (for `aria-hidden` drawings). */
export const lifecycleSummary = `The Tevrin lifecycle: ${lifecyclePhases.join(', ')}, with an approval gate between each phase.`
