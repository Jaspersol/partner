/**
 * Keyword-driven emotion scoring for the desktop pet. Code, URLs and link targets are stripped first because they
 * produce false hits; keywords then match longest-first so a longer phrase owns its characters (`不开心` does not
 * also count as `开心`). Pure and DOM-free: the settings page's trial box runs the same function, so a preview is
 * exactly what the pet will act on.
 */

type Emotion = 'smile' | 'angry' | 'cry' | 'shy'

/** The four emotions, in the order ties fall back to. Unknown dictionary keys are ignored. */
const EMOTIONS: readonly Emotion[] = ['smile', 'angry', 'cry', 'shy']

interface Keyword {
  text: string
  weight: number
}

/**
 * The dictionary as the API stores it. `keywords` is a string map, not `Record<Emotion, …>`: a dictionary written by
 * a later build may carry an emotion this one does not know, and the settings page has to round-trip it.
 */
export interface Dictionary {
  keywords: Record<string, Keyword[]>
  negationPrefixes: string[]
  minWeight: number
}

interface EmotionHit {
  emotion: Emotion
  text: string
  weight: number
  /** Index in the cleaned text, so the trial box can highlight it. */
  at: number
}

export interface EmotionMatch {
  emotion: Emotion | null
  /** The winning emotion's primary score — the highest single weight, not the sum. */
  score: number
  /** Sum of matched weights per emotion. Every known emotion is present, 0 when it matched nothing. */
  totals: Record<Emotion, number>
  hits: EmotionHit[]
}

/** Long replies are cut here: the pet says a line or two, and the API already truncates at the same point. */
export const MAX_TEXT_LENGTH = 4000

/** Words that turn a hit into its opposite. */
const DEFAULT_NEGATION = ['不', '没', '别', '不太', '并不', 'not', 'no', 'never']

/**
 * The built-in table, restored by the settings page's "restore defaults". Kept text-for-text in step with
 * `PetEmotionDictionary.Default`, including weights.
 */
export const DEFAULT_DICTIONARY: Dictionary = {
  keywords: {
    smile: [
      { text: '哈哈', weight: 3 },
      { text: '嘻嘻', weight: 3 },
      { text: '好笑', weight: 3 },
      { text: '开心', weight: 3 },
      { text: '高兴', weight: 3 },
      { text: '太好了', weight: 4 },
      { text: '恭喜', weight: 4 },
      { text: '棒', weight: 2 },
      { text: '不错', weight: 1 },
      { text: '喜欢', weight: 2 },
      { text: '有趣', weight: 2 },
      { text: '😄', weight: 4 },
      { text: '😊', weight: 3 },
      { text: '🎉', weight: 4 },
      { text: 'haha', weight: 3 },
      { text: 'great', weight: 2 },
      { text: 'awesome', weight: 3 },
      { text: 'glad', weight: 2 },
    ],
    angry: [
      { text: '生气', weight: 4 },
      { text: '气死', weight: 5 },
      { text: '可恶', weight: 4 },
      { text: '讨厌', weight: 3 },
      { text: '烦', weight: 2 },
      { text: '不能接受', weight: 3 },
      { text: '过分', weight: 3 },
      { text: '哼', weight: 3 },
      { text: '😠', weight: 4 },
      { text: '😡', weight: 5 },
      { text: 'angry', weight: 4 },
      { text: 'annoying', weight: 3 },
    ],
    cry: [
      { text: '难过', weight: 4 },
      { text: '伤心', weight: 4 },
      { text: '哭', weight: 4 },
      { text: '呜呜', weight: 4 },
      { text: '抱歉', weight: 2 },
      { text: '遗憾', weight: 3 },
      { text: '可惜', weight: 2 },
      { text: '不开心', weight: 3 },
      { text: '失败', weight: 2 },
      { text: '😢', weight: 4 },
      { text: '😭', weight: 5 },
      { text: 'sorry', weight: 2 },
      { text: 'sad', weight: 4 },
      { text: 'unfortunately', weight: 2 },
    ],
    shy: [
      { text: '害羞', weight: 4 },
      { text: '不好意思', weight: 3 },
      { text: '脸红', weight: 4 },
      { text: '过奖', weight: 3 },
      { text: '哪里哪里', weight: 3 },
      { text: '被夸', weight: 3 },
      { text: '讨厌啦', weight: 4 },
      { text: '///', weight: 3 },
      { text: '😳', weight: 4 },
      { text: '☺️', weight: 3 },
      { text: 'blush', weight: 4 },
      { text: 'flattered', weight: 3 },
    ],
  },
  negationPrefixes: [...DEFAULT_NEGATION],
  minWeight: 1,
}

