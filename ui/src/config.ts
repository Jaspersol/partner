/**
 * Where the API lives and which token to present.
 *
 * The token is never baked into the bundle and never read from the URL: the shell injects it, a developer
 * can set it in the environment, and a browser session can keep it in localStorage. Without one the UI
 * refuses to start rather than issuing anonymous requests.
 */

export interface Connection {
  port: number
  token: string
}

export type ConnectionResult =
  | { ok: true; connection: Connection }
  | { ok: false; reason: string }

declare global {
  interface Window {
    /** Injected by the Tauri shell's initialization script. */
    __PARTNER_TOKEN__?: string
    __PARTNER_PORT__?: number
  }
}

const DEFAULT_PORT = 5280
const TOKEN_KEY = 'partner.token'
const PORT_KEY = 'partner.port'

export function resolveConnection(): ConnectionResult {
  // localStorage throws in some sandboxed contexts; that is not a reason to fail startup.
  let storedToken: string | null = null
  let storedPort: string | null = null
  try {
    storedToken = window.localStorage.getItem(TOKEN_KEY)
    storedPort = window.localStorage.getItem(PORT_KEY)
  } catch {
    storedToken = null
    storedPort = null
  }

  const token = window.__PARTNER_TOKEN__ ?? import.meta.env.VITE_PARTNER_TOKEN ?? storedToken ?? ''

  if (token.trim() === '') {
    return {
      ok: false,
      reason:
        '未找到访问令牌。壳启动时会注入；在浏览器里调试请先执行 ' +
        `localStorage.setItem('${TOKEN_KEY}', '<token>')。`,
    }
  }

  const configuredPort = window.__PARTNER_PORT__ ?? import.meta.env.VITE_PARTNER_PORT ?? storedPort

  return { ok: true, connection: { port: Number(configuredPort) || DEFAULT_PORT, token } }
}
