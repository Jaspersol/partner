// @vitest-environment node
import { describe, expect, it } from 'vitest'

import { DEFAULT_DICTIONARY, MAX_TEXT_LENGTH, cleanText, matchEmotion, type Dictionary } from '../src/pet/emotion'

const dictionary = DEFAULT_DICTIONARY

describe('longest match first', () => {
  it('counts 不开心 as sadness, not 开心 as a smile', () => {
    const match = matchEmotion('今天有点不开心', dictionary)
    expect(match.emotion).toBe('cry')
    expect(match.hits.map((hit) => hit.text)).toEqual(['不开心'])
  })

  it('counts 讨厌啦 as shyness, not 讨厌 as anger', () => {
    const match = matchEmotion('讨厌啦', dictionary)
    expect(match.emotion).toBe('shy')
    expect(match.hits.map((hit) => hit.text)).toEqual(['讨厌啦'])
    expect(match.totals.angry).toBe(0)
  })

  it('still counts 开心 when it is not part of a longer phrase', () => {
    const match = matchEmotion('很开心', dictionary)
    expect(match.emotion).toBe('smile')
    expect(match.score).toBe(3)
  })
})

describe('negation', () => {
  it('drops a hit that a negation prefix sits in front of', () => {
    // 不 + 烦: 烦 would be anger, and there is no dictionary entry for 不烦, so the hit is dropped entirely.
    const match = matchEmotion('不烦', dictionary)
    expect(match.emotion).toBeNull()
    expect(match.hits).toEqual([])
  })

  it('drops an English hit after a negation word', () => {
    expect(matchEmotion('i am not sad', dictionary).emotion).toBeNull()
    expect(matchEmotion('never angry', dictionary).emotion).toBeNull()
  })

  it('does not treat a letter inside a word as a negation', () => {
    // `cannot` and `nota` contain `not`, but neither is the word `not`.
    expect(matchEmotion('i cannot sad', dictionary).emotion).toBe('cry')
    expect(matchEmotion('this is nota sad thing', dictionary).emotion).toBe('cry')
  })
})

describe('selection', () => {
  it('picks the highest single weight, not the highest sum', () => {
    // 哈哈(3) 好笑(3) sum to 6 for a smile; 害羞(4) alone is shyness with a primary score of 4.
    const match = matchEmotion('哈哈，这个太好笑了，不过被你夸得有点害羞', dictionary)
    expect(match.totals.smile).toBeGreaterThan(match.totals.shy)
    expect(match.emotion).toBe('shy')
    expect(match.score).toBe(4)
  })

  it('breaks a primary-score tie on the sum of matched weights', () => {
    // Equal primaries (3 each), but the smile matched twice: its total wins.
    const tie: Dictionary = {
      keywords: { smile: [{ text: '哈哈', weight: 3 }, { text: '好笑', weight: 3 }], shy: [{ text: '害羞', weight: 3 }] },
      negationPrefixes: [],
      minWeight: 1,
    }
    const match = matchEmotion('哈哈好笑，但也害羞', tie)
    expect(match.emotion).toBe('smile')
    expect(match.totals.smile).toBe(6)
    expect(match.totals.shy).toBe(3)
  })

  it('breaks a full tie on the emotion whose last hit is later', () => {
    // Equal primaries and equal totals: the order decides.
    const own: Dictionary = {
      keywords: {
        smile: [{ text: 'aaa', weight: 2 }],
        angry: [{ text: 'bbb', weight: 2 }],
      },
      negationPrefixes: [],
      minWeight: 1,
    }
    expect(matchEmotion('aaa bbb', own).emotion).toBe('angry')
    expect(matchEmotion('bbb aaa', own).emotion).toBe('smile')
  })

  it('returns no emotion below minWeight', () => {
    const strict: Dictionary = { ...dictionary, minWeight: 5 }
    expect(matchEmotion('不错', strict).emotion).toBeNull()
    expect(matchEmotion('不错', strict).totals.smile).toBe(1)
  })
})

describe('text cleaning', () => {
  it('ignores a keyword inside a fenced code block', () => {
    expect(matchEmotion('看这个：\n```\n生气\n```\n', dictionary).emotion).toBeNull()
  })

  it('ignores a keyword inside inline code', () => {
    expect(matchEmotion('用 `生气` 这个变量', dictionary).emotion).toBeNull()
  })

  it('ignores a keyword inside a URL', () => {
    expect(matchEmotion('see https://example.com/angry/very', dictionary).emotion).toBeNull()
  })

  it('drops a link target but keeps its text', () => {
    const match = matchEmotion('[开心](https://example.com/angry)', dictionary)
    expect(match.emotion).toBe('smile')
    expect(match.totals.angry).toBe(0)
  })

  it('lowercases ASCII without touching CJK', () => {
    expect(cleanText('HAHA 开心')).toBe('haha 开心')
  })

  it('caps the text at 4000 characters', () => {
    const long = `${'x'.repeat(2000)} 生气 ${'y'.repeat(3000)} 害羞`
    const cleaned = cleanText(long)
    expect(cleaned.length).toBe(MAX_TEXT_LENGTH)
    // The 害羞 sits past the cap, so only the anger inside the kept window survives.
    expect(matchEmotion(long, dictionary).emotion).toBe('angry')
  })

  it('answers null for empty and whitespace-only text', () => {
    expect(matchEmotion('', dictionary).emotion).toBeNull()
    expect(matchEmotion('   \n\t', dictionary).emotion).toBeNull()
  })
})

describe('scripts and boundaries', () => {
  it('requires a word boundary for English keywords', () => {
    expect(matchEmotion('sadly', dictionary).emotion).toBeNull()
    expect(matchEmotion('she is sad.', dictionary).emotion).toBe('cry')
  })

  it('matches CJK and emoji as substrings', () => {
    expect(matchEmotion('今天真开心啊', dictionary).emotion).toBe('smile')
    expect(matchEmotion('🎉🎉', dictionary).emotion).toBe('smile')
    expect(matchEmotion('😭', dictionary).emotion).toBe('cry')
  })

  it('reports totals for all four emotions even when a key is absent', () => {
    const sparse: Dictionary = { keywords: { smile: [{ text: 'x', weight: 2 }] }, negationPrefixes: [], minWeight: 1 }
    const match = matchEmotion('x', sparse)
    expect(match.totals).toEqual({ smile: 2, angry: 0, cry: 0, shy: 0 })
  })

  it('ignores a dictionary key this build does not know', () => {
    const future: Dictionary = {
      keywords: { smile: [{ text: 'x', weight: 1 }], puzzled: [{ text: 'y', weight: 5 }] },
      negationPrefixes: [],
      minWeight: 1,
    }
    expect(matchEmotion('y', future).emotion).toBeNull()
    expect(matchEmotion('x y', future).emotion).toBe('smile')
  })
})