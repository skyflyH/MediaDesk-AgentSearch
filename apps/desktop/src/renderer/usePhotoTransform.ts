import { computed, ref } from 'vue'

export function usePhotoTransform(enabled: () => boolean) {
  const zoom = ref(100)
  const offset = ref({ x: 0, y: 0 })
  const dragging = ref(false)
  const minZoom = 50
  const fitZoom = 100
  const maxZoom = 400
  const zoomStep = 25
  let pointer: { id: number; x: number; y: number; target: HTMLElement } | undefined
  const imageStyle = computed(() => ({
    transform: `translate(${offset.value.x}px, ${offset.value.y}px) scale(${zoom.value / 100})`
  }))

  function setZoom(value: number, anchor = { x: 0, y: 0 }) {
    const next = Math.max(minZoom, Math.min(maxZoom, value))
    const ratio = next / zoom.value
    offset.value = {
      x: anchor.x - (anchor.x - offset.value.x) * ratio,
      y: anchor.y - (anchor.y - offset.value.y) * ratio
    }
    zoom.value = next
  }

  function endDrag(event?: PointerEvent) {
    if (!pointer || (event && event.pointerId !== pointer.id)) return
    const previous = pointer
    pointer = undefined
    dragging.value = false
    if (previous.target.hasPointerCapture(previous.id)) previous.target.releasePointerCapture(previous.id)
  }

  function reset() {
    endDrag()
    zoom.value = fitZoom
    offset.value = { x: 0, y: 0 }
  }

  function wheel(event: WheelEvent) {
    if (!enabled()) return
    event.preventDefault()
    event.stopPropagation()
    if (!event.deltaY) return
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
    // Normalize mouse wheels (lines/pages) and touchpad pixel deltas.
    const pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? rect.height : 1)
    setZoom(Math.round(zoom.value * Math.exp(-Math.max(-100, Math.min(100, pixels)) * .002)), {
      x: event.clientX - rect.left - rect.width / 2,
      y: event.clientY - rect.top - rect.height / 2
    })
  }

  function startDrag(event: PointerEvent) {
    if (!enabled() || event.button !== 0 || pointer) return
    const target = event.currentTarget as HTMLElement
    target.setPointerCapture(event.pointerId)
    pointer = { id: event.pointerId, x: event.clientX, y: event.clientY, target }
    dragging.value = true
    event.preventDefault()
  }

  function moveDrag(event: PointerEvent) {
    if (!pointer || event.pointerId !== pointer.id) return
    offset.value = {
      x: offset.value.x + event.clientX - pointer.x,
      y: offset.value.y + event.clientY - pointer.y
    }
    pointer.x = event.clientX
    pointer.y = event.clientY
  }

  return { zoom, dragging, imageStyle, minZoom, fitZoom, maxZoom, zoomStep, setZoom, reset, wheel, startDrag, moveDrag, endDrag }
}
