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
  /** One plain line on what the report shows, for an owner who does not know accounting words. */
  hint: string
  /** Rests on the tab's definitions and warnings (such as a profit that cannot be known), so it shows them too. */
  explained?: boolean
}

/** Every report of the three tabs, by the key the server gives it. A report the server sends without an entry still opens, with a plain picture. */
export const reportItems: Record<string, ReportItemInfo> = {
  sections: { art: 'patients', hint: 'پول واقعاً دریافت‌شده هر بخش', explained: true },
  doctors: { art: 'doctors', hint: 'هر داکتر چند مریض داشت و چند نفر را معاینه کرد' },
  net: { art: 'income', hint: 'از پول دریافت‌شده تا خالص دریافتی، قدم به قدم', explained: true },
  cash: { art: 'cash', hint: 'پول نقدی که طبق اسناد باید نزد شفاخانه باشد', explained: true },
  registers: { art: 'adjustments', hint: 'هر صندوق: باز یا بسته، دریافت و کمی و زیادی' },
  expenses: { art: 'expenses', hint: 'مصارف این دوره به تفکیک نوع' },
  outstanding: { art: 'outstanding', hint: 'فاکتورهایی که مریضان هنوز نپرداخته‌اند' },
  sales: { art: 'sales', hint: 'پول دریافت‌شده دواخانه و عینک، قیمت خرید و سود', explained: true },
  shortages: { art: 'stock', hint: 'اجناسی که تمام شده یا کم مانده است' },
  expiring: { art: 'outstanding', hint: 'اجناسی که تاریخ آن‌ها گذشته یا نزدیک است' },
  stock: { art: 'overview', hint: 'موجودی و ارزش همه اجناس گدام' },
}
