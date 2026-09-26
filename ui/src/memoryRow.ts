import type { MemoryItem, RecallHit } from './api'

/**
 * A memory row as the list and the inspector show it. Both views of a memory (the stored record and a recall
 * hit) collapse into this shape so the list has one rendering path.
 */
export interface MemoryRow {
  recordId: string
  tier: string
  scope: string
  body: string
  validity: string
  supersededBy: string | null
  editedBy: string | null
}

export function memoryRowFromItem(item: MemoryItem): MemoryRow {
  return {
    recordId: item.recordId,
    tier: item.tier,
    scope: item.scope,
    body: item.body,
    // The edit model is visible in the list: a superseded record says so instead of vanishing.
    validity:
      item.validTo === null
        ? `有效 · 编辑者 ${item.editedBy ?? '原始'}`
        : `已失效 → ${item.supersededBy ?? '(无后继)'}`,
    supersededBy: item.supersededBy,
    editedBy: item.editedBy,
  }
}

export function memoryRowFromHit(hit: RecallHit): MemoryRow {
  return {
    recordId: hit.recordId,
    tier: 'recall',
    scope: '-',
    body: hit.body,
    validity: `命中 ${hit.total.toFixed(3)} · ${hit.reasons.join(', ')}`,
    supersededBy: null,
    editedBy: null,
  }
}
