import { GitHostErrorAlert, GitHostLink } from '../git-hosts/git-host-link'

/** A link that leaves AIDE for a GitLab instance (new tab, announced as such). */
export function GitLabLink(props: { href: string; children: string; variant?: 'secondary' | 'link' }) {
  return <GitHostLink site="GitLab" {...props} />
}

/** A failed GitLab picker call (FR-132) with the way out: GIT_NOT_CONNECTED links to Connections. */
export const GitLabErrorAlert = GitHostErrorAlert
