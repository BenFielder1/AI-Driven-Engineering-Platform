import { z } from 'zod'

/** Matches the backend Identity policy: >= 8 characters with at least one letter and one digit. */
export const passwordSchema = z
  .string()
  .min(8, { error: 'Password must be at least 8 characters.' })
  .max(128, { error: 'Password must be 128 characters or fewer.' })
  .regex(/\p{L}/u, { error: 'Password must contain at least one letter.' })
  // Identity's RequireDigit accepts ASCII 0-9 only; the letter rule is ours and allows any Unicode letter.
  .regex(/[0-9]/, { error: 'Password must contain at least one digit.' })

const emailSchema = z
  .string()
  .trim()
  .min(1, { error: 'Enter your email address.' })
  .max(256, { error: 'Email must be 256 characters or fewer.' })
  .pipe(z.email({ error: 'Enter a valid email address, like name@example.com.' }))

export const loginSchema = z.object({
  email: emailSchema,
  password: z.string().min(1, { error: 'Enter your password.' }),
})

export const registerSchema = z.object({
  email: emailSchema,
  name: z
    .string()
    .trim()
    .min(1, { error: 'Enter your name.' })
    .max(200, { error: 'Name must be 200 characters or fewer.' }),
  password: passwordSchema,
})

export type LoginValues = z.infer<typeof loginSchema>
export type RegisterValues = z.infer<typeof registerSchema>
