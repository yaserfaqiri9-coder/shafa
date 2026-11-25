import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'

export function normalizeQueueText(value: unknown) {
  return String(value ?? '').replace(/[۰-۹]/g, digit => String(digit.charCodeAt(0) - 1776))
    .replace(/[٠-٩]/g, digit => String(digit.charCodeAt(0) - 1632))
    .replace(/ي/g, 'ی').replace(/ك/g, 'ک').toLocaleLowerCase().trim()
}

export function createQueueSearch(scope: () => string = () => 'queue') {
  const drafts = reactive<Record<string, string>>({})
  const queries = reactive<Record<string, string>>({})
  const draft = computed({ get: () => drafts[scope()] ?? '', set: value => { drafts[scope()] = value } })
  const applied = computed(() => queries[scope()] ?? '')
  function apply() { queries[scope()] = normalizeQueueText(draft.value) }
  function clear() { drafts[scope()] = ''; queries[scope()] = '' }
  function filterRows<T>(rows: T[]): T[] {
    const query = applied.value
    if (!query) return rows
    const terms = query.split(/\s+/)
    return rows.filter(row => {
      const value = row as Record<string, unknown>
      const haystack = normalizeQueueText([
        value.patientName, value.patientFileNumber, value.fileNumber, value.firstName, value.lastName,
        value.queueNumber, value.orderNumber, value.caseNumber, value.testName, value.surgeryType,
        value.departmentName, value.doctorName, value.reason,
      ].filter(item => item !== null && item !== undefined).join(' '))
      return terms.every(term => haystack.includes(term))
    })
  }
  return { draft, applied, apply, clear, filterRows }
}

export function shiftAt(time: number): 'Morning' | 'Afternoon' {
  const hour = Number(new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kabul', hour: '2-digit', hourCycle: 'h23' }).format(time))
  return hour >= 15 ? 'Afternoon' : 'Morning'
}

/** The reception queue's order: a morning patient still waiting stays ahead of the afternoon cards, both restart at 1. */
export function createReceptionQueue() {
  function visible<T>(rows: T[]): T[] {
    return rows.slice().sort((left, right) => {
      const a = left as { queueNumber?: number | null; queueShift?: string; id?: number; visitId?: number }
      const b = right as typeof a
      return Number(a.queueShift === 'Afternoon') - Number(b.queueShift === 'Afternoon')
        || (a.queueNumber ?? Number.MAX_SAFE_INTEGER) - (b.queueNumber ?? Number.MAX_SAFE_INTEGER)
        || (a.id ?? a.visitId ?? 0) - (b.id ?? b.visitId ?? 0)
    })
  }
  return { visible }
}

type ShiftValue = 'Morning' | 'Afternoon'
type ShiftRow = { queueShift?: ShiftValue | null }
const shiftNames: Record<ShiftValue, string> = { Morning: 'صبح', Afternoon: 'چاشت' }

/**
 * Today's clinical queue holds both shifts. Once the afternoon starts, a morning patient still waiting sits among the
 * afternoon cards; the filter and tag keep them apart without changing the call order the Backend decides.
 */
export function createShiftFilter() {
  const selected = ref<'all' | ShiftValue>('all')
  const now = ref(Date.now())
  let timer: ReturnType<typeof setInterval> | undefined
  onMounted(() => { timer = setInterval(() => { now.value = Date.now() }, 60_000) })
  onBeforeUnmount(() => clearInterval(timer))
  const current = computed(() => shiftAt(now.value))
  const count = (rows: ShiftRow[], shift: ShiftValue) => rows.filter(row => (row.queueShift ?? 'Morning') === shift).length
  const mixed = (rows: ShiftRow[]) => count(rows, 'Morning') > 0 && count(rows, 'Afternoon') > 0
  function apply<T extends ShiftRow>(rows: T[]): T[] {
    if (selected.value === 'all' || !mixed(rows)) return rows
    return rows.filter(row => (row.queueShift ?? 'Morning') === selected.value)
  }
  /** A tag only where both shifts share the list; a morning patient left for the afternoon is marked to stand out. */
  function tag(rows: ShiftRow[], row: ShiftRow, waiting = true) {
    if (!mixed(rows)) return null
    const shift = row.queueShift ?? 'Morning'
    const carried = waiting && shift === 'Morning' && current.value === 'Afternoon'
    return { text: carried ? 'از صبح' : shiftNames[shift], tone: carried ? 'orange' : shift === 'Morning' ? 'cyan' : 'primary' }
  }
  return { selected, current, count, mixed, apply, tag }
}
