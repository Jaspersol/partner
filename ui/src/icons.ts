/**
 * The icon set (P5 S3).
 *
 * <para>
 * These used to be Unicode glyphs — `◈ ☰ ⌗ ⇄ ❖ ⚙ ⌘ ▤ ▣ ⌕ ⇩ ✎ ⚑`. That was the single most visible source of
 * the app looking unfinished, and not because the shapes were bad: glyphs are drawn by whatever font the
 * system happens to reach for, so they arrived at different weights, different optical sizes and different
 * baselines depending on the machine, none of them matched the type beside them, and none of them could
 * inherit `currentColor` or change stroke weight with context.
 * </para>
 *
 * <para>
 * A library would normally solve this, and adding one is not allowed here (`ui.md §12`, bottom line: no new
 * npm dependencies). So these are written by hand on a 24×24 grid with a single stroke weight, round caps and
 * round joins — which is also why they look like one family rather than fourteen found objects.
 * </para>
 *
 * <para>
 * Each entry is a list of path data because several icons need a second stroke (a box plus its divider, a
 * magnifier plus its handle). Circles are expressed as two arcs rather than `<circle>`, so that one element
 * type is enough to draw everything.
 * </para>
 */

/**
 * The names an icon may be referred to by. Kept as a union so a typo is a type error rather than a blank
 * square that reaches a reader.
 */
export type IconName =
  // navigation
  | 'chat'
  | 'queue'
  | 'memory'
  | 'mcp'
  | 'skill'
  | 'settings'
  // chrome
  | 'panel-left'
  | 'moon'
  | 'sun'
  | 'contrast'
  | 'refresh'
  // tool families
  | 'terminal'
  | 'file'
  | 'pencil'
  | 'search'
  | 'download'
  | 'swap'
  | 'database'
  | 'flag'
  | 'crosshair'
  | 'diamond'
  // small inline marks
  | 'return'
  | 'chevron-down'
  | 'chevron-up'
  | 'star'
  | 'arrow-up'
  | 'arrow-down'
  | 'check'
  | 'square'
  | 'sparkle'
  | 'more'
  | 'plus'

/**
 * Name → path data, in a 24×24 viewBox.
 *
 * Drawn at this size and scaled down by `Icon.vue`, so a 16px icon is a clean 2/3 reduction rather than a
 * 16px design squeezed in. Stroke weight is set once by the component.
 */
