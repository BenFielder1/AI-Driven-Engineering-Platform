import { CheckIcon, CircleIcon } from 'lucide-react'
import { DropdownMenu as DropdownMenuPrimitive } from 'radix-ui'
import { useRef, type ComponentProps } from 'react'

import { cn } from '../../lib/utils'

/** shadcn/ui "new-york" DropdownMenu on radix-ui (SPEC §7.1): role="menu", arrow keys, Escape returns focus. */

/**
 * Non-modal by default: a menu is not a dialog (WAI-ARIA menu button pattern), so the page behind it is neither
 * `aria-hidden` nor blocked. Radix's modal mode hides the rest of the page from assistive tech while the skip link,
 * sidebar and forms stay focusable (axe `aria-hidden-focus`). Focus still moves into the menu and arrow keys move within
 * it; Tab is held inside the open menu (Radix ignores it there), Escape closes it and returns focus to the trigger, and a
 * click outside closes it.
 */
export function DropdownMenu({ modal = false, ...props }: ComponentProps<typeof DropdownMenuPrimitive.Root>) {
  return <DropdownMenuPrimitive.Root data-slot="dropdown-menu" modal={modal} {...props} />
}

export function DropdownMenuTrigger(props: ComponentProps<typeof DropdownMenuPrimitive.Trigger>) {
  return <DropdownMenuPrimitive.Trigger data-slot="dropdown-menu-trigger" {...props} />
}

/**
 * A non-modal menu stays mounted while its close animation plays (e.g. right after Escape), and its outside-
 * pointerdown listener is still live: pressing the trigger then would reopen the menu and immediately dismiss it again,
 * swallowing the click. A press on the menu's own trigger is therefore never an "outside" interaction; the trigger
 * itself toggles the menu (and still closes an open one). Preventing it also keeps Escape returning focus to the trigger.
 */
function isOwnTrigger(content: HTMLElement | null, target: EventTarget | null) {
  const triggerId = content?.getAttribute('aria-labelledby')
  const trigger = triggerId ? content?.ownerDocument.getElementById(triggerId) : null
  return trigger != null && target instanceof Node && trigger.contains(target)
}

export function DropdownMenuContent({
  className,
  sideOffset = 4,
  ref,
  onInteractOutside,
  ...props
}: ComponentProps<typeof DropdownMenuPrimitive.Content>) {
  const contentRef = useRef<HTMLDivElement | null>(null)
  return (
    <DropdownMenuPrimitive.Portal>
      <DropdownMenuPrimitive.Content
        data-slot="dropdown-menu-content"
        sideOffset={sideOffset}
        ref={(node) => {
          contentRef.current = node
          if (typeof ref === 'function') {
            ref(node)
          } else if (ref) {
            ref.current = node
          }
        }}
        onInteractOutside={(event) => {
          onInteractOutside?.(event)
          if (isOwnTrigger(contentRef.current, event.target)) {
            event.preventDefault()
          }
        }}
        className={cn(
          'z-50 max-h-(--radix-dropdown-menu-content-available-height) min-w-[8rem] origin-(--radix-dropdown-menu-content-transform-origin) overflow-x-hidden overflow-y-auto rounded-xl border border-border/60 bg-popover/95 p-1 text-popover-foreground shadow-lg backdrop-blur-md data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95 data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95',
          className,
        )}
        {...props}
      />
    </DropdownMenuPrimitive.Portal>
  )
}

export function DropdownMenuGroup(props: ComponentProps<typeof DropdownMenuPrimitive.Group>) {
  return <DropdownMenuPrimitive.Group data-slot="dropdown-menu-group" {...props} />
}

/**
 * Choosing an item closes the menu at once, without the exit animation: the item usually navigates or changes the
 * page, and a menu still fading out would sit over it (and catch the next click). Escape and clicks outside still
 * animate. Items whose `onSelect` keeps the menu open (`preventDefault`) are left alone.
 */
