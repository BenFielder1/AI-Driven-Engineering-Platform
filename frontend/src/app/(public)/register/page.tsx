'use client'

import { RegisterPage } from '../../../features/auth/register-page'
import { parseRegisterSearch } from '../../../features/auth/auth-search'
import { useSearch } from '../../../lib/navigation'

/** /register (FR-1): `?returnUrl=`. */
export default function RegisterRoute() {
  const { returnUrl } = useSearch(parseRegisterSearch)
  return <RegisterPage returnUrl={returnUrl} />
}
