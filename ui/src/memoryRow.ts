import type { MemoryItem, RecallHit } from './api'
import { t, tr } from './i18n'

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
  Working: tr('刚记下 · 还没印证'),
  Episodic: tr('经历 · 反复出现过的事'),
  Semantic: tr('事实 · 可以当事实用'),
  Procedural: tr('做法 · 该怎么做某件事'),
}

/** The short name a chip carries: four words a person can tell apart at a glance. */
const TIER_SHORT: Record<string, string> = {
  Working: tr('刚记下'),
  Episodic: tr('经历'),
  Semantic: tr('事实'),
  Procedural: tr('做法'),
}

/** One sentence per tier, for the ⓘ beside the picker. */
const TIER_HINT: Record<string, string> = {
  Working: tr('刚听到的，还没被印证过。'),
  Episodic: tr('反复出现过的事。'),
  Semantic: tr('可以当事实用的内容。'),
  Procedural: tr('做某件事的步骤和习惯。'),
}

/** What a row's tier means, in the words a person deciding between them would use. */
export function tierLabel(tier: string): string {
  const label = TIER_LABEL[tier]
  return label === undefined ? tier : t(label)
}

export function tierShort(tier: string): string {
  const label = TIER_SHORT[tier]
  return label === undefined ? tier : t(label)
}

export function tierHint(tier: string): string {
  const label = TIER_HINT[tier]
  return label === undefined ? '' : t(label)
}

/**
 * Retention: the only defence the clock does not have.
 *
 * <p>Two values: a third one (shared across sessions) has nothing to share across, since there is one logical
 * session.
 */
export const MEMORY_SCOPES = ['Recall', 'Retain'] as const

const SCOPE_LABEL: Record<string, string> = {
  Recall: tr('普通'),
  Retain: tr('固定'),
}

/** What a row's retention means, in one word. The full promise is spelled out next to the checkbox. */
export function scopeLabel(scope: string): string {
  const label = SCOPE_LABEL[scope]
  return label === undefined ? scope : t(label)
}

function ago(iso: string | null | undefined, now: number): string {
  if (iso === null || iso === undefined || iso === '') {
    return t('从未被召回')
  }

  const at = Date.parse(iso)
  if (Number.isNaN(at)) {
    return t('时间未知')
  }

  const minutes = Math.max(0, Math.floor((now - at) / 60_000))
  if (minutes < 60) {
    return t('{max} 分钟前用过', { max: Math.max(1, minutes) })
  }

  const days = Math.floor(minutes / 1440)
  return days < 1 ? t('{floor} 小时前用过', { floor: Math.floor(minutes / 60) }) : t('{days} 天前用过', { days: days })
}

export function memoryRowFromItem(item: MemoryItem, now = Date.now()): MemoryRow {
  const retired = item.validTo !== null

  return {
    recordId: item.recordId,
    tier: item.tier,
    scope: item.scope,
    body: item.body,
    // Whether this still counts, how long ago it was used, and what replaced it.
    validity: retired
      ? t('已失效 → {successor} · {ago}', { successor: item.supersededBy ?? t('(无后继)'), ago: ago(item.lastRecalledAt, now) })
      : t('有效 · {ago} · 编辑者 {editor}', { ago: ago(item.lastRecalledAt, now), editor: item.editedBy ?? t('原始') }),
    supersededBy: item.supersededBy,
    editedBy: item.editedBy,
    createdAt: item.createdAt,
    lastRecalledAt: item.lastRecalledAt ?? null,
    retired,
    brief: retired ? t('已被 {successor} 取代', { successor: item.supersededBy ?? t('新记录') }) : ago(item.lastRecalledAt, now),
  }
}

/** No `now`: a hit reports why it surfaced, not how old it is, so nothing here needs a clock. */
export function memoryRowFromHit(hit: RecallHit): MemoryRow {
  return {
    recordId: hit.recordId,
    // The record's own tier and scope, not a placeholder: the two filters must match the same record the list
    // shows.
    tier: hit.tier,
    scope: hit.scope,
    body: hit.body,
    // A hit's why-it-surfaced line replaces the age line: recalling it is what just happened, so there is no
    // "last used" to report.
    validity: t('命中 {toFixed} · {join}', { toFixed: hit.total.toFixed(3), join: hit.reasons.join(', ') }),
    supersededBy: null,
    editedBy: null,
    createdAt: hit.createdAt,
    lastRecalledAt: null,
    retired: false,
    brief: t('检索命中'),
  }
}
