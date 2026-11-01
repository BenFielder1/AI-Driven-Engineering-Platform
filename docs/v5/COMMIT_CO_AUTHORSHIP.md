# Commit co-authorship: crediting the person behind the AIDE bot

*Research note for v5 planning, 2026-10-28. Not a spec: nothing here is a requirement until it lands in `SPEC.md`.*

## Summary

**Yes, this can be done. Recommendation: keep the App bot as author and committer, and add a `Co-authored-by:` trailer
for the person who started the work. This is the model GitHub's own Copilot coding agent uses. It needs no new token,
permission or scope, and no change to FR-82's rule that background Git work runs on installation tokens.**

- **Today (FR-82):** every commit, merge and tag in a GitHub App project has `<slug>[bot]` and its noreply email as
  both author and committer (`GitHubAppGitProvider.PrepareWorkingCopyAsync` → `WorkingCopies.UseCommitIdentity`). The
  person who approved the plan doesn't appear on the commit at all.
- **The fix is one trailer line.** GitHub reads `Co-authored-by: Name <email>` at the end of a commit message. It shows
  that person's avatar next to the bot's on the commit, and counts the commit toward their contributions graph when
  the email belongs to their account.
- **AIDE already has the email.** `github_connections` stores the user's numeric `GithubUserId` and `Login`, which
  give their GitHub noreply address `<id>+<login>@users.noreply.github.com`. That address always belongs to the
  account, even when the user's email is private, so AIDE doesn't need the `user:email` scope.
- **Where it plugs in:** `CodeGeneration.CommitMessage` (`backend/src/Aide.Domain/Execution/CodeGeneration.cs`) already
  ends each step commit with the `AIDE-Step` trailer. The co-author trailer goes in the same trailer block. Merge and
  tag messages in `WorkingCopies` get the same treatment.

## How Claude Code and Copilot do it

| Tool | Author | Co-author | Why |
| --- | --- | --- | --- |
| **Claude Code** (CLI) | the human (their local `git config user.*`) | `Co-Authored-By: Claude <noreply@anthropic.com>` | It runs on the user's machine, in the user's git identity |
| **Copilot coding agent** | Copilot (bot) | the person who assigned the task (`Co-authored-by`), plus an `Agent-Logs-Url` trailer linking to the session | It runs server-side on GitHub's credentials, like AIDE |

AIDE is in Copilot's position: it runs server-side and has no git identity of the user's. Copilot's direction (bot
authors, human co-authors) is the one that fits. Claude Code's direction (human authors, bot co-authors) is option B
below.

## Options

### A. Bot authors, user as co-author (recommended)

Append the trailer to every commit AIDE makes:

```
[AIDE][Phase:execution][Step:3] Add the projects endpoint

Files:
- backend/src/Api/Projects.cs

AIDE-Step: 0192…
Co-authored-by: Ben Fielder <12345678+bencf@users.noreply.github.com>
```

- **On GitHub:** the commit shows both avatars ("aide-bot[bot] and bencf"). The commit appears on the user's
  contributions graph once it reaches the default branch, and the bot stays the visible actor.
- **Verification:** this changes nothing. The commit is no more or less "Verified" than it is today, and the user's
  vigilant mode isn't affected, because they aren't the author or committer.
