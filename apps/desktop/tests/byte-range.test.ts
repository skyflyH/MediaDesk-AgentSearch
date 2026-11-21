import { expect, it } from 'vitest'
import { parseRange } from '../src/main/byte-range'

it.each([
  [null, 100, null],
  ['bytes=0-9', 100, { start: 0, end: 9 }],
  ['bytes=45-', 100, { start: 45, end: 99 }],
  ['bytes=-12', 100, { start: 88, end: 99 }],
  ['bytes=95-99999999999999999999999', 100, { start: 95, end: 99 }],
  ['bytes=-100', 9, { start: 0, end: 8 }],
  ['bytes=10-', 10, false],
  ['bytes=9-1', 10, false],
  ['bytes=0-1,5-6', 10, false],
  ['bytes=-0', 10, false],
  ['bytes=0-', 0, false]
])('parses Range %s against %i bytes', (header, size, expected) => {
  expect(parseRange(header, size)).toEqual(expected)
})
