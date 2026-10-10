import type { ArtifactAuthor } from './artifacts-api'

/** FR-16: who produced a version: a user, or an agent run. */
export function authorName(author: ArtifactAuthor): string {
  if (author.user) {
    return author.user.name
  }
  return author.agentRunId ? 'Agent' : 'Unknown'
}

/** Whether a version came from an agent run (rejecting it re-runs the agent, FR-17). */
export const isAgentAuthored = (author: ArtifactAuthor) => author.user === null && author.agentRunId !== null
