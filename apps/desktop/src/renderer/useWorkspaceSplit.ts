import { computed, ref, watch } from 'vue'

const storageKey = 'workspace.panel-sizes'
const dividerSize = 8
const panes = {
  queue: { axis: 'x', min: 220, remaining: 320, decrease: 'ArrowLeft', increase: 'ArrowRight' },
  preview: { axis: 'y', min: 160, remaining: 160, decrease: 'ArrowUp', increase: 'ArrowDown' }
} as const
type Pane = keyof typeof panes

function savedSizes(): Partial<Record<Pane, number>> {
  try {
    const sizes = JSON.parse(localStorage.getItem(storageKey) ?? '{}')
    return {
      queue: Number.isFinite(sizes?.queue) ? sizes.queue : undefined,
      preview: Number.isFinite(sizes?.preview) ? sizes.preview : undefined
    }
  } catch {
    return {}
  }
}

export function useWorkspaceSplit() {
  const layout = ref<HTMLElement>()
  const preferred = ref(savedSizes())
  const dimensions = ref({ width: 0, height: 0, viewportHeight: 0 })
  const activePane = ref<Pane>()
  let drag: { pane: Pane; pointerId: number; start: number; size: number } | undefined

  watch(layout, (element, _, onCleanup) => {
    if (!element) return
    function measure() {
      if (!element) return
      dimensions.value = {
        width: element.clientWidth,
        height: element.clientHeight,
        viewportHeight: window.innerHeight
      }
    }
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    measure()
    onCleanup(() => {
      observer.disconnect()
      stopResize()
    })
  }, { flush: 'post' })

  function maxSize(pane: Pane) {
    const { width, height } = dimensions.value
    const { axis, min, remaining } = panes[pane]
    return Math.max(min, (axis === 'x' ? width : height) - dividerSize - remaining)
  }

  function clampSize(pane: Pane, size: number) {
    return Math.min(maxSize(pane), Math.max(panes[pane].min, Math.round(size)))
  }

  const queueWidth = computed(() => clampSize('queue',
    preferred.value.queue ?? (dimensions.value.width - dividerSize) * 4 / 9))
  const previewHeight = computed(() => clampSize('preview',
    preferred.value.preview ?? Math.min(432, Math.max(192, dimensions.value.viewportHeight * .38))))
  const layoutStyle = computed(() => dimensions.value.width ? {
    '--workspace-queue-width': `${queueWidth.value}px`,
    '--workspace-preview-height': `${previewHeight.value}px`
  } : undefined)

  function currentSize(pane: Pane) {
    return pane === 'queue' ? queueWidth.value : previewHeight.value
  }

  function save() {
    try { localStorage.setItem(storageKey, JSON.stringify(preferred.value)) } catch { /* Keep sizes for this session. */ }
  }

  function coordinate(pane: Pane, event: PointerEvent) {
    return panes[pane].axis === 'x' ? event.clientX : event.clientY
  }

  function startResize(pane: Pane, event: PointerEvent) {
    if (event.button !== 0 || drag) return
    const divider = event.currentTarget as HTMLElement
    divider.setPointerCapture(event.pointerId)
    event.preventDefault()
    drag = { pane, pointerId: event.pointerId, start: coordinate(pane, event), size: currentSize(pane) }
    activePane.value = pane
  }

  function moveResize(event: PointerEvent) {
    if (!drag || event.pointerId !== drag.pointerId) return
    const { pane, start, size } = drag
    const next = clampSize(pane, size + coordinate(pane, event) - start)
    if (next !== currentSize(pane)) preferred.value[pane] = next
  }

  function stopResize(event?: PointerEvent) {
    if (!drag || (event && event.pointerId !== drag.pointerId)) return
    drag = undefined
    activePane.value = undefined
    save()
  }

  function resizeWithKey(pane: Pane, event: KeyboardEvent) {
    const { decrease, increase, min } = panes[pane]
    const size = currentSize(pane)
    let next: number
    if (event.key === decrease) next = size - 16
    else if (event.key === increase) next = size + 16
    else if (event.key === 'Home') next = min
    else if (event.key === 'End') next = maxSize(pane)
    else return
    event.preventDefault()
    preferred.value[pane] = clampSize(pane, next)
    save()
  }

  function dividerValues(pane: Pane) {
    return {
      'aria-valuemin': panes[pane].min,
      'aria-valuemax': maxSize(pane),
      'aria-valuenow': currentSize(pane),
      'aria-valuetext': `${currentSize(pane)} 像素`
    }
  }

  return { layout, layoutStyle, activePane, dividerValues, startResize, moveResize, stopResize, resizeWithKey }
}
