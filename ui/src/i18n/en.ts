import part1 from './en/part1'
import part2 from './en/part2'
import part3 from './en/part3'

const en: Record<string, string> = {
  ...part1,
  ...part2,
  ...part3,
  'nav.session': 'Chat',
  'nav.schedules': 'Schedules',
  'nav.memory': 'Memory',
  'nav.skills': 'Skills',
  'nav.settings': 'Settings',
  'nav.main': 'Main navigation',
  'nav.views': 'View navigation',
  'nav.expand': 'Expand sidebar',
  'nav.collapse': 'Collapse sidebar',

  'app.unsavedLeave': 'This page has unsaved changes that will be lost if you switch. Continue?',
  'app.connecting': 'Connecting…',
  'app.tokenInvalid': 'Token expired (401) — restart the shell to get a new one',
  'app.cannotConnect': 'Cannot reach the local service — make sure the Partner API is running',

  'topbar.disconnected': 'Disconnected',
  'topbar.queueBlocked': 'Queue blocked: {count} failed',
  'topbar.running': 'Running {id}',
  'topbar.queuePending': '{count} queued',
  'topbar.queueEmpty': 'Queue is empty',
  'topbar.failed': 'Failed {count}',
  'topbar.theme': 'Theme: {name}',
  'topbar.minimize': 'Minimize',
  'topbar.maximize': 'Maximize or restore',
  'topbar.close': 'Close',

  'status.disconnected': 'Connection lost: {error}',
  'status.subJob': 'Sub-task running · {agent} · round {round}/{max}',
  'status.stop': 'Stop',
  'status.stale': 'Read failed · showing the last data received',

  'savebar.save': 'Save',
  'savebar.dirty': 'Unsaved changes',
  'savebar.confirmDiscard': 'Confirm discard',
  'savebar.discard': 'Discard',
  'savebar.cancel': 'Cancel',

  'drawer.close': 'Close',

  'theme.auto': 'System',
  'theme.light': 'Light',
  'theme.dark': 'Dark',

  'settings.section.general': 'General',
  'settings.section.model': 'Chat model',
  'settings.section.input': 'Voice input',
  'settings.section.output': 'Voice output',
  'settings.section.desktop': 'Computer control',
  'settings.section.files': 'File access',
  'settings.section.web': 'Web search',
  'settings.section.engines': 'Local engines',
  'settings.section.appearance': 'Appearance',
  'settings.running': 'Running',
  'settings.general.language': 'Language',
  'settings.general.languageHint': 'The language of the interface. Takes effect immediately.',
  'settings.general.autostart': 'Launch at login',
  'settings.general.autostartHint': 'Start Partner automatically when you sign in to Windows.',
  'settings.general.tray': 'Enable tray icon',
  'settings.general.trayHint': 'When on, closing the window minimizes to the system tray; right-click the tray icon to reopen or quit. When off, closing the window quits.',
  'settings.general.desktopOnly': 'These options are only available in the desktop app.',
  'settings.general.autostartFailed': 'Failed to change launch at login: {error}',
  'settings.general.trayFailed': 'Failed to change the tray setting: {error}',
  'settings.appearance.theme': 'Theme',
  'settings.appearance.themeHint': 'The theme button in the top bar switches it too.',
  'settings.appearance.reduce': 'Reduce transparency',
  'settings.appearance.reduceHint': 'Removes the frosted-glass blur for a crisper, lighter interface. Applies automatically when the system has "reduce transparency" on.',
}

export default en
