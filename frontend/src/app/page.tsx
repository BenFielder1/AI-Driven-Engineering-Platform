import { redirect } from 'next/navigation'

/** `/` → /projects (the (app) guard sends signed-out users on to /login). */
export default function Home(): never {
  redirect('/projects')
}
