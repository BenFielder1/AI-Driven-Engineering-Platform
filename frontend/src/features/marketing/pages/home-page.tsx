import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'

import { useNavigate } from '../../../lib/navigation'
import { meQueryOptions } from '../../auth/auth-api'
import { HomeHero } from '../components/home/home-hero'
import { LifecycleSteps, WhyTevrin, WorksWithStrip } from '../components/home/home-intro-sections'
import { ClosingCta, FeatureBento, HomeFaq, LatestChanges, SelfHostTeaser } from '../components/home/home-more-sections'

/**
 * FR-211 (§4.32): a signed-in user on `/` goes to `/home`. The content renders first (in the server HTML too) and the
 * redirect happens once `me` resolves non-null, so the session check never blocks a visitor.
 */
function useSignedInRedirect(to: string): void {
  const signedIn = useQuery(meQueryOptions).data != null
  const navigate = useNavigate()
  useEffect(() => {
    if (signedIn) {
      void navigate({ href: to, replace: true })
    }
  }, [signedIn, to, navigate])
}

/**
 * `/` for visitors (FR-212), sections in the spec's order: hero with the product preview, Works with, lifecycle, Why
 * Tevrin, feature bento, self-host, latest changes, FAQ and the closing call to action.
 */
export function MarketingHomePage() {
  useSignedInRedirect('/home')
  return (
    <>
      <HomeHero />
      <WorksWithStrip />
      <LifecycleSteps />
      <WhyTevrin />
      <FeatureBento />
      <SelfHostTeaser />
      <LatestChanges />
      <HomeFaq />
      <ClosingCta />
    </>
  )
}
