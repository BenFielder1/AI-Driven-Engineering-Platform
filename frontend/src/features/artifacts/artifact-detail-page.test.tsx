import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../lib/errors'
import { seedArtifact, seedArtifactHistory } from '../../mocks/handlers/artifacts'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { openArtifact, seedProjectAs, specV1, specV2, specV3 } from '../../test/artifacts'
import { projectEvent } from '../../test/fake-live'
import { requestsTo } from '../../test/project-settings'

const SPEC = 'technical_specification'

const content = (name: string) => screen.findByRole('article', { name })

/** The value of a version detail (`<dt>term</dt><dd>…</dd>`). */
function detail(term: string) {
  const dt = screen.getAllByText(term).find((el) => el.tagName === 'DT')
  if (!dt?.nextElementSibling) {
    throw new Error(`No detail "${term}"`)
  }
  return dt.nextElementSibling
}

describe('Artifact detail (FR-16–FR-21)', () => {
  it('FR-21: renders the latest version as Markdown, without raw HTML, with its details and history', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [
        specV1,
        `${specV2}\n\n<script>window.hacked = true</script>\n<img src="x" onerror="window.hacked = true">\n\n[bad](javascript:alert(1))`,
      ])
    })
    await openArtifact(project, SPEC)

    const article = await content('Technical specification v2')
    // `#` renders below the page's h2.
    expect(within(article).getByRole('heading', { level: 3, name: 'Inventory tracker spec' })).toBeInTheDocument()
    expect(within(article).getByRole('heading', { level: 4, name: 'Scope' })).toBeInTheDocument()
    expect(article.querySelector('script, img, [onerror]')).toBeNull()
    expect(within(article).queryByRole('link', { name: 'bad' })?.getAttribute('href') ?? '').not.toMatch(/javascript/i)

    expect(screen.getByRole('heading', { level: 2, name: 'Technical specification' })).toBeInTheDocument()
    expect(detail('Parent version')).toHaveTextContent('v1')

    const history = screen.getByRole('table', { name: 'Version history, newest first' })
    const [, newestRow, oldestRow, ...rest] = within(history).getAllByRole('row')
    if (!newestRow || !oldestRow) {
      throw new Error('missing history rows')
    }
    expect(rest).toHaveLength(0)
    expect(within(newestRow).getByRole('rowheader')).toHaveTextContent('v2(shown)')
    expect(within(newestRow).getByText('Draft')).toBeInTheDocument()
    expect(within(oldestRow).getByRole('rowheader')).toHaveTextContent('v1')
    expect(within(oldestRow).getByText('Approved')).toBeInTheDocument()

    // Viewers see no mutations.
    expect(screen.queryByRole('button', { name: 'Edit as new draft' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
  })

  it('FR-21: the version picker shows an older version', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2, specV3])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v3')

    await user.selectOptions(screen.getByLabelText('Version'), '1')

    const article = await content('Technical specification v1')
    expect(within(article).getByText('Track stock levels.')).toBeInTheDocument()
    expect(screen.getByLabelText('Version')).toHaveValue('1')
  })

  it('FR-21: picking v1 and v3 shows the changed lines side by side', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2, specV3])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v3')

    const compare = screen.getByRole('form', { name: 'Compare versions' })
    await user.selectOptions(within(compare).getByLabelText('From version'), '1')
    await user.selectOptions(within(compare).getByLabelText('To version'), '3')
    await user.click(within(compare).getByRole('button', { name: 'Compare' }))

    const diff = await screen.findByRole('table', { name: /Changes from v1 to v3/ })
    expect(diff).toHaveAccessibleName('Changes from v1 to v3: 2 lines added, 1 line removed')
    // Added/removed is in the text, not only the colour (NFR-A11Y).
    expect(within(diff).getByRole('cell', { name: 'Removed: Track stock levels.' })).toBeInTheDocument()
    expect(within(diff).getByRole('cell', { name: 'Added: Track stock levels across warehouses.' })).toBeInTheDocument()
    expect(within(diff).getByRole('cell', { name: 'Added: - POST /items' })).toBeInTheDocument()
    // The changed line is paired with its replacement on one row.
    const changed = within(diff).getByRole('cell', { name: 'Removed: Track stock levels.' }).closest('tr')
    expect(changed).toHaveTextContent('Track stock levels across warehouses.')
    // Unchanged lines appear on both sides without a marker.
    expect(within(diff).getAllByRole('cell', { name: '- GET /items' })).toHaveLength(2)
  })

  it('FR-21: comparing a version with itself asks for two different versions', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    const compare = screen.getByRole('form', { name: 'Compare versions' })
    await user.selectOptions(within(compare).getByLabelText('From version'), '2')
    await user.click(within(compare).getByRole('button', { name: 'Compare' }))
    expect(within(compare).getByText('Pick two different versions.')).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: /Changes from/ })).not.toBeInTheDocument()
  })

  it('FR-18: a Contributor edits locked v3 and saves it as draft v4 with parent v3', async () => {
    const project = seedProjectAs('contributor', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2, specV3], 'locked')
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v3')
    // Contributors can't decide artifacts.
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Edit as new draft' }))
    const editor = await screen.findByLabelText('Markdown')
    expect(editor).toHaveValue(specV3)

    // Saving unchanged content is caught before the request.
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))
    expect(await screen.findAllByText('Change the Markdown before saving: it matches v3.')).not.toHaveLength(0)
    expect(requestsTo('POST', `/artifacts/${SPEC}`)).toHaveLength(0)

    await user.type(editor, '\n- DELETE /items/{{id}')
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))

    expect(await screen.findByText('Saved as draft v4. An Owner can now approve or reject it.')).toBeInTheDocument()
    const article = await content('Technical specification v4')
    expect(within(article).getByText('DELETE /items/{id}')).toBeInTheDocument()
    // The save names the version the edit started from (FR-18: no silent overwrite).
    expect(requestsTo('POST', `/artifacts/${SPEC}`)[0]?.body).toEqual({ content: `${specV3}\n- DELETE /items/{id}`, baseVersion: 3 })
    const v4 = project.artifacts.find((a) => a.type === SPEC && a.version === 4)
    expect(v4).toMatchObject({ status: 'draft', parentVersion: 3 })
    expect(detail('Parent version')).toHaveTextContent('v3')
  })

  it('FR-18: editing while a draft is open warns that it will be superseded, and empty content is invalid', async () => {
    const project = seedProjectAs('contributor', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(screen.getByRole('button', { name: 'Edit as new draft' }))
    expect(await screen.findByText(/Saving supersedes the open draft v2/)).toBeInTheDocument()
    await user.clear(screen.getByLabelText('Markdown'))
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))
    expect(screen.getByLabelText('Markdown')).toHaveAccessibleDescription(/Write some Markdown before saving\./)
    expect(requestsTo('POST', `/artifacts/${SPEC}`)).toHaveLength(0)
  })

  it('FR-18: WRONG_PHASE shows the mapped copy and keeps the edit', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifactHistory(p, SPEC, [specV1], 'locked')
    })
    server.use(
      http.post('*/api/projects/:id/artifacts/:type', () => problem(409, 'WRONG_PHASE', 'Wrong phase.')),
    )
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v1')

    await user.click(screen.getByRole('button', { name: 'Edit as new draft' }))
    await user.type(await screen.findByLabelText('Markdown'), '\nMore')
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))

    expect(await screen.findByText(contextualErrorMessages.createArtifactDraft.WRONG_PHASE)).toBeInTheDocument()
    expect(screen.getByLabelText('Markdown')).toHaveValue(`${specV1}\nMore`)
  })

  it('FR-18: a version saved meanwhile is reported (409 CONFLICT), not superseded, and the edit is kept', async () => {
    const project = seedProjectAs('contributor', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2], 'locked')
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(screen.getByRole('button', { name: 'Edit as new draft' }))
    const editor = await screen.findByLabelText('Markdown')
    await user.type(editor, '\nMine')

    // Someone else saves v3 while this edit is open.
    seedArtifact(project, { type: SPEC, content: specV3 })
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))

    expect(
      await screen.findByText(
        'Someone saved a newer version (v3) while you were editing. Your text is kept here: copy it, then cancel and edit again to start from v3.',
      ),
    ).toBeInTheDocument()
    expect(requestsTo('POST', `/artifacts/${SPEC}`)[0]?.body).toEqual({ content: `${specV2}\nMine`, baseVersion: 2 })
    expect(screen.getByLabelText('Markdown')).toHaveValue(`${specV2}\nMine`)
    // v3 stays the open draft; nothing was superseded.
    expect(project.artifacts.filter((a) => a.type === SPEC).map((a) => a.status)).toEqual(['locked', 'locked', 'draft'])
    // The refetched latest version doesn't move the editor's base under the user.
    await waitFor(() => {
      expect(screen.getByLabelText('Version')).toHaveDisplayValue(/^v3/)
    })
    expect(screen.getByLabelText('Markdown')).toHaveAccessibleDescription(/Starting from v2\./)
  })

  it('FR-17: an Owner approves a draft, which locks it', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(screen.getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve technical specification v2?' })
    expect(dialog).toHaveAccessibleDescription(
      'v2 is locked and becomes the current technical specification. Any existing implementation and optimisation plans and all execution steps are invalidated.',
    )
    await user.type(within(dialog).getByLabelText('Comment (optional)'), 'Ship it')
    await user.click(within(dialog).getByRole('button', { name: 'Approve v2' }))

    expect(await screen.findByText('v2 approved and locked.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => {
      expect(detail('Status')).toHaveTextContent('Approved')
    })
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({ comment: 'Ship it' })
  })

  it('FR-17: reject needs confirmation; the agent regenerates, and the page waits for the new version', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    const { user, live } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(screen.getByRole('button', { name: 'Reject' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Reject technical specification v2?' })
    expect(dialog).toHaveAccessibleDescription(
      'v2 is marked rejected and the agent that produced it runs again with your feedback.',
    )
    // Cancelling sends nothing.
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(requestsTo('POST', '/reject')).toHaveLength(0)

    await user.click(screen.getByRole('button', { name: 'Reject' }))
    const again = await screen.findByRole('alertdialog')
    await user.type(within(again).getByLabelText('Feedback (optional)'), 'Add the POST endpoint')
    await user.click(within(again).getByRole('button', { name: 'Reject v2' }))

    expect(await screen.findByText('v2 rejected.')).toBeInTheDocument()
    expect(requestsTo('POST', '/reject')[0]?.body).toEqual({ comment: 'Add the POST endpoint' })
    // §7: pending until the regenerated version arrives.
    expect(screen.getByText(/The agent is regenerating the technical specification/)).toBeInTheDocument()
    await waitFor(() => {
      expect(detail('Rejection comment')).toHaveTextContent('Add the POST endpoint')
    })

    // The agent finishes: the server pushes the artifact event (FR-46) and the page refreshes.
    seedArtifact(project, { type: SPEC, content: specV3 })
    live.latest.emit(projectEvent(project.id, 'artifact', { type: 'ARTIFACT_CREATED' }))

    expect(await content('Technical specification v3')).toBeInTheDocument()
    expect(screen.queryByText(/The agent is regenerating/)).not.toBeInTheDocument()
  })

  it('FR-17: rejecting a human edit says no agent runs', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: SPEC, content: specV1, status: 'locked' })
      seedArtifact(p, { type: SPEC, content: specV2, authorUserId: p.members[0]?.userId ?? '' })
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(screen.getByRole('button', { name: 'Reject' }))
    expect(await screen.findByRole('alertdialog')).toHaveAccessibleDescription(
      'v2 is marked rejected and your comment is recorded. No agent runs for a human edit.',
    )
    await user.click(screen.getByRole('button', { name: 'Reject v2' }))
    expect(await screen.findByText('v2 rejected.')).toBeInTheDocument()
    expect(screen.queryByText(/The agent is regenerating/)).not.toBeInTheDocument()
  })

  it('FR-17: ARTIFACT_NOT_DRAFT and ARTIFACT_LOCKED show the mapped copy when the page is stale', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')
    const v2 = project.artifacts.find((a) => a.version === 2)
    if (!v2) {
      throw new Error('missing v2')
    }

    // Someone else locked it meanwhile: reject → ARTIFACT_LOCKED.
    v2.status = 'locked'
    await user.click(screen.getByRole('button', { name: 'Reject' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Reject v2' }))
    expect(await screen.findByText(contextualErrorMessages.decideArtifact.ARTIFACT_LOCKED)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    // The refetch shows it approved, so the decision buttons are gone.
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument()
    })

    // A superseded draft can't be approved.
    v2.status = 'draft'
    await user.selectOptions(screen.getByLabelText('Version'), '1')
    await user.selectOptions(screen.getByLabelText('Version'), '2')
    await user.click(await screen.findByRole('button', { name: 'Approve' }))
    v2.status = 'superseded'
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Approve v2' }))
    expect(await screen.findByText(contextualErrorMessages.decideArtifact.ARTIFACT_NOT_DRAFT)).toBeInTheDocument()
  })

  const integrityAlert = () =>
    screen.getAllByRole('alert').find((el) => el.textContent.includes('This version failed its integrity check'))

  it('FR-20: a corrupted current agent version shows a warning that regeneration was requested, and cannot be decided', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: SPEC, content: specV1, integrity: 'corrupted' })
    })
    await openArtifact(project, SPEC)
    await content('Technical specification v1')

    expect(integrityAlert()).toHaveTextContent(/regeneration was requested/)
    expect(detail('Integrity')).toHaveTextContent('Integrity check failed')
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
  })

  it('FR-20: a corrupted older version only says it failed the check (no regeneration for history)', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifact(p, { type: SPEC, content: specV1, status: 'locked', integrity: 'corrupted' })
      seedArtifact(p, { type: SPEC, content: specV2, status: 'locked' })
    })
    await openArtifact(project, SPEC, 1)
    await content('Technical specification v1')

    const alert = integrityAlert()
    expect(alert).toHaveTextContent(/An integrity alert was raised\.$/)
    expect(alert).not.toHaveTextContent(/regeneration|replace it/)
  })

  it('FR-20: a corrupted current human edit says to replace it; no regeneration is claimed', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: SPEC, content: specV1, status: 'locked' })
      seedArtifact(p, { type: SPEC, content: specV2, integrity: 'corrupted', authorUserId: p.members[0]?.userId ?? '' })
    })
    await openArtifact(project, SPEC)
    await content('Technical specification v2')

    const alert = integrityAlert()
    expect(alert).toHaveTextContent(/Edit and save a new draft to replace it\./)
    expect(alert).not.toHaveTextContent(/regeneration/)
  })

  it('FR-21: unchanged lines far from a change collapse into "⋯ N unchanged lines" rows', async () => {
    const body = Array.from({ length: 20 }, (_, i) => `Line ${String(i + 1)}`)
    const changed = body.map((l) => (l === 'Line 10' ? 'Line ten' : l))
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [body.join('\n'), changed.join('\n')])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    const compare = screen.getByRole('form', { name: 'Compare versions' })
    await user.selectOptions(within(compare).getByLabelText('From version'), '1')
    await user.selectOptions(within(compare).getByLabelText('To version'), '2')
    await user.click(within(compare).getByRole('button', { name: 'Compare' }))

    const diff = await screen.findByRole('table', { name: 'Changes from v1 to v2: 1 line added, 1 line removed' })
    // Lines 1–6 and 14–20 are hidden; 7–9 and 11–13 stay as context around the change.
    expect(within(diff).getByRole('cell', { name: '6 unchanged lines hidden (v1 lines 1–6, v2 lines 1–6)' })).toBeInTheDocument()
    expect(within(diff).getByRole('cell', { name: '7 unchanged lines hidden (v1 lines 14–20, v2 lines 14–20)' })).toBeInTheDocument()
    expect(within(diff).getByRole('cell', { name: 'Removed: Line 10' })).toBeInTheDocument()
    expect(within(diff).getAllByRole('cell', { name: 'Line 7' })).toHaveLength(2)
    expect(within(diff).queryByRole('cell', { name: 'Line 6' })).not.toBeInTheDocument()
  })

  it('FR-21: identical versions say there are no differences', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV1])
    })
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    const compare = screen.getByRole('form', { name: 'Compare versions' })
    await user.selectOptions(within(compare).getByLabelText('From version'), '1')
    await user.selectOptions(within(compare).getByLabelText('To version'), '2')
    await user.click(within(compare).getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('No differences between v1 and v2.')).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: /Changes from/ })).not.toBeInTheDocument()
  })

  it('FR-21: a diff over the size limit (400 VALIDATION) explains why, with no retry', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifactHistory(p, SPEC, [specV1, specV2])
    })
    server.use(
      http.get('*/api/projects/:id/artifacts/:type/diff', () =>
        problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
          errors: { to: ['The two versions have 120000 lines together; diffs are limited to 100000 lines.'] },
        }),
      ),
    )
    const { user } = await openArtifact(project, SPEC)
    await content('Technical specification v2')

    await user.click(within(screen.getByRole('form', { name: 'Compare versions' })).getByRole('button', { name: 'Compare' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("The diff couldn't load")
    expect(alert).toHaveTextContent(contextualErrorMessages.artifactDiff.VALIDATION)
    expect(within(alert).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  })

  it('NFR-SEC: remote Markdown images render as a link and are never loaded', async () => {
    const project = seedProjectAs('viewer', (p) => {
      seedArtifact(p, {
        type: SPEC,
        content: '# Spec\n\n![x](https://evil/x?d=1)\n\n![inline](data:image/png;base64,iVBORw0KGgo=)\n\n![local](/logo.svg)',
      })
    })
    await openArtifact(project, SPEC)

    const article = await content('Technical specification v1')
    const link = within(article).getByRole('link', { name: 'x (external image, not loaded: https://evil/x?d=1)' })
    expect(link).toHaveAttribute('href', 'https://evil/x?d=1')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer nofollow')
    const images = within(article).getAllByRole('img')
    expect(images.map((img) => img.getAttribute('src'))).toEqual(['data:image/png;base64,iVBORw0KGgo=', '/logo.svg'])
    expect(article.querySelector('img[src*="evil"]')).toBeNull()
  })

  it('shows the empty state, and a Writer can write the first draft', async () => {
    const project = seedProjectAs('contributor')
    const { user } = await openArtifact(project, SPEC)

    expect(await screen.findByText('No versions yet')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Write the first draft' }))
    await user.type(await screen.findByLabelText('Markdown'), '# Spec')
    await user.click(screen.getByRole('button', { name: 'Save as new draft' }))

    expect(await content('Technical specification v1')).toBeInTheDocument()
  })

  it('an unknown artifact type shows not found', async () => {
    const project = seedProjectAs('viewer')
    await openArtifact(project, 'nonsense' as typeof SPEC)
    expect(screen.getByRole('heading', { name: "There's no artifact of that type" })).toBeInTheDocument()
  })
})
