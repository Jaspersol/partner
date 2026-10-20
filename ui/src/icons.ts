/**
 * Hand-drawn icons on a 24×24 grid: single stroke weight, round caps and joins.
 *
 * No icon library: this project does not add npm dependencies for this.
 *
 * Each entry is a list of path data because several icons need more than one stroke. Circles are expressed as
 * two arcs rather than `<circle>`, so one element type draws everything.
 */

/**
 * The names an icon may be referred to by. Kept as a union so a typo is a type error rather than a blank
 * square that reaches a reader.
 */
export type IconName =
  | 'chat'
  | 'queue'
  | 'memory'
  | 'mcp'
  | 'skill'
  | 'settings'
  | 'prompt'
  | 'panel-left'
  | 'moon'
  | 'sun'
  | 'contrast'
  | 'refresh'
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
  | 'return'
  | 'chevron-down'
  | 'chevron-up'
  | 'star'
  | 'arrow-up'
  | 'arrow-down'
  | 'check'
  | 'square'
  | 'sparkle'
  | 'plus'
  | 'minus'
  | 'info'
  | 'x'
  | 'mic'
  | 'speaker'
  | 'folder'
  | 'clock'
  | 'keyboard'
  | 'paperclip'
  | 'pin'
  | 'copy'
  | 'trash'
  | 'undo'
  | 'shield'
  | 'sliders'

/**
 * Name → path data, in a 24×24 viewBox.
 *
 * Drawn at this size and scaled down by `Icon.vue`, so a 16px icon is a clean 2/3 reduction. Stroke weight is
 * set once by the component.
 */