/** ASCII letters only: `toLowerCase()` would also fold non-ASCII letters, changing CJK-adjacent text. */
function lowerAscii(text: string): string {
  return text.replace(/[A-Z]/g, (letter) => letter.toLowerCase())
}

/**
 * The text the scorer searches: fences, inline code, link targets and URLs removed, ASCII folded to lower case, cut
 * to {@link MAX_TEXT_LENGTH}. Removed spans become a space so the text around them does not glue into a new word.
 */
export function cleanText(text: string): string {
  let out = text.replace(/```[\s\S]*?(?:```|$)/g, ' ')
  out = out.replace(/`[^`\n]*`/g, ' ')
  out = out.replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
  out = out.replace(/(?:https?:\/\/|www\.)\S+/gi, ' ')
  out = lowerAscii(out)
  return out.length > MAX_TEXT_LENGTH ? out.slice(0, MAX_TEXT_LENGTH) : out
}

/** English keywords need word boundaries; CJK and emoji are matched as substrings. */
function needsWordBoundaries(keyword: string): boolean {
  return /^[a-z0-9]+$/.test(keyword)
}

function isAsciiWord(character: string): boolean {
  return /^[a-z0-9]$/.test(character)
}

interface Candidate {
  emotion: Emotion
  text: string
  weight: number
  bounded: boolean
}

function collectCandidates(dictionary: Dictionary): Candidate[] {
  const candidates: Candidate[] = []
  const known = new Set<string>(EMOTIONS)
  for (const [emotion, keywords] of Object.entries(dictionary.keywords ?? {})) {
    if (!known.has(emotion) || !Array.isArray(keywords)) continue
    for (const keyword of keywords) {
      const text = lowerAscii(String(keyword.text ?? '').trim())
      if (text === '') continue
      const weight = Number(keyword.weight)
      if (!Number.isFinite(weight)) continue
      candidates.push({ emotion: emotion as Emotion, text, weight, bounded: needsWordBoundaries(text) })
    }
  }
  // Longest first, which is what makes `讨厌啦` win over `讨厌`; equal lengths keep dictionary order.
  candidates.sort((left, right) => right.text.length - left.text.length)
  return candidates
}

/** Every dictionary phrase, used to decide whether a negation is part of a phrase that means something else. */
function allPhrases(dictionary: Dictionary): Set<string> {
  const phrases = new Set<string>()
  for (const keywords of Object.values(dictionary.keywords ?? {})) {
    if (!Array.isArray(keywords)) continue
    for (const keyword of keywords) {
      const text = lowerAscii(String(keyword.text ?? '').trim())
      if (text !== '') phrases.add(text)
    }
  }
  return phrases
}

/**
 * Whether a hit at `at` sits inside the two characters after a negation prefix. English prefixes are words (`never`
 * is five characters), so for them the window is a word boundary plus at most one space instead.
 */
function negationAt(
  clean: string,
  at: number,
  keywordsByLength: readonly string[],
  phrases: Set<string>,
  hitText: string,
): boolean {
  for (const prefix of keywordsByLength) {
    if (!prefix) continue
    for (const gap of [0, 1]) {
      const start = at - gap - prefix.length
      if (start < 0) continue
      if (gap === 1 && clean[at - 1] !== ' ') continue
      if (clean.slice(start, start + prefix.length) !== prefix) continue

      if (needsWordBoundaries(prefix)) {
        // `cannot` is one word: a letter before the prefix means it is not a separate negation.
        if (start > 0 && isAsciiWord(clean[start - 1] as string)) continue
      } else if (prefix.length + gap > 2) {
        // A prefix too long to sit in the two characters before the hit cannot be the one described.
        continue
      }

      // "negation + phrase" being a word of its own means the negation is part of that word: 不开心 is a
      // dictionary entry for sadness, so it is not "开心" negated.
      if (phrases.has(clean.slice(start, at + hitText.length))) return false
      if (gap === 1 && phrases.has(prefix + hitText)) return false
      return true
    }
  }
  return false
}

/** All non-overlapping occurrences of a candidate, in ascending order. */
function occurrences(clean: string, candidate: Candidate, taken: Uint8Array): number[] {
  const found: number[] = []
  const needle = candidate.text
  if (needle.length === 0) return found

  let from = 0
  while (from <= clean.length - needle.length) {
    const at = clean.indexOf(needle, from)
    if (at < 0) break
    from = at + 1

    if (candidate.bounded) {
      const before = at === 0 ? '' : (clean[at - 1] as string)
      const after = clean.charAt(at + needle.length)
      if (isAsciiWord(before) || isAsciiWord(after)) continue
    }

    let free = true
    for (let index = at; index < at + needle.length; index += 1) {
      if (taken[index] !== 0) {
        free = false
        break
      }
    }
    if (!free) continue

    for (let index = at; index < at + needle.length; index += 1) taken[index] = 1
    found.push(at)
  }

  return found
}

/**
 * Scores `text` against `dictionary`. The winner has the highest primary score (its single best weight); ties go to
 * the higher sum of matched weights, then to the emotion whose last hit is later, because an ending sets the tone.
 * No hit, or a primary score below `minWeight`, is no emotion.
 */
export function matchEmotion(text: string, dictionary: Dictionary): EmotionMatch {
  const clean = cleanText(text ?? '')
  const totals: Record<Emotion, number> = { smile: 0, angry: 0, cry: 0, shy: 0 }
  const empty: EmotionMatch = { emotion: null, score: 0, totals, hits: [] }
  if (clean.trim() === '') return empty

  const prefixes = (dictionary.negationPrefixes ?? [])
    .map((prefix) => lowerAscii(String(prefix).trim()))
    .filter((prefix) => prefix !== '')
  const candidates = collectCandidates(dictionary)
  if (candidates.length === 0) return empty

  const phrases = allPhrases(dictionary)
  const taken = new Uint8Array(clean.length)
  const hits: EmotionHit[] = []

  for (const candidate of candidates) {
    for (const at of occurrences(clean, candidate, taken)) {
      if (negationAt(clean, at, prefixes, phrases, candidate.text)) continue
      hits.push({ emotion: candidate.emotion, text: candidate.text, weight: candidate.weight, at })
      totals[candidate.emotion] += candidate.weight
    }
  }

  if (hits.length === 0) return empty
  hits.sort((left, right) => left.at - right.at)

  const primary: Record<Emotion, number> = { smile: 0, angry: 0, cry: 0, shy: 0 }
  const last: Record<Emotion, number> = { smile: -1, angry: -1, cry: -1, shy: -1 }
  for (const hit of hits) {
    if (hit.weight > primary[hit.emotion]) primary[hit.emotion] = hit.weight
    if (hit.at > last[hit.emotion]) last[hit.emotion] = hit.at
  }

  let winner: Emotion | null = null
  for (const emotion of EMOTIONS) {
    if (primary[emotion] === 0) continue
    if (winner === null) {
      winner = emotion
      continue
    }
    if (primary[emotion] > primary[winner]) {
      winner = emotion
    } else if (primary[emotion] === primary[winner]) {
      if (totals[emotion] > totals[winner]) {
        winner = emotion
      } else if (totals[emotion] === totals[winner] && last[emotion] > last[winner]) {
        winner = emotion
      }
    }
  }

  if (winner === null) return empty
  const score = primary[winner]
  if (score < (dictionary.minWeight ?? 1)) {
    return { emotion: null, score: 0, totals, hits }
  }

  return { emotion: winner, score, totals, hits }
}