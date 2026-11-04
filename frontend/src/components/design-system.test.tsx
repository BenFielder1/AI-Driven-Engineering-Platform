import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactElement } from 'react'
import { toast } from 'sonner'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../test/axe'
import { renderWithProviders } from '../test/render'
import { EmptyState } from './shared/empty-state'
import { MetricCard } from './shared/metric-card'
import { PageHeader } from './shared/page-header'
import { Panel } from './shared/panel'
import { SkeletonCards, SkeletonPage, SkeletonTable } from './shared/skeletons'
import { LIFECYCLE_STATUS_STYLES, LifecycleStatusBadge, StatusBadge, type LifecycleStatus } from './shared/status-badge'
import { ProjectVersionBadges } from './shared/version-badge'
import { Alert, ErrorSummary } from './ui/alert'
import { Badge } from './ui/badge'
import { Button } from './ui/button'
import { buttonClasses } from './ui/button-classes'
import { Pagination } from './ui/pagination'
import { RadioGroup } from './ui/radio-group'
import { SelectField } from './ui/select-field'
import { Skeleton } from './ui/skeleton'
import { Toaster } from './ui/sonner'
import { ErrorState, EmptyState as InlineEmptyState, LoadingState } from './ui/states'
import { TextAreaField } from './ui/textarea-field'
import { TextField } from './ui/text-field'

afterEach(() => {
  document.documentElement.className = ''
})

/** FR-205: each restyled component, rendered as a page would use it. */
const components: Record<string, () => ReactElement> = {
  Button: () => (
    <div>
      <Button>Create project</Button>
      <Button variant="secondary">Cancel</Button>
      <Button variant="danger">Delete</Button>
      <Button variant="ghost">More</Button>
      <Button pending pendingLabel="Saving…">
        Save
      </Button>
    </div>
  ),
  StatusBadge: () => (
    <div>
      {(Object.keys(LIFECYCLE_STATUS_STYLES) as LifecycleStatus[]).map((status) => (
        <LifecycleStatusBadge key={status} status={status} />
      ))}
      <Badge tone="danger">Failed checks</Badge>
      <ProjectVersionBadges released="1.1.0" development="1.2.0" />
    </div>
  ),
  EmptyState: () => (
    <div>
      <EmptyState
        variant="no_data"
        title="No projects yet"
        description="Projects hold an idea from refinement through release."
        action={<Button>Create project</Button>}
      />
      <EmptyState variant="not_connected" title="No connections" description="Connect a Git host to push code." />
      <InlineEmptyState title="Nothing here yet">Runs show up once execution starts.</InlineEmptyState>
    </div>
  ),
  Skeletons: () => (
    <div>
      <SkeletonCards />
      <SkeletonTable />
      <SkeletonPage />
    </div>
  ),
  ErrorState: () => <ErrorState error={new Error('boom')} onRetry={() => undefined} />,
  LoadingState: () => <LoadingState />,
  Alert: () => (
    <div>
      <Alert tone="success" title="Saved">
        Your changes are saved.
      </Alert>
      <Alert tone="warning">Check the settings.</Alert>
      <ErrorSummary title="Fix these" errors={[{ fieldId: 'name', message: 'Enter a name' }]} />
    </div>
  ),
  Fields: () => (
    <form>
      <TextField id="name" label="Name" hint="Shown to your team." />
      <TextField label="Slug" error="Use lowercase letters." />
      <TextAreaField label="Idea" />
      <SelectField label="Role">
        <option>Owner</option>
      </SelectField>
      <RadioGroup
        legend="Visibility"
        options={[
          { value: 'private', label: 'Private', description: 'Only members.' },
          { value: 'public', label: 'Public' },
        ]}
        inputProps={{ name: 'visibility', onChange: () => Promise.resolve(), onBlur: () => Promise.resolve(), ref: () => undefined }}
      />
    </form>
  ),
  Panel: () => (
    <Panel>
      <PageHeader title="Projects" description="Everything you are building." actions={<Button>Create project</Button>} />
      <MetricCard label="Open gates" value="3" />
    </Panel>
  ),
  Pagination: () => <Pagination page={2} pageCount={3} onPage={() => undefined} />,
}

describe('FR-205: restyled components render in both themes with no axe violations', () => {
  for (const theme of THEMES) {
    for (const [name, Example] of Object.entries(components)) {
      it(`FR205_axe_${theme}: ${name}`, async () => {
        setTheme(theme)
        const { container } = renderWithProviders(<Example />)
        expect(document.documentElement).toHaveClass(theme)
        expect(await axeViolations(container)).toEqual([])
      })
    }
  }
})