- **Honesty:** the record stays accurate. An agent wrote the code, and a person directed and approved it. This matters
  for audit (AIDE's whole premise), and it's why GitHub chose this shape for Copilot.
- **Squash merges:** GitHub's default squash message keeps the `Co-authored-by` lines of the squashed commits, so the
  credit survives a squash merge in the GitHub UI.
- **GitLab and Bitbucket (v4):** the trailer is plain git, so it costs nothing to add there too. Neither host renders
  co-author avatars the way GitHub does, so it shows only as message text. Check each host's current behaviour before
  promising more.

### B. User as author, bot as committer

Set `GIT_AUTHOR_*` to the user (noreply email) and keep `GIT_COMMITTER_*` as the bot. GitHub shows
"bencf authored and aide-bot[bot] committed".

- The user gets primary credit, which is closer to Claude Code's look.
- **Risk: vigilant mode.** If the user has turned on vigilant mode, GitHub marks every unsigned commit with them as an
  author **Unverified**. AIDE can't sign as the user, so every AIDE commit would carry a warning badge on their account.
- It claims the person typed code that an agent wrote, which weakens the audit trail.
- Not recommended. It could be an opt-in per project later, if users ask for it.

### C. Act on behalf of the user (user-to-server token)

Push and open PRs with the user's GitHub App **user access token** (`ghu_…`) instead of an installation token. GitHub
attributes the activity to the user and shows the App's badge on their avatar ("via AIDE"). The GraphQL
`createCommitOnBranch` mutation makes commits authored by the token's owner and signed by GitHub, so they show as
**Verified**.

- This is the closest thing to a real "partnered" identity. It's also the most expensive option, and it reverses a
  decision:
  - **It amends FR-82**, which says background Git work uses installation tokens, "never a user token". Members today
    work through the App on the binding Owner's behalf (v2 decision log, 2026-10-16). A user token would tie every job
    to one person's token being valid.
  - User tokens expire after 8 hours and need refreshing (AIDE already does this for the picker, FR-81). A refresh
    failure marks the connection invalid, and execution would stop until the user reconnects.
  - Access is the intersection of the App's permissions and the user's own access, so it breaks when the user loses
    repo access or leaves the org.
  - `createCommitOnBranch` can't set a custom author, and it means replacing AIDE's local `git commit` + push flow
    (FR-34, FR-35) with API calls per commit. That's a rewrite of the working-copy path, not a tweak.
- Not recommended for commits. A smaller use might be worth it later: open the **pull request** with the user's token,
  so the PR shows the user (with the AIDE badge) as its opener.

## Recommended design (option A)

- **Who is credited:**
  - For step commits: the user who approved the gate that started execution (the plan approval).
  - For the release merge and tag: the user who approved the pre-release gate.
  - Fallback: the binding Owner (`ProjectSettings` records who bound the credential).
  - Optional: a project setting to also credit every Owner, capped at a few names.
- **Which email:** only `<GithubUserId>+<Login>@users.noreply.github.com` from `github_connections`. Never the user's
  AIDE sign-in email: it may not match their GitHub account, and putting it in a public repo's history would expose it.
  A user with no GitHub connection gets no trailer, and the commit still succeeds.
- **Domain rule:** a pure `CommitTrailers.CoAuthor(name, githubUserId, login)` in `Aide.Domain`. It strips newlines and
  `<>` from the name, so a display name can't inject extra trailers. It's unit-tested, per CLAUDE.md rule 3.
- **Idempotency:** the trailer is part of the message. FR-34's redelivery check matches on the `AIDE-Step` trailer, so a
  redelivered step finds its earlier commit whether or not that commit has the trailer.
- **Setting:** a per-project **Credit approvers on commits** toggle, on by default for GitHub App projects. Some teams
  won't want personal names in a client's repo history.
- **Also worth taking from Copilot:** an `AIDE-Run:` trailer with a link to the agent run in AIDE. It's the same idea
  as Copilot's `Agent-Logs-Url`, and it gives a reviewer a path from any commit back to the run, its prompt version
  and its gate decisions.
- **Spec impact:**
  - amend FR-34 (the commit message format)
  - amend FR-82's acceptance line ("the commit is attributed to the App bot" → "… with the approver as co-author")
  - add a requirement ID for the trailer rule and the setting
  - no API change beyond the setting, and no migration beyond the setting's column

## Open questions

1. Credit the plan approver only, or every user who approved a gate in the version?
2. Should the toggle default to on? Probably on for personal-account projects and off for organisation-owned repos.
3. Should GitLab and Bitbucket projects get the trailer in v5 as well, using each user's connection on that host to
   build the email? GitLab noreply addresses are `<id>-<username>@users.noreply.<host>`.

## Sources

- GitHub Docs: [Creating a commit with multiple authors](https://docs.github.com/en/pull-requests/committing-changes-to-your-project/creating-and-editing-commits/creating-a-commit-with-multiple-authors)
- GitHub Docs: [About commit signature verification](https://docs.github.com/en/authentication/managing-commit-signature-verification/about-commit-signature-verification) (bot signatures, vigilant mode)
- GitHub Docs: [Authenticating with a GitHub App on behalf of a user](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user)
- GitHub Docs: [GraphQL commits reference, `createCommitOnBranch`](https://docs.github.com/en/graphql/reference/commits)
- GitHub Docs: [About Copilot coding agent](https://docs.github.com/copilot/concepts/coding-agent/about-copilot-coding-agent)
- GitHub Changelog: [Trace any Copilot coding agent commit to its session logs (2026-03-20)](https://github.blog/changelog/2026-03-20-trace-any-copilot-coding-agent-commit-to-its-session-logs)
