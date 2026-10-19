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
  /** The one short phrase a card shows: when it was last used, what replaced it, or that it was a search hit. */
  brief: string
}

/** The four tiers, in the order the picker offers them: a person can move a record between any two. */
export const MEMORY_TIERS = ['Working', 'Episodic', 'Semantic', 'Procedural'] as const

const TIER_LABEL: Record<string, string> = {
  Working: '刚记下 · 还没印证',
  Episodic: '经历 · 反复出现过的事',
  Semantic: '事实 · 可以当事实用',
  Procedural: '做法 · 该怎么做某件事',
}

/** The short name a chip carries: four words a person can tell apart at a glance. */
const TIER_SHORT: Record<string, string> = {
  Working: '刚记下',
  Episodic: '经历',
  Semantic: '事实',
  Procedural: '做法',
}

/** One sentence per tier, for the ⓘ beside the picker. */
const TIER_HINT: Record<string, string> = {
  Working: '刚听到的，还没被印证过。',
  Episodic: '反复出现过的事。',
  Semantic: '可以当事实用的内容。',
  Procedural: '做某件事的步骤和习惯。',
}

/** What a row's tier means, in the words a person deciding between them would use. */
export function tierLabel(tier: string): string {
  return TIER_LABEL[tier] ?? tier
}

export function tierShort(tier: string): string {
  return TIER_SHORT[tier] ?? tier
}

export function tierHint(tier: string): string {
  return TIER_HINT[tier] ?? ''
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
  Retain: '固定',
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
    brief: retired ? `已被 ${item.supersededBy ?? '新记录'} 取代` : ago(item.lastRecalledAt, now),
  }
}

/** No `now`: a hit reports why it surfaced, not how old it is, so nothing here needs a clock. */
export function memoryRowFromHit(hit: RecallHit): MemoryRow {
  return {
    recordId: hit.recordId,
    // The record's own tier and scope, not a placeholder. A hit is the same record the list shows, so writing
    // 'recall' and '-' here made the two filters match nothing: a reader who had filtered the list down to
    // retained records saw an empty list and the words 无记忆 right after the app said it had hit one.
    tier: hit.tier,
    scope: hit.scope,
    body: hit.body,
    // A hit's why-it-surfaced line, which is the part a reader checking the ranking needs. It replaces the
    // age line rather than joining it: a hit has no "last used" to report — recalling it is what just happened.
    validity: `命中 ${hit.total.toFixed(3)} · ${hit.reasons.join(', ')}`,
    supersededBy: null,
    editedBy: null,
    createdAt: hit.createdAt,
    lastRecalledAt: null,
    retired: false,
    brief: '检索命中',
  }
}