function closeInstantlyOnSelect(onSelect: ((event: Event) => void) | undefined) {
  return (event: Event) => {
    onSelect?.(event)
    if (!event.defaultPrevented && event.currentTarget instanceof HTMLElement) {
      const content = event.currentTarget.closest<HTMLElement>('[data-slot="dropdown-menu-content"]')
      if (content) {
        content.style.animationName = 'none'
      }
    }
  }
}

const itemClass =
  'relative flex min-h-11 cursor-default items-center gap-2 rounded-lg px-2 py-1.5 text-sm outline-hidden select-none focus:bg-accent focus:text-accent-foreground data-[disabled]:pointer-events-none data-[disabled]:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*=size-])]:size-4'

export function DropdownMenuItem({
  className,
  variant = 'default',
  onSelect,
  ...props
}: ComponentProps<typeof DropdownMenuPrimitive.Item> & { variant?: 'default' | 'destructive' }) {
  return (
    <DropdownMenuPrimitive.Item
      data-slot="dropdown-menu-item"
      data-variant={variant}
      className={cn(
        itemClass,
        '[&_svg:not([class*=text-])]:text-muted-foreground data-[variant=destructive]:text-destructive',
        className,
      )}
      onSelect={closeInstantlyOnSelect(onSelect)}
      {...props}
    />
  )
}

export function DropdownMenuCheckboxItem({
  className,
  children,
  checked,
  onSelect,
  ...props
}: ComponentProps<typeof DropdownMenuPrimitive.CheckboxItem>) {
  return (
    <DropdownMenuPrimitive.CheckboxItem
      data-slot="dropdown-menu-checkbox-item"
      className={cn(itemClass, 'pl-8', className)}
      checked={checked}
      onSelect={closeInstantlyOnSelect(onSelect)}
      {...props}
    >
      <span className="pointer-events-none absolute left-2 flex size-3.5 items-center justify-center">
        <DropdownMenuPrimitive.ItemIndicator>
          <CheckIcon className="size-4" />
        </DropdownMenuPrimitive.ItemIndicator>
      </span>
      {children}
    </DropdownMenuPrimitive.CheckboxItem>
  )
}

export function DropdownMenuRadioGroup(props: ComponentProps<typeof DropdownMenuPrimitive.RadioGroup>) {
  return <DropdownMenuPrimitive.RadioGroup data-slot="dropdown-menu-radio-group" {...props} />
}

export function DropdownMenuRadioItem({
  className,
  children,
  onSelect,
  ...props
}: ComponentProps<typeof DropdownMenuPrimitive.RadioItem>) {
  return (
    <DropdownMenuPrimitive.RadioItem
      data-slot="dropdown-menu-radio-item"
      className={cn(itemClass, 'pl-8', className)}
      onSelect={closeInstantlyOnSelect(onSelect)}
      {...props}
    >
      <span className="pointer-events-none absolute left-2 flex size-3.5 items-center justify-center">
        <DropdownMenuPrimitive.ItemIndicator>
          <CircleIcon className="size-2 fill-current" />
        </DropdownMenuPrimitive.ItemIndicator>
      </span>
      {children}
    </DropdownMenuPrimitive.RadioItem>
  )
}

export function DropdownMenuLabel({ className, ...props }: ComponentProps<typeof DropdownMenuPrimitive.Label>) {
  return (
    <DropdownMenuPrimitive.Label
      data-slot="dropdown-menu-label"
      className={cn('px-2 py-1.5 text-sm font-medium', className)}
      {...props}
    />
  )
}

export function DropdownMenuSeparator({ className, ...props }: ComponentProps<typeof DropdownMenuPrimitive.Separator>) {
  return (
    <DropdownMenuPrimitive.Separator
      data-slot="dropdown-menu-separator"
      className={cn('-mx-1 my-1 h-px bg-border', className)}
      {...props}
    />
  )
}