describe('FR-205: component rules', () => {
  it('FR205_axe_helper: the axe helper does report violations (a button with no name)', async () => {
    const { container } = render(<button type="button" />)
    expect((await axeViolations(container)).join(' ')).toContain('button-name')
  })

  it('FR205_status_badges: a dot plus a label, never colour alone, with the §4.31 tones', () => {
    render(
      <div>
        {(Object.keys(LIFECYCLE_STATUS_STYLES) as LifecycleStatus[]).map((status) => (
          <LifecycleStatusBadge key={status} status={status} />
        ))}
      </div>,
    )
    const tones = Object.fromEntries(
      ['Locked', 'In review', 'Approved', 'Failed', 'Running'].map((label) => {
        const badge = screen.getByText(label)
        // The dot is decorative; the label carries the meaning.
        const dot = badge.querySelector('[aria-hidden]')
        expect(dot).toHaveClass('rounded-full')
        expect(badge).toHaveClass('text-foreground')
        return [label, badge.getAttribute('data-tone')]
      }),
    )
    expect(tones).toEqual({ Locked: 'neutral', 'In review': 'warning', Approved: 'success', Failed: 'destructive', Running: 'info' })
  })

  it('FR205_running_pulse: only Running pulses, and only when motion is allowed (motion-safe)', () => {
    render(
      <div>
        <LifecycleStatusBadge status="running" />
        <LifecycleStatusBadge status="approved" />
        <StatusBadge tone="info">Queued</StatusBadge>
      </div>,
    )
    const dot = (label: string) => screen.getByText(label).querySelector('[aria-hidden]')
    expect(dot('Running')).toHaveClass('motion-safe:animate-pulse')
    expect(dot('Running')).not.toHaveClass('animate-pulse')
    expect(dot('Approved')?.className).not.toContain('animate-pulse')
    expect(dot('Queued')?.className).not.toContain('animate-pulse')
  })

  it('FR205_skeleton_motion: skeletons pulse only when motion is allowed', () => {
    const { container } = render(<Skeleton className="h-4" />)
    expect(container.firstElementChild).toHaveClass('motion-safe:animate-pulse')
    expect(container.firstElementChild).not.toHaveClass('animate-pulse')
  })

  it('FR205_skeleton_status: a loading group is one labelled status', () => {
    render(<SkeletonTable />)
    expect(screen.getByRole('status')).toHaveTextContent('Loading table…')
  })

  it('FR205_empty_state: a title, one sentence and the primary action', () => {
    render(
      <EmptyState
        variant="no_data"
        title="No projects yet"
        description="Projects hold an idea from refinement through release."
        action={<Button>Create project</Button>}
      />,
    )
    const empty = screen.getByTestId('empty-state')
    expect(within(empty).getByRole('heading', { name: 'No projects yet' })).toBeInTheDocument()
    expect(empty).toHaveTextContent('Projects hold an idea from refinement through release.')
    expect(within(empty).getByRole('button', { name: 'Create project' })).toBeInTheDocument()
  })

  it('FR205_panel_error_retry: a panel error shows the message and a retry button that calls onRetry', async () => {
    const onRetry = vi.fn()
    render(<ErrorState error={new Error('boom')} onRetry={onRetry} />)
    const alert = screen.getByRole('alert')
    await userEvent.setup().click(within(alert).getByRole('button', { name: 'Try again' }))
    expect(onRetry).toHaveBeenCalledOnce()
  })

  it('FR205_targets: buttons are at least 24 x 24 px (min-h-10) and use the global focus ring', () => {
    for (const variant of ['primary', 'secondary', 'danger', 'ghost'] as const) {
      const classes = buttonClasses(variant).split(' ')
      expect(classes).toContain('min-h-10')
      expect(classes).not.toContain('outline-none')
      expect(classes).not.toContain('glow-primary')
    }
  })

  it('FR205_toasts: toasts sit bottom-right', async () => {
    render(<Toaster />)
    act(() => {
      toast('Saved')
    })
    const list = await screen.findByText('Saved')
    const toaster = list.closest('[data-sonner-toaster]')
    expect(toaster).toHaveAttribute('data-y-position', 'bottom')
    expect(toaster).toHaveAttribute('data-x-position', 'right')
  })
})
