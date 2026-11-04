'use client'

import { useCurrentUser } from '../../../features/auth/auth-api'
import { HomePage } from '../../../features/home/home-page'

/** /home (FR-207): the signed-in landing page. The (app) layout has already loaded the user. */
export default function HomeRoute() {
  const user = useCurrentUser()
  return user ? <HomePage user={user} /> : null
}
