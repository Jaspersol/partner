import { reactive } from 'vue'

/**
 * Transient confirmations (「已删除」, 「已复制」): a sentence that is true for a few seconds and then is not.
 *
 * Module state rather than provide/inject so a view can say something without being handed a callback, and so
 * a test mounting that view alone still works — nothing is rendered unless a `ToastHost` is on the page.
 */
export interface ToastMessage {
  id: number
  text: string
  tone: 'ok' | 'error'
}

export const toasts = reactive<ToastMessage[]>([])

let next = 1
const LIFETIME_MS = 3200

export function showToast(text: string, tone: ToastMessage['tone'] = 'ok'): void {
  const id = next++
  toasts.push({ id, text, tone })
  setTimeout(() => {
    const at = toasts.findIndex((toast) => toast.id === id)
    if (at >= 0) toasts.splice(at, 1)
  }, LIFETIME_MS)
}