export const ICONS: Record<IconName, readonly string[]> = {
  chat: ['M20 4H4a1 1 0 0 0-1 1v11a1 1 0 0 0 1 1h3v4l5-4h8a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1z'],

  queue: ['M4 6h16M4 12h16M4 18h9'],

  memory: ['M12 3l9 4.5-9 4.5-9-4.5L12 3z', 'M3 12.5l9 4.5 9-4.5', 'M3 17l9 4.5 9-4.5'],

  mcp: ['M9 3v5M15 3v5', 'M6 8h12v2a6 6 0 0 1-12 0V8z', 'M12 16v5'],

  skill: ['M12 6.5C10.5 5 8 4.3 4 4.3v13c4 0 6.5.7 8 2.2 1.5-1.5 4-2.2 8-2.2v-13c-4 0-6.5.7-8 2.2z', 'M12 6.5v13'],

  settings: [
    'M4 7h8M16 7h4M4 12h2M10 12h10M4 17h8M16 17h4',
    'M14 7a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
    'M8 12a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
    'M14 17a2 2 0 1 1 4 0 2 2 0 0 1-4 0z',
  ],

  prompt: ['M5 4h14v16H5z', 'M8 8h8M8 12h8M8 16h5'],

  'panel-left': ['M4 5h16v14H4z', 'M10 5v14'],

  moon: ['M20.5 14.6A8.5 8.5 0 0 1 9.4 3.5a8.5 8.5 0 1 0 11.1 11.1z'],

  sun: [
    'M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0z',
    'M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4',
  ],

  contrast: ['M12 4a8 8 0 1 1 0 16 8 8 0 0 1 0-16z', 'M12 4a8 8 0 0 1 0 16z'],

  refresh: ['M20 12a8 8 0 1 1-2.4-5.7', 'M20 4v4h-4'],

  terminal: ['M4 5h16v14H4z', 'M8 10l2.5 2.5L8 15', 'M13 15h3'],

  file: ['M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z', 'M14 3v5h5'],

  pencil: ['M4 20h4L20 8l-4-4L4 16z', 'M14 6l4 4'],

  search: ['M11 4a7 7 0 1 1 0 14 7 7 0 0 1 0-14z', 'M16.4 16.4L21 21'],

  download: ['M12 3v11', 'M8 10l4 4 4-4', 'M4 16v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3'],

  swap: ['M4 8h13', 'M14 5l3 3-3 3', 'M20 16H7', 'M10 13l-3 3 3 3'],

  database: [
    'M12 3c4.4 0 8 1.3 8 3s-3.6 3-8 3-8-1.3-8-3 3.6-3 8-3z',
    'M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6',
    'M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3',
  ],

  flag: ['M5 21V4', 'M5 4h11l-2 4 2 4H5'],

  crosshair: ['M12 2v5M12 17v5M2 12h5M17 12h5', 'M17 12a5 5 0 1 1-10 0 5 5 0 0 1 10 0z'],

  diamond: ['M12 3l9 9-9 9-9-9 9-9z'],

  return: ['M9 10l-4 4 4 4', 'M5 14h9a5 5 0 0 0 5-5V5'],

  'chevron-down': ['M6 9.5l6 6 6-6'],
  'chevron-up': ['M6 14.5l6-6 6 6'],

  star: ['M12 3.5l2.6 5.4 5.9.8-4.3 4.2 1 5.9-5.2-2.8-5.2 2.8 1-5.9-4.3-4.2 5.9-.8z'],

  'arrow-up': ['M12 20V5', 'M6 11l6-6 6 6'],
  'arrow-down': ['M12 4v15', 'M6 13l6 6 6-6'],

  check: ['M5 12.5l4.5 4.5L19 7'],
  square: ['M5 5h14v14H5z'],

  sparkle: ['M12 4l1.6 4.4L18 10l-4.4 1.6L12 16l-1.6-4.4L6 10l4.4-1.6L12 4z'],

  plus: ['M12 5v14M5 12h14'],
  minus: ['M5 12h14'],

  info: ['M12 4a8 8 0 1 1 0 16 8 8 0 0 1 0-16z', 'M12 8.2v4.6', 'M12 15.9h.01'],
  x: ['M6 6l12 12M18 6L6 18'],
  mic: ['M12 3a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z', 'M6 11a6 6 0 0 0 12 0', 'M12 17v4'],
  speaker: ['M4 9.5v5h4l5 4v-13l-5 4H4z', 'M16.5 9a4 4 0 0 1 0 6', 'M19 6.5a7.5 7.5 0 0 1 0 11'],
  folder: ['M3 7a2 2 0 0 1 2-2h4l2 2.5h8a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z'],
  clock: ['M12 4a8 8 0 1 1 0 16 8 8 0 0 1 0-16z', 'M12 8v4.5l3 1.8'],
  keyboard: ['M3 7h18v10H3z', 'M7 11h.01M11 11h.01M15 11h.01M7 14h10'],
  paperclip: ['M20 11.5l-7.6 7.6a4.5 4.5 0 0 1-6.4-6.4l8-8a3 3 0 0 1 4.2 4.2l-8 8a1.5 1.5 0 0 1-2.1-2.1l7.3-7.3'],
  pin: ['M9 4h6l-1 6 3 3H7l3-3-1-6z', 'M12 13v7'],
  copy: ['M9 9h10v11H9z', 'M5 15V5a1 1 0 0 1 1-1h9'],
  trash: ['M4 7h16', 'M9 7V4h6v3', 'M6 7l1 13h10l1-13', 'M10 11v6M14 11v6'],
  undo: ['M9 7L4 12l5 5', 'M4 12h10a6 6 0 0 1 0 6'],
  shield: ['M12 3l8 3v6c0 4.5-3.2 7.9-8 9-4.8-1.1-8-4.5-8-9V6l8-3z', 'M9 12l2.2 2.2L15.5 10'],
  sliders: ['M5 8h14M5 16h14', 'M9 6a2 2 0 1 1 0 4 2 2 0 0 1 0-4z', 'M15 14a2 2 0 1 1 0 4 2 2 0 0 1 0-4z'],
}

/** The paths for one icon. Exported so a caller can assert a name actually has geometry. */
export function iconPaths(name: IconName): readonly string[] {
  return ICONS[name]
}