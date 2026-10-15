import type { ReactNode } from 'react'

export type AlertTone = 'error' | 'success' | 'warning' | 'info'

const tones: Record<AlertTone, string> = {
  error: 'border-destructive bg-destructive/10 text-foreground',
  success: 'border-success bg-success/10 text-foreground',
  warning: 'border-warning bg-warning/10 text-foreground',
  info: 'border-primary/50 bg-primary/10 text-foreground',
}

interface AlertProps {
  tone?: AlertTone
  title?: string
  children?: ReactNode
  /** `alert` interrupts screen readers (errors); `status` is polite. Omit for static content. */
  role?: 'alert' | 'status'
  className?: string
}

export function Alert({ tone = 'info', title, children, role, className = '' }: AlertProps) {
  return (
    <div role={role} className={`rounded-lg border border-l-4 p-3 text-sm backdrop-blur-sm ${tones[tone]} ${className}`}>
      {title && <p className="font-semibold">{title}</p>}
      {children}
    </div>
  )
}

export interface SummaryError {
  /** id of the field to focus, if the error belongs to one */
  fieldId?: string
  message: string
}

/**
 * Form error summary: announced through an always-present live region so screen readers hear new errors
 * after submit; each field error links to its input. The links are at least 24 by 24 CSS px (WCAG 2.2 SC 2.5.8).
 */
export function ErrorSummary({ title, errors }: { title: string; errors: SummaryError[] }) {
  return (
    <div role="alert" aria-live="assertive">
      {errors.length > 0 && (
        <Alert tone="error" title={title}>
          <ul className="mt-1 list-disc pl-5">
            {errors.map((e) => (
              <li key={`${e.fieldId ?? ''}:${e.message}`}>
                {e.fieldId ? (
                  <a href={`#${e.fieldId}`} className="inline-block min-h-6 min-w-6 py-0.5 underline">
                    {e.message}
                  </a>
                ) : (
                  e.message
                )}
              </li>
            ))}
          </ul>
        </Alert>
      )}
    </div>
  )
}
