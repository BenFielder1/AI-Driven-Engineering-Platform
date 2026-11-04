import { redirect } from 'next/navigation'

/** `/` → /home (FR-207; the (app) guard sends signed-out users on to /login). M41 replaces this with the marketing Home. */
export default function Home(): never {
  redirect('/home')
}
