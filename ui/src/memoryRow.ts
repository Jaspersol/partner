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
  createdAt: string
  lastRecalledAt: string | null
  /** True once a later statement has retired this record. Still shown: what replaced it is the useful part. */
  retired: boolean
}

/** The four tiers, in the order the picker offers them: a person can move a record between any two. */
export const MEMORY_TIERS = ['Working', 'Episodic', 'Semantic', 'Procedural'] as const

const TIER_LABEL: Record<string, string> = {
  Working: '暂记 · 刚听到，还没印证',
  Episodic: '经过 · 反复出现过的事',
  Semantic: '确定 · 可以当事实用',
  Procedural: '做法 · 该怎么做某件事',
}

/** What a row's tier means, in the words a person deciding between them would use. */
export function tierLabel(tier: string): string {
  return TIER_LABEL[tier] ?? tier
}

/**
 * Retention: the only defence the clock does not have.
 *
 * <p>Two values on purpose. The design doc's third one (shared across sessions) had nothing to share across —
 * there is one logical session — and a value every row already has is a filter that cannot filter.
 */
export const MEMORY_SCOPES = ['Recall', 'Retain'] as const

const SCOPE_LABEL: Record<string, string> = {
  Recall: '普通',
  Retain: '保留',
}

/** What a row's retention means, in one word. The full promise is spelled out next to the checkbox. */
export function scopeLabel(scope: string): string {
  return SCOPE_LABEL[scope] ?? scope
}

function ago(iso: string | null | undefined, now: number): string {
  if (iso === null || iso === undefined || iso === '') {
    return '从未被召回'
  }

  const at = Date.parse(iso)
  if (Number.isNaN(at)) {
    return '时间未知'
  }

  const minutes = Math.max(0, Math.floor((now - at) / 60_000))
  if (minutes < 60) {
    return `${Math.max(1, minutes)} 分钟前用过`
  }

  const days = Math.floor(minutes / 1440)
  return days < 1 ? `${Math.floor(minutes / 60)} 小时前用过` : `${days} 天前用过`
}

export function memoryRowFromItem(item: MemoryItem, now = Date.now()): MemoryRow {
  const retired = item.validTo !== null

  return {
    recordId: item.recordId,
    tier: item.tier,
    scope: item.scope,
    body: item.body,
    // Three things a reader needs and the old one-line form did not carry: whether this still counts, how long
    // ago it was used, and what replaced it. "已编辑" said none of that.
    validity: retired
      ? `已失效 → ${item.supersededBy ?? '(无后继)'} · ${ago(item.lastRecalledAt, now)}`
      : `有效 · ${ago(item.lastRecalledAt, now)} · 编辑者 ${item.editedBy ?? '原始'}`,
    supersededBy: item.supersededBy,
    editedBy: item.editedBy,
    createdAt: item.createdAt,
    lastRecalledAt: item.lastRecalledAt ?? null,
    retired,
  }
}

export function memoryRowFromHit(hit: RecallHit, now = Date.now()): MemoryRow {
  return {
    recordId: hit.recordId,
    tier: 'recall',
    scope: '-',
    body: hit.body,
    // A hit is a recall result, not a stored record: it has no tier, no age and no edit history. What it does
    // have is why it surfaced, which is the part a reader checking the ranking needs.
    validity: `命中 ${hit.total.toFixed(3)} · ${hit.reasons.join(', ')}`,
    supersededBy: null,
    editedBy: null,
    createdAt: new Date(now).toISOString(),
    lastRecalledAt: null,
    retired: false,
  }
}