export const ICONS: Record<IconName, readonly string[]> = {
  // ---- navigation -------------------------------------------------------------------------------
  // A bubble with a tail: speech, which is what the session log is.
  chat: ['M20 4H4a1 1 0 0 0-1 1v11a1 1 0 0 0 1 1h3v4l5-4h8a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1z'],

  // Three rules, the last one short: a queue is a list with something at the end of it.
  queue: ['M4 6h16M4 12h16M4 18h9'],

  // Two stacked sheets. Memory is records laid over older records, not a brain.
  memory: ['M12 3l9 4.5-9 4.5-9-4.5L12 3z', 'M3 12.5l9 4.5 9-4.5', 'M3 17l9 4.5 9-4.5'],

  // A plug: MCP is subprocesses the model may reach.
  mcp: ['M9 3v5M15 3v5', 'M6 8h12v2a6 6 0 0 1-12 0V8z', 'M12 16v5'],

  // An open book: a skill is a document that gets read.
  skill: ['M12 6.5C10.5 5 8 4.3 4 4.3v13c4 0 6.5.7 8 2.2 1.5-1.5 4-2.2 8-2.2v-13c-4 0-6.5.7-8 2.2z', 'M12 6.5v13'],

  // Sliders, not a cog. A cog is the OS's job; sliders say "these are the settings" without pretending to be
  // machinery, and it is far easier to keep legible at 16px.
  settings: [
    'M4 7h8M16 7h4M4 12h2M10 12h10M4 17h8M16 17h4',
    'M14 7a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
    'M8 12a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
    'M14 17a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
  ],

  // ---- chrome -----------------------------------------------------------------------------------
  // A window with a divider: the sidebar is a region, not an arrow.
  'panel-left': ['M4 5h16v14H4z', 'M10 5v14'],

  moon: ['M20.5 14.6A8.5 8.5 0 0 1 9.4 3.5a8.5 8.5 0 1 0 11.1 11.1z'],

  sun: [
    'M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0z',
    'M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4',
  ],

  // A disc with one half filled: "follow the system" is literally neither of the other two.
  contrast: ['M12 4a8 8 0 1 1 0 16 8 8 0 0 1 0-16z', 'M12 4a8 8 0 0 1 0 16z'],

  // The circular arrow, drawn so the gap is at the top-right where a refresh glyph's usually is.
  refresh: ['M20 12a8 8 0 1 1-2.4-5.7', 'M20 4v4h-4'],

  // ---- tool families ----------------------------------------------------------------------------
  // A prompt, which is what a terminal actually is.
  terminal: ['M4 5h16v14H4z', 'M8 10l2.5 2.5L8 15', 'M13 15h3'],

  // A page with a folded corner.
  file: ['M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z', 'M14 3v5h5'],

  pencil: ['M4 20h4L20 8l-4-4L4 16z', 'M14 6l4 4'],

  search: ['M11 4a7 7 0 1 1 0 14 7 7 0 0 1 0-14z', 'M16.4 16.4L21 21'],

  // An arrow into a tray. `download` is the web family because that is what the tool does; the icon is
  // deliberately not the browser globe, which would claim these calls go out to a page.
  download: ['M12 3v11', 'M8 10l4 4 4-4', 'M4 16v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3'],

  // Two arrows crossing: work handed back and forth.
  swap: ['M4 8h13', 'M14 5l3 3-3 3', 'M20 16H7', 'M10 13l-3 3 3 3'],

  // A cylinder: records that live on a disk and outlive the run.
  database: [
    'M12 3c4.4 0 8 1.3 8 3s-3.6 3-8 3-8-1.3-8-3 3.6-3 8-3z',
    'M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6',
    'M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3',
  ],

  // A flag on a staff.
  flag: ['M5 21V4', 'M5 4h11l-2 4 2 4H5'],

  // A crosshair: acting on the world rather than reading it.
  crosshair: ['M12 2v5M12 17v5M2 12h5M17 12h5', 'M17 12a5 5 0 1 1-10 0 5 5 0 0 1 10 0z'],

  // A plain diamond, for a tool whose family nothing matched. It stays visible on purpose
  // (ui.md §9): an unrecognised tool is the one a reader cannot guess.
  diamond: ['M12 3l9 9-9 9-9-9 9-9z'],

  // ---- inline marks -----------------------------------------------------------------------------
  // The Enter key, which is what this replaces in the send button's label.
  return: ['M9 10l-4 4 4 4', 'M5 14h9a5 5 0 0 0 5-5V5'],

  'chevron-down': ['M6 9.5l6 6 6-6'],
  'chevron-up': ['M6 14.5l6-6 6 6'],

  // Five points, because a gear row means "this is the level the composer offers by default" and a star is
  // the one mark everyone already reads that way.
  star: ['M12 3.5l2.6 5.4 5.9.8-4.3 4.2 1 5.9-5.2-2.8-5.2 2.8 1-5.9-4.3-4.2 5.9-.8z'],

  'arrow-up': ['M12 20V5', 'M6 11l6-6 6 6'],
  'arrow-down': ['M12 4v15', 'M6 13l6 6 6-6'],

  check: ['M5 12.5l4.5 4.5L19 7'],
  square: ['M5 5h14v14H5z'],

  // A four-point sparkle for the reasoning row: quiet, and nothing like the agent family's swap arrows.
  sparkle: ['M12 4l1.6 4.4L18 10l-4.4 1.6L12 16l-1.6-4.4L6 10l4.4-1.6L12 4z'],

  // Horizontal dots: "there is more here", and deliberately not a kebab. A kebab means settings; this means
  // the row's own actions, which are destructive, and they should not look like preferences.
  more: ['M6 12h.01M12 12h.01M18 12h.01'],

  // Two strokes rather than the ＋ character: at rail size the glyph's arms do not meet the vertical cleanly
  // at every weight, and this has to look the same as the other thirty.
  plus: ['M12 5v14M5 12h14'],
}

/** The paths for one icon. Exported so a caller can assert a name actually has geometry. */
export function iconPaths(name: IconName): readonly string[] {
  return ICONS[name]
}