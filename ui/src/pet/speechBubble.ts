/** Placement and text rules for the desktop pet's speech bubble. */

export type BubblePlacement = 'right-top' | 'left-top' | 'left-bottom' | 'right-bottom'

export interface BubbleRect {
  x: number
  y: number
  width: number
  height: number
}

export interface BubbleLayoutInput {
  pet: BubbleRect
  bubbleWidth: number
  bubbleHeight: number
  gap: number
  workArea: BubbleRect | null
}

export interface BubbleLayout {
  placement: BubblePlacement
  x: number
  y: number
}

export interface BubbleFrame {
  width: number
  height: number
  modelOffsetX: number
  bubble: BubbleLayout | null
}

export const BUBBLE_WIDTH = 260
export const BUBBLE_HEIGHT = 72
const BUBBLE_GAP = 14

const ORDER: readonly BubblePlacement[] = ['right-top', 'left-top', 'left-bottom', 'right-bottom']

function position(input: BubbleLayoutInput, placement: BubblePlacement): BubbleLayout {
  const { pet, bubbleWidth, gap } = input
  const rightX = pet.x + pet.width + gap
  const leftX = pet.x - bubbleWidth - gap
  const topY = pet.y
  const bottomY = pet.y + pet.height + gap

  switch (placement) {
    case 'left-top':
      return { placement, x: leftX, y: topY }
    case 'left-bottom':
      return { placement, x: leftX, y: bottomY }
    case 'right-bottom':
      return { placement, x: rightX, y: bottomY }
    case 'right-top':
    default:
      return { placement, x: rightX, y: topY }
  }
}

function fits(layout: BubbleLayout, input: BubbleLayoutInput): boolean {
  const workArea = input.workArea
  if (workArea === null) return true
  return (
    layout.x >= workArea.x &&
    layout.y >= workArea.y &&
    layout.x + input.bubbleWidth <= workArea.x + workArea.width &&
    layout.y + input.bubbleHeight <= workArea.y + workArea.height
  )
}

/** Chooses the first fully visible corner, keeping the requested priority deterministic. */
export function chooseBubbleLayout(input: BubbleLayoutInput): BubbleLayout | null {
  for (const placement of ORDER) {
    const layout = position(input, placement)
    if (fits(layout, input)) return layout
  }
  return null
}

export function bubbleFrame(pet: BubbleRect, workArea: BubbleRect | null, show: boolean): BubbleFrame {
  const layout = show
    ? chooseBubbleLayout({ pet, workArea, bubbleWidth: BUBBLE_WIDTH, bubbleHeight: BUBBLE_HEIGHT, gap: BUBBLE_GAP })
    : null
  if (layout === null) return { width: pet.width, height: pet.height, modelOffsetX: 0, bubble: null }

  const left = Math.min(pet.x, layout.x)
  return {
    width: Math.max(pet.x + pet.width, layout.x + BUBBLE_WIDTH) - left,
    height: Math.max(pet.height, layout.y - pet.y + BUBBLE_HEIGHT),
    modelOffsetX: pet.x - left,
    bubble: { placement: layout.placement, x: layout.x - left, y: layout.y - pet.y },
  }
}

/** Converts a finalized assistant reply into safe, readable bubble text. */
export function bubbleText(body: string): string {
  return body
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/[*_~`>#]/g, '')
    .replace(/[ \t]+/g, ' ')
    .trim()
}
