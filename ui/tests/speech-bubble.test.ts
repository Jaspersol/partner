import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

import { bubbleFrame, bubbleText, chooseBubbleLayout } from '../src/pet/speechBubble'

describe('desktop pet speech bubble', () => {
  const input = {
    pet: { x: 700, y: 500, width: 300, height: 360 },
    bubbleWidth: 260,
    bubbleHeight: 72,
    gap: 14,
    workArea: { x: 0, y: 0, width: 1280, height: 720 },
  }

  it('prefers the upper-right corner when it fits', () => {
    expect(chooseBubbleLayout(input)?.placement).toBe('right-top')
  })

  it.each([
    { x: 900, y: 500, placement: 'left-top' },
    { x: 400, y: -100, placement: 'left-bottom' },
    { x: 100, y: -100, placement: 'right-bottom' },
  ])('reselects $placement after moving to ($x, $y)', ({ x, y, placement }) => {
    const pet = { ...input.pet, x, y }
    expect(chooseBubbleLayout({ ...input, pet })?.placement).toBe(placement)
  })

  it('reserves space on either side and shrinks back when speech ends', () => {
    expect(bubbleFrame(input.pet, input.workArea, true)).toMatchObject({ width: 574, modelOffsetX: 0 })
    const moved = { ...input.pet, x: 900 }
    expect(bubbleFrame(moved, input.workArea, true)).toMatchObject({ width: 574, modelOffsetX: 274 })
    expect(bubbleFrame(moved, input.workArea, false)).toEqual({ width: 300, height: 360, modelOffsetX: 0, bubble: null })
  })

  it('does not show a clipped bubble when all four corners overflow', () => {
    const workArea = { ...input.workArea, width: 540 }
    expect(bubbleFrame({ ...input.pet, x: 120 }, workArea, true).bubble).toBeNull()
  })

  it('does not paint the bubble at startup or after its content is cleared', () => {
    const html = readFileSync(join(process.cwd(), 'pet.html'), 'utf8')
    const style = document.createElement('style')
    style.textContent = /<style>([\s\S]*?)<\/style>/.exec(html)?.[1] ?? ''
    const bubble = document.createElement('div')
    bubble.className = 'speech-bubble'
    bubble.hidden = true
    document.head.append(style)
    document.body.append(bubble)
    try {
      expect(getComputedStyle(bubble).display).toBe('none')
      bubble.textContent = '你好'
      bubble.hidden = false
      expect(getComputedStyle(bubble).display).not.toBe('none')
      bubble.hidden = true
      bubble.textContent = ''
      expect(getComputedStyle(bubble).display).toBe('none')
    } finally {
      bubble.remove()
      style.remove()
    }
  })

  it('removes markdown-only syntax before rendering', () => {
    expect(bubbleText('**你好**，[文档](https://example.com)')).toBe('你好，文档')
  })
})
