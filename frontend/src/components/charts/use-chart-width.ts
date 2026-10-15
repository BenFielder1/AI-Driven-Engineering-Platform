import { useLayoutEffect, useState } from 'react'

/**
 * The width of a chart's container (attach `setElement` as its `ref`), kept up to date with a ResizeObserver. Falls
 * back to `fallback` when nothing can be measured (no layout, e.g. jsdom), so the chart still renders its marks
 * instead of an empty 0 x 0 box.
 */
export function useChartWidth(fallback = 480): { setElement: (el: HTMLElement | null) => void; width: number } {
  const [element, setElement] = useState<HTMLElement | null>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    if (!element) {
      return undefined
    }
    // ResizeObserver reports the initial size too, so this is the only place the width is set.
    const observer = new ResizeObserver(() => {
      setWidth(element.clientWidth)
    })
    observer.observe(element)
    return () => {
      observer.disconnect()
    }
  }, [element])
  return { setElement, width: width > 0 ? width : fallback }
}
