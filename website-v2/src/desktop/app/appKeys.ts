// The texts of the real app (Optimizer.App/Resources/Strings*.resx) that the demo app shows, read at build time.
export const appKeys = [
  'Nav_Dashboard',
  'Nav_Tweaks',
  'Nav_Startup',
  'Nav_Debloat',
  'Nav_Changes',
  'Nav_GroupOptimize',
  'Nav_GroupCleanUp',
  'Intro_Text',
  'Scan_Running',
  'Scan_Probe',
  'Scan_Again',
  'Fix_Apply',
  'Dash_VerdictOne',
  'Dash_VerdictMany',
  'Dash_VerdictNone',
  'Tweaks_Title',
  'Tweak_SwitchOn',
  'Tweak_SwitchOff',
  'Tweaks_GroupApply',
  'Tweaks_GroupCount',
  'Startup_Title',
  'Startup_SignInKinds',
  'Startup_Enabled',
  'Startup_Disabled',
  'Debloat_Title',
  'Debloat_TabStore',
  'Debloat_Remove',
  'Debloat_RemoveName',
  'Debloat_RemovedTitle',
  'Debloat_RemovedEmpty',
  'Changes_Title',
  'Changes_Backups',
  'Changes_UndoAll',
  'Changes_LogEmpty',
  'Changes_Entries',
  'Result_UndoAll',
  'Tweak_Undo',
  'Impact_Short',
] as const;

export type AppStrings = Record<(typeof appKeys)[number], string>;

/** Fills {0}, {1} in an app text. */
export const fill = (text: string, ...values: (string | number)[]) => text.replace(/\{(\d)\}/g, (_, index: string) => String(values[Number(index)]));

export type Page = 'overview' | 'tweaks' | 'startup' | 'apps' | 'changes';
