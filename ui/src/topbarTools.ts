import type { InjectionKey, Ref } from 'vue'

/**
 * The slot in the top bar that the page on screen may put its own tools into (today: the session view's search).
 *
 * The shell owns the ref and the top bar fills it once mounted; a view that wants the slot teleports into it. A
 * view mounted on its own finds no slot and renders where it stands, which is why the injection has a default
 * rather than being required.
 */
export const TOPBAR_TOOLS: InjectionKey<Ref<HTMLElement | null>> = Symbol('topbar-tools')
