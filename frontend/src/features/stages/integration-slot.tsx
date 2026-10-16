import { StatusBadge } from '../../components/shared/status-badge'
import { StageSection } from './stage-section'

/**
 * FR-55: a stage panel whose integration lands in a later phase (merge readiness FR-84, GitHub Release, production
 * and preview deployments FR-90/FR-91). It holds the panel's place on the stage page and says what will show there,
 * so the page doesn't imply the feature exists yet.
 */
export function IntegrationSlot({ id, title, description }: { id: string; title: string; description: string }) {
  return (
    <StageSection
      id={id}
      title={title}
      actions={<StatusBadge tone="neutral">Not available yet</StatusBadge>}
      className="border-dashed"
    >
      <p data-testid={`${id}-slot`} className="text-sm text-muted-foreground">
        {description}
      </p>
    </StageSection>
  )
}
