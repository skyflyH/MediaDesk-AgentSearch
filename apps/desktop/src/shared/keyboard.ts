export function isControlA(event: KeyboardEvent): boolean {
  return event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey &&
    !event.isComposing && event.key.toLowerCase() === 'a'
}

export function capturedShortcut(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey' | 'isComposing'>): string | null {
  if (event.isComposing || event.key === 'AltGraph') return null
  const key = event.key === ' ' ? 'Space' : event.key.toUpperCase()
  if (!/^(?:[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4])|Space)$/.test(key)) return null
  const modifiers = [event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', event.metaKey && 'Super'].filter(Boolean)
  return modifiers.length ? [...modifiers, key].join('+') : null
}
