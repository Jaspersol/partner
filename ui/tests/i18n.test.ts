import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SaveBar from '../src/components/SaveBar.vue'
import { locale, setLocale, t } from '../src/i18n'
import zhCN from '../src/i18n/zh-CN'
import zhTW from '../src/i18n/zh-TW'
import en from '../src/i18n/en'

afterEach(() => {
  setLocale('zh-CN')
  window.localStorage.clear()
})

const placeholders = (text: string) => (text.match(/\{\w+\}/g) ?? []).sort()

/** Every `t('…')` / `tr('…')` literal in the sources: the strings a reader can be shown. */
function usedKeys(): Map<string, string> {
  const found = new Map<string, string>()
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name)
      if (statSync(path).isDirectory()) {
        if (name !== 'i18n') walk(path)
      } else if (/\.(vue|ts)$/.test(name)) {
        const source = readFileSync(path, 'utf8')
        for (const match of source.matchAll(/\btr?\(\s*'((?:\\.|[^'\\])*)'/g)) {
          found.set((match[1] ?? '').replace(/\\(['\\])/g, '$1').replace(/\\n/g, '\n'), path)
        }
      }
    }
  }
  walk(join(__dirname, '..', 'src'))
  return found
}

describe('i18n', () => {
  it('has every dotted key in every language, with the same placeholders', () => {
    for (const key of Object.keys(zhCN)) {
      expect(zhTW[key], `zh-TW: ${key}`).toBeDefined()
      expect(en[key], `en: ${key}`).toBeDefined()
      expect(placeholders(zhTW[key] ?? ''), `zh-TW: ${key}`).toEqual(placeholders(zhCN[key as keyof typeof zhCN]))
      expect(placeholders(en[key] ?? ''), `en: ${key}`).toEqual(placeholders(zhCN[key as keyof typeof zhCN]))
    }
  })

  it('translates every string the sources show, keeping the placeholders', () => {
    const missing: string[] = []
    const mismatched: string[] = []
    for (const [key, file] of usedKeys()) {
      const isChinese = /[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]/.test(key)
      if (!isChinese && !(key in zhCN)) continue
      for (const [name, table] of [['zh-TW', zhTW], ['en', en]] as const) {
        const value = table[key]
        if (value === undefined) missing.push(`${name}: ${key}  (${file})`)
        else if (placeholders(value).join() !== placeholders(key in zhCN ? zhCN[key as keyof typeof zhCN] : key).join()) {
          mismatched.push(`${name}: ${key}`)
        }
      }
    }
    expect(missing).toEqual([])
    expect(mismatched).toEqual([])
  })

  it('interpolates and switches language, remembers it, and sets <html lang>', () => {
    expect(t('topbar.failed', { count: 2 })).toBe('失败 2')
    setLocale('en')
    expect(t('topbar.failed', { count: 2 })).toBe('Failed 2')
    setLocale('zh-TW')
    expect(t('topbar.failed', { count: 2 })).toBe('失敗 2')
    expect(window.localStorage.getItem('partner.locale')).toBe('zh-TW')
    expect(document.documentElement.getAttribute('lang')).toBe('zh-TW')
    expect(locale.value).toBe('zh-TW')
  })

  it('uses the Chinese source text as its own zh-CN message and as the fallback', () => {
    expect(t('已保存 · {at}', { at: '10:00' })).toBe('已保存 · 10:00')
    setLocale('en')
    expect(t('已保存 · {at}', { at: '10:00' })).toBe('Saved · 10:00')
    expect(t('没有这条翻译')).toBe('没有这条翻译')
  })

  it('re-renders mounted components when the language changes', async () => {
    const wrapper = mount(SaveBar, { props: { prefix: 'x', dirty: true } })
    expect(wrapper.text()).toContain('保存')
    setLocale('en')
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('Save')
    expect(wrapper.text()).toContain('Unsaved changes')
  })
})
