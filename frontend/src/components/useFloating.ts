import { nextTick, onBeforeUnmount, ref, watch, type Ref } from 'vue'

const GAP = 4
const EDGE = 8

/**
 * Places a body-level panel under its field, or above it when there is no room.
 * Being fixed on the body, it is never clipped by a dialog or a scrolling table, and it stays on screen.
 */
export function useFloating(anchor: Ref<HTMLElement | null>, panel: Ref<HTMLElement | null>, open: Ref<boolean>, options: { matchWidth?: boolean } = {}) {
  // Transparent, not hidden, while it is measured: a hidden panel could not take keyboard focus.
  const style = ref<Record<string, string>>({ opacity: '0' })

  function place() {
    if (!anchor.value || !panel.value) return
    const box = anchor.value.getBoundingClientRect()
    const viewport = window.visualViewport
    const viewportLeft = viewport?.offsetLeft ?? 0
    const viewportTop = viewport?.offsetTop ?? 0
    const viewportWidth = viewport?.width ?? window.innerWidth
    const viewportHeight = viewport?.height ?? window.innerHeight
    const wantedWidth = options.matchWidth ? Math.max(box.width, 220) : panel.value.offsetWidth
    const width = Math.min(wantedWidth, viewportWidth - EDGE * 2)
    const height = panel.value.scrollHeight
    const below = viewportTop + viewportHeight - box.bottom - GAP - EDGE
    const above = box.top - viewportTop - GAP - EDGE
    const flip = height > below && above > below
    const room = flip ? above : below
    const left = Math.min(Math.max(viewportLeft + EDGE, box.right - width), viewportLeft + viewportWidth - width - EDGE)
    style.value = {
      position: 'fixed',
      left: `${left}px`,
      width: `${width}px`,
      maxHeight: `${Math.max(room, 0)}px`,
      ...(flip ? { bottom: `${window.innerHeight - box.top + GAP}px` } : { top: `${box.bottom + GAP}px` }),
    }
  }

  function watchViewport(on: boolean) {
    const change = on ? window.addEventListener : window.removeEventListener
    change('resize', place)
    change('scroll', place, true)
    if (on) {
      window.visualViewport?.addEventListener('resize', place)
      window.visualViewport?.addEventListener('scroll', place)
    } else {
      window.visualViewport?.removeEventListener('resize', place)
      window.visualViewport?.removeEventListener('scroll', place)
    }
  }

  watch(open, async (isOpen) => {
    watchViewport(isOpen)
    if (!isOpen) return
    style.value = { opacity: '0', position: 'fixed', top: '0', left: '0' }
    await nextTick()
    place()
  })

  onBeforeUnmount(() => watchViewport(false))

  return { style, place }
}
