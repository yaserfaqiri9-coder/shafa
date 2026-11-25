export interface DashboardDay {
  date: string
  completed: number
  active: number
  cancelled: number
  male: number
  female: number
}

export interface DashboardReport {
  today: string
  yesterdayVisits: number
  days: DashboardDay[]
  visitsByDepartment: { name: string; count: number }[]
  todayByStatus: { name: string; count: number }[]
  lowStockProducts: number
}

export interface FinancialReport {
  receipts: number
  expenses: number
  outstandingNow: number
}

export type ReportValueKind = 'count' | 'money' | 'quantity' | 'text' | 'date' | 'datetime'

export type ReportCell = string | number | null

export interface ReportTile {
  label: string
  /** Null when the recorded data cannot give a true figure; shown as «نامعلوم». */
  value: number | null
  kind: ReportValueKind
  note: string | null
  /** One of the at most four figures shown on top of the tab; the rest are lines of the reports below. */
  main: boolean
  /** The report that details this figure, opened by clicking it; only when that report is in this tab. */
  item?: string | null
}

export interface ReportColumn {
  label: string
  kind: ReportValueKind
}

export interface ReportTable {
  title: string
  columns: ReportColumn[]
  rows: ReportCell[][]
  total: ReportCell[] | null
  note: string | null
  /** The report's own name in the address and the export link. */
  key: string
}

export interface ReportTrend {
  title: string
  kind: ReportValueKind
  points: { date: string; value: number }[]
}

/** One report in the single shape the page shows, prints and exports. From and to are null on a report of the present moment. */
export interface ReportDocument {
  title: string
  description: string
  from: string | null
  to: string | null
  tiles: ReportTile[]
  trend: ReportTrend | null
  tables: ReportTable[]
  /** What the figures cannot show, such as a profit that cannot be known. */
  warnings: string[] | null
}
