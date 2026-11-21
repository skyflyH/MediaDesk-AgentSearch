import { expect, it, vi } from 'vitest'
import { usePhotoTransform } from '../src/renderer/usePhotoTransform'

function viewport() {
  return {
    getBoundingClientRect: () => ({ left: 10, top: 20, width: 400, height: 300 }),
    setPointerCapture: vi.fn(), hasPointerCapture: () => true, releasePointerCapture: vi.fn()
  }
}

it('zooms around the cursor and consumes wheel scrolling only for photos', () => {
  let enabled = true
  const view = usePhotoTransform(() => enabled)
  const event = {
    currentTarget: viewport(), deltaY: -100, deltaMode: 0, clientX: 310, clientY: 170,
    preventDefault: vi.fn(), stopPropagation: vi.fn()
  } as unknown as WheelEvent
  view.wheel(event)
  expect(event.preventDefault).toHaveBeenCalledOnce()
  expect(view.zoom.value).toBeGreaterThan(100)
  // The image point 100px to the right of center stays beneath the cursor.
  const scale = view.zoom.value / 100
  expect(view.imageStyle.value.transform).toBe(`translate(${100 - 100 * scale}px, 0px) scale(${scale})`)
  enabled = false
  view.wheel(event)
  expect(event.preventDefault).toHaveBeenCalledOnce()
})

it('captures dragging, ignores other pointers, and resets pan and zoom between photos', () => {
  const view = usePhotoTransform(() => true)
  const target = viewport()
  const event = (x: number, y: number, id = 1) => ({
    currentTarget: target, button: 0, pointerId: id, clientX: x, clientY: y, preventDefault: vi.fn()
  }) as unknown as PointerEvent
  view.startDrag(event(40, 50))
  expect(target.setPointerCapture).toHaveBeenCalledWith(1)
  view.moveDrag(event(300, 400, 2))
  view.moveDrag(event(70, 30))
  expect(view.imageStyle.value.transform).toBe('translate(30px, -20px) scale(1)')
  expect(view.dragging.value).toBe(true)
  view.setZoom(200)
  view.reset()
  expect(target.releasePointerCapture).toHaveBeenCalledWith(1)
  expect(view.dragging.value).toBe(false)
  expect(view.imageStyle.value.transform).toBe('translate(0px, 0px) scale(1)')
  view.moveDrag(event(100, 100))
  expect(view.imageStyle.value.transform).toBe('translate(0px, 0px) scale(1)')
})

it('keeps wheel zoom within limits and lets the view recover after reaching a limit', () => {
  const view = usePhotoTransform(() => true)
  view.setZoom(10000)
  expect(view.zoom.value).toBe(400)
  view.setZoom(-100)
  expect(view.zoom.value).toBe(50)
  view.setZoom(100)
  expect(view.zoom.value).toBe(100)
})
