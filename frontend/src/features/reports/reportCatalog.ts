import type { PeriodKind } from '@/utils/period'

export interface ReportTab {
  /** The report's address on the server and the tab's name in the page address. */
  key: 'overview' | 'finance' | 'pharmacy'
  title: string
  icon: string
  /** Any one of these opens the tab; the server checks the same. */
  needs: string[]
}

/** Three reports, no more: the hospital at a glance, money and accounts, pharmacy and store. */
export const reportTabs: ReportTab[] = [
  { key: 'overview', title: 'گزارش عمومی شفاخانه', icon: 'ri-hospital-line', needs: ['reports.view'] },
  { key: 'finance', title: 'گزارش مالی و حسابداری', icon: 'ri-money-dollar-circle-line', needs: ['ledger.view'] },
  { key: 'pharmacy', title: 'گزارش دواخانه و گدام', icon: 'ri-capsule-line', needs: ['inventory.view'] },
]

/** The one filter every report shares. */
export const reportPeriods: PeriodKind[] = ['today', 'yesterday', 'week', 'month', 'custom']

export interface ReportItemInfo {
  /** The report's picture from the gallery artwork (ReportAvatar). */
  art: string
  /** Rests on the tab's definitions and warnings (such as a profit that cannot be known), so it shows them too. */
  explained?: boolean
}

/** Every report of the three tabs, by the key the server gives it. A report the server sends without an entry still opens, with a plain picture. */
export const reportItems: Record<string, ReportItemInfo> = {
  sections: { art: 'patients', explained: true },
  doctors: { art: 'doctors' },
  net: { art: 'income', explained: true },
  cash: { art: 'cash', explained: true },
  registers: { art: 'adjustments' },
  expenses: { art: 'expenses' },
  outstanding: { art: 'outstanding' },
  sales: { art: 'sales', explained: true },
  shortages: { art: 'stock' },
  expiring: { art: 'outstanding' },
  stock: { art: 'overview' },
}
