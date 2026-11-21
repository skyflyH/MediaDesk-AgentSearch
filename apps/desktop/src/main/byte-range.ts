export function parseRange(header: string | null, size: number): { start: number; end: number } | null | false {
  if (header === null) return null
  const match = /^bytes=(\d*)-(\d*)$/i.exec(header.trim())
  if (!match || !size || (!match[1] && !match[2])) return false
  const first = match[1] ? BigInt(match[1]) : undefined
  const last = match[2] ? BigInt(match[2]) : undefined
  const length = BigInt(size)
  if (first === undefined) {
    if (!last) return false
    return { start: last >= length ? 0 : Number(length - last), end: size - 1 }
  }
  if (first >= length || (last !== undefined && last < first)) return false
  return { start: Number(first), end: last === undefined || last >= length ? size - 1 : Number(last) }
}
