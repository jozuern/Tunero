// The tray icons of the demo PC's startup programs, in the order of the startup names in the copy.
import type { IconName } from './icons.ts';

export const startupIcons: IconName[] = ['games', 'chat', 'lightbulb', 'cloud', 'sync', 'music', 'keyboard', 'camera', 'mic'];

/** Startup entries whose programs show toasts while they run: game launcher, cloud sync, updater. */
export const toastingStartup = { launcher: 'startup-1', cloud: 'startup-4', updater: 'startup-5' } as const;
