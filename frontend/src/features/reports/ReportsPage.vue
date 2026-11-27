<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, downloadFile, errorMessage } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiFilterBar from '@/components/UiFilterBar.vue'
import UiPeriodFilter from '@/components/UiPeriodFilter.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { useHospital } from '@/features/print/paper'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, latinDigits, todayIso } from '@/utils/date'
import { money } from '@/utils/money'
import { kindOf, quickRange, type DateRange, type PeriodKind } from '@/utils/period'
import { reportItems, reportPeriods, reportTabs, type ReportTab } from './reportCatalog'
import { periodWords } from './reportFormat'
import ReportArtDefs from './ReportArtDefs.vue'
import ReportAvatar from './ReportAvatar.vue'
import ReportTableCard from './ReportTableCard.vue'
import type { ReportDocument } from './types'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const { hospital, load: loadHospital } = useHospital()

const tabs = computed(() => reportTabs.filter((tab) => tab.needs.some((need) => auth.can(need))))
/** The address retains the source endpoint for a detail page; the gallery itself has no category tabs. */
const active = computed<ReportTab | null>(() => tabs.value.find((tab) => tab.key === route.query.tab) ?? tabs.value[0] ?? null)
/** The one report open on its own page, or none for the tab's list of reports. */
const item = computed(() => (typeof route.query.item === 'string' && route.query.item ? route.query.item : null))
const isDailySummary = computed(() => auth.can('ledger.view') && !item.value && route.query.view === 'daily')

const isoDate = /^\d{4}-\d{2}-\d{2}$/

/** What the address asks for, or today when it asks for something that makes no sense. */
const selected = computed<DateRange & { kind: PeriodKind }>(() => {
  const { period, from, to } = route.query
  if (period === 'custom' && typeof from === 'string' && typeof to === 'string' && isoDate.test(from) && isoDate.test(to) && from <= to) {
    return { kind: 'custom', from, to }
  }
  const choice = reportPeriods.find((kind) => kind === period && kind !== 'custom') ?? 'today'
  return { kind: choice, ...quickRange(choice as Exclude<PeriodKind, 'custom'>) }
})

function queryFor(tab: string, kind: PeriodKind, range: DateRange, report: string | null = null, view: 'gallery' | 'daily' | null = null) {
  const days = kind === 'custom' ? { period: kind, from: range.from, to: range.to } : { period: kind }
  const query = report ? { tab, item: report, ...days } : { tab, ...days }
  return view ? { ...query, view } : query
}

/** A detail page keeps the endpoint that owns the report, so server-side authorization remains authoritative. */
const openReport = (key: string, tabKey = active.value?.key) => tabKey && router.push({ query: queryFor(tabKey, selected.value.kind, selected.value, key, 'gallery') })
const closeReport = () => active.value && router.push({ query: queryFor(active.value.key, selected.value.kind, selected.value, null, 'gallery') })
const showGallery = () => active.value && router.push({ query: queryFor(active.value.key, selected.value.kind, selected.value, null, 'gallery') })
const showDailySummary = () => active.value && router.push({ query: queryFor(active.value.key, selected.value.kind, selected.value, null, 'daily') })

/** The shared period filter reads and writes the address, so back, forward and reload keep the days. */
const period = computed<DateRange | null>({
  get: () => ({ from: selected.value.from, to: selected.value.to }),
  set: (range) => {
    if (range && active.value) router.push({ query: queryFor(active.value.key, kindOf(range), range, item.value, 'gallery') })
  },
})

const documents = ref<Partial<Record<ReportTab['key'], ReportDocument>>>({})
const doc = computed(() => (active.value ? documents.value[active.value.key] ?? null : null))
interface DailyFinancialSummary { receipts: number; refunds: number; expenses: number }
const dailyDate = ref(todayIso())
const dailyFinancial = ref<DailyFinancialSummary | null>(null)
const loading = ref(false)
const error = ref('')
const loadedAt = ref<string | null>(null)

const daysQuery = () => new URLSearchParams({ from: selected.value.from, to: selected.value.to }).toString()

let controller: AbortController | null = null
async function load() {
  const requested = isDailySummary.value ? [] : item.value ? (active.value ? [active.value] : []) : tabs.value
  if (!isDailySummary.value && !requested.length) return
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  loading.value = true
  error.value = ''
  if (isDailySummary.value) {
    documents.value = {}
    dailyFinancial.value = null
    try {
      const date = encodeURIComponent(dailyDate.value)
      dailyFinancial.value = await api<DailyFinancialSummary>(`/reports/financial?from=${date}&to=${date}`, { signal })
      loadedAt.value = new Date().toISOString()
    } catch (cause) {
      if (signal.aborted) return
      error.value = errorMessage(cause)
    }
    if (signal.aborted) return
    loading.value = false
    return
  }
  dailyFinancial.value = null
  documents.value = {}
  const results = await Promise.allSettled(requested.map(async (tab) => ({
    key: tab.key,
    document: await api<ReportDocument>(`/reports/${tab.key}?${daysQuery()}`, { signal }),
  })))
  if (signal.aborted) return
  const next: Partial<Record<ReportTab['key'], ReportDocument>> = {}
  for (const result of results) if (result.status === 'fulfilled') next[result.value.key] = result.value.document
  documents.value = next
  if (Object.keys(next).length) loadedAt.value = new Date().toISOString()
  const failed = results.find((result) => result.status === 'rejected')
  if (failed?.status === 'rejected') {
    error.value = Object.keys(next).length
      ? 'بارگذاری برخی از گزارش‌ها ممکن نشد؛ برای تلاش دوباره اقدام کنید.'
      : errorMessage(failed.reason)
  }
  loading.value = false
}

// Opening a report reads the same tab document, so its numbers are those of the tab and of its file.
const requestKey = computed(() => {
  if (route.name !== 'reports' || !active.value) return null
  if (isDailySummary.value) return `daily|${dailyDate.value}`
  const source = item.value ? `${active.value.key}|${item.value}` : `gallery|${tabs.value.map((tab) => tab.key).join(',')}`
  return `${source}|${selected.value.from}|${selected.value.to}`
})
watch(requestKey, (key) => key && load(), { immediate: true })

// The address always says what is on screen: a missing or wrong tab or period is written in.
watch(
  () => route.query,
  (query) => {
    if (route.name !== 'reports' || !active.value) return
    const view = query.view === 'daily' && auth.can('ledger.view') && !item.value ? 'daily' : 'gallery'
    const wanted = queryFor(active.value.key, selected.value.kind, selected.value, item.value, view) as Record<string, string>
    const same = Object.keys({ ...wanted, ...query }).every((key) => query[key] === wanted[key])
    if (!same) router.replace({ query: wanted })
  },
  { immediate: true },
)

const warnings = computed(() => doc.value?.warnings ?? [])

const reports = computed(() =>
  (doc.value?.tables ?? []).map((table) => ({ table, info: reportItems[table.key] ?? { art: 'overview', explained: false } })),
)
const galleryReports = computed(() => tabs.value.flatMap((tab) =>
  (documents.value[tab.key]?.tables ?? []).map((table) => ({ tab, table, info: reportItems[table.key] ?? { art: 'overview', explained: false } })),
))
const hasGalleryDocuments = computed(() => Object.keys(documents.value).length > 0)
const current = computed(() => (item.value ? reports.value.find((r) => r.table.key === item.value) ?? null : null))

const canExcel = computed(() => auth.can('reports.export'))
const canPdf = computed(() => auth.can('reports.print') || auth.can('reports.export'))
const canPrint = computed(() => auth.can('reports.print') || auth.can('reports.export'))
const saving = ref<'xlsx' | 'pdf' | null>(null)

/** The server writes the file from the same document as the page, keeping only this report, so the file always matches the screen. */
async function save(format: 'xlsx' | 'pdf') {
  if (!active.value || !current.value || saving.value) return
  const key = current.value.table.key
  saving.value = format
  error.value = ''
  try {
    await downloadFile(`/reports/${active.value.key}/export?format=${format}&item=${encodeURIComponent(key)}&${daysQuery()}`, `report-${key}.${format}`)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = null
  }
}

const print = () => window.print()

interface SyncStatus {
  mode: 'local' | 'mirror'
  enabled: boolean
  lastSuccessAt: string | null
  pending: number | null
}

/** Online, how fresh the copy is; at the hospital, what has not reached the online copy yet. */
const sync = ref<SyncStatus | null>(null)
const STALE_MINUTES = 15
const syncLine = computed<{ text: string; warn: boolean } | null>(() => {
  const status = sync.value
  if (!status) return null
  if (status.mode === 'mirror') {
    if (!status.lastSuccessAt) return { text: 'هنوز معلوماتی از سرور شفاخانه به نسخه آنلاین نرسیده است؛ ارقام این صفحه کامل نیست.', warn: true }
    const minutes = (Date.now() - Date.parse(status.lastSuccessAt)) / 60_000
    return minutes > STALE_MINUTES
      ? { text: `نسخه آنلاین از ${formatDateTime(status.lastSuccessAt)} به این طرف از شفاخانه معلومات نگرفته است؛ معاملات پس از آن در این ارقام نیست.`, warn: true }
      : { text: `نسخه آنلاین · آخرین معلومات از شفاخانه: ${formatDateTime(status.lastSuccessAt)}`, warn: false }
  }
  if (status.enabled && status.pending && status.pending > 0) {
    return { text: `${latinDigits(status.pending)} تغییر هنوز به نسخه آنلاین نرفته است. ارقام این صفحه کامل است؛ نسخه آنلاین تا رسیدن آن‌ها عقب‌تر است.`, warn: false }
  }
  return null
})

async function loadSync() {
  try {
    sync.value = await api<SyncStatus>('/sync/status')
  } catch {
    // The figures stand on their own; a failed check only hides the line.
  }
}

onMounted(() => {
  loadHospital()
  loadSync()
})
onBeforeUnmount(() => controller?.abort())
</script>

<template>
  <div class="report-page">
    <UiCard v-if="!active">
      <p class="xr-state mb-0">هنوز گزارشی برای نقش شما باز نشده است. اگر به گزارشی نیاز دارید، به مدیر سیستم بگویید.</p>
    </UiCard>

    <!-- One report on its own page: its title, its days, its files, and only its own list. -->
    <div v-else-if="item" class="report-main">
      <ReportArtDefs />
      <UiFilterBar :title="current?.table.title ?? 'گزارش'" :subtitle="`${active.title} · ${periodWords(selected.from, selected.to)}`" sticky class="report-bar no-print">
        <UiPeriodFilter v-model="period" label="تاریخ گزارش" :kinds="reportPeriods" />
        <template #actions>
          <button type="button" class="btn btn-soft report-back" @click="closeReport">
            <i class="ri-arrow-right-line" aria-hidden="true"></i> همه گزارش‌ها
          </button>
          <template v-if="current">
            <button v-if="canPrint" type="button" class="report-file report-file--print" :disabled="loading" @click="print">
              <i class="ri-printer-line" aria-hidden="true"></i> چاپ
            </button>
            <button v-if="canExcel" type="button" class="report-file report-file--excel" :disabled="loading || saving !== null" @click="save('xlsx')">
              <i :class="saving === 'xlsx' ? 'ri-loader-4-line report-spin' : 'ri-file-excel-2-line'" aria-hidden="true"></i> Excel
            </button>
            <button v-if="canPdf" type="button" class="report-file report-file--pdf" :disabled="loading || saving !== null" @click="save('pdf')">
              <i :class="saving === 'pdf' ? 'ri-loader-4-line report-spin' : 'ri-file-pdf-2-line'" aria-hidden="true"></i> PDF
            </button>
          </template>
        </template>
      </UiFilterBar>

      <p v-if="syncLine" class="report-sync no-print" :class="{ 'report-sync--warn': syncLine.warn }" role="status">
        <i :class="syncLine.warn ? 'ri-error-warning-line' : 'ri-refresh-line'" aria-hidden="true"></i> {{ syncLine.text }}
      </p>

      <div v-if="error" class="alert alert-danger report-error" role="alert">
        <span>{{ error }}</span>
        <button type="button" class="btn btn-soft-danger btn-sm" @click="load">تلاش دوباره</button>
      </div>

      <p class="visually-hidden" role="status">{{ loading ? 'گزارش در حال آمدن است' : '' }}</p>

      <article v-if="doc && current" class="report-sheet" :class="{ 'report-sheet--busy': loading }" :aria-busy="loading">
        <div class="report-print-head">
          <div v-if="hospital?.hospitalName" class="report-print-head__name">{{ hospital.hospitalName }}</div>
          <div>گرفته‌شده در {{ formatDateTime(loadedAt) }}</div>
        </div>
        <!-- On paper the bar is gone, so the name and days are written once above the list. -->
        <div class="report-print-title">
          <div class="report-head__title">{{ current.table.title }}</div>
          <div class="report-head__period">{{ periodWords(doc.from, doc.to) }}</div>
        </div>

        <template v-if="current.info.explained">
          <div v-for="warning in warnings" :key="warning" class="report-warning" role="note">
            <i class="ri-information-line" aria-hidden="true"></i>
            <span>{{ warning }}</span>
          </div>
        </template>

        <ReportTableCard :table="current.table" bare />
      </article>

      <UiCard v-else-if="doc && !loading">
        <div class="report-missing">
          <p class="mb-0">این گزارش پیدا نشد یا شما به آن دسترسی ندارید.</p>
          <button type="button" class="btn btn-soft" @click="closeReport">بازگشت به گزارش‌ها</button>
        </div>
      </UiCard>

      <div v-else-if="loading" class="report-sheet" aria-hidden="true">
        <div class="report-skeleton-tile"><span class="xr-skeleton xr-skeleton--wide"></span></div>
      </div>
    </div>

    <div v-else-if="isDailySummary" class="report-main report-daily">
      <UiFilterBar title="خلاصه روزانه" :subtitle="periodWords(dailyDate, dailyDate)" sticky class="report-bar no-print">
        <ShamsiDateInput v-model="dailyDate" label="تاریخ مورد نظر" required :max="todayIso()" />
        <template #actions>
          <button type="button" class="btn btn-soft report-back" @click="showGallery">
            <i class="ri-list-check-2" aria-hidden="true"></i> همه گزارش‌ها
          </button>
        </template>
      </UiFilterBar>

      <p v-if="syncLine?.warn" class="report-sync report-sync--warn no-print" role="status">
        <i class="ri-error-warning-line" aria-hidden="true"></i> {{ syncLine.text }}
      </p>

      <div v-if="error" class="alert alert-danger report-error" role="alert">
        <span>{{ error }}</span>
        <button type="button" class="btn btn-soft-danger btn-sm" @click="load">تلاش دوباره</button>
      </div>

      <p class="visually-hidden" role="status">{{ loading ? 'گزارش روزانه در حال بارگذاری است' : '' }}</p>

      <section v-if="dailyFinancial" class="daily-summary" :aria-busy="loading" aria-label="عواید و مصارف روزانه">
        <div class="daily-summary__cards">
          <article class="daily-summary__card daily-summary__card--income">
            <span class="daily-summary__icon" aria-hidden="true"><i class="ri-money-dollar-circle-line"></i></span>
            <div>
              <h2>عواید دریافت‌شده</h2>
              <strong class="daily-summary__amount">{{ money(dailyFinancial.receipts) }}</strong>
              <p>پولی که شفاخانه در این تاریخ واقعاً دریافت کرده است.</p>
            </div>
          </article>
          <article class="daily-summary__card daily-summary__card--expense">
            <span class="daily-summary__icon" aria-hidden="true"><i class="ri-wallet-3-line"></i></span>
            <div>
              <h2>مصارف</h2>
              <strong class="daily-summary__amount">{{ money(dailyFinancial.expenses) }}</strong>
              <p>مصارف ثبت‌شده در همین تاریخ.</p>
            </div>
          </article>
        </div>
        <p v-if="dailyFinancial.refunds > 0" class="daily-summary__refund">
          <i class="ri-arrow-go-back-line" aria-hidden="true"></i>
          پول برگشت‌داده‌شده به مریضان: <strong>{{ money(dailyFinancial.refunds) }}</strong>
        </p>
      </section>

      <div v-else-if="loading" class="daily-summary__loading" role="status">خلاصه این تاریخ در حال آماده‌شدن است…</div>
    </div>

    <!-- One gallery contains every report returned by the endpoints the user is allowed to access. -->
    <div v-else class="report-main">
      <ReportArtDefs />
      <UiFilterBar title="گزارش‌ها" :subtitle="periodWords(selected.from, selected.to)" sticky class="report-bar no-print">
        <UiPeriodFilter v-model="period" label="تاریخ گزارش" :kinds="reportPeriods" />
      </UiFilterBar>

      <p v-if="syncLine" class="report-sync no-print" :class="{ 'report-sync--warn': syncLine.warn }" role="status">
        <i :class="syncLine.warn ? 'ri-error-warning-line' : 'ri-refresh-line'" aria-hidden="true"></i> {{ syncLine.text }}
      </p>

      <div v-if="error" class="alert alert-danger report-error" role="alert">
        <span>{{ error }}</span>
        <button type="button" class="btn btn-soft-danger btn-sm" @click="load">تلاش دوباره</button>
      </div>

      <p class="visually-hidden" role="status">{{ loading ? 'گزارش در حال آمدن است' : '' }}</p>

      <article v-if="hasGalleryDocuments" class="report-sheet" :class="{ 'report-sheet--busy': loading }" :aria-busy="loading">
        <section class="report-list" aria-label="فهرست همه گزارش‌ها">
          <div class="report-grid">
            <button v-if="auth.can('ledger.view')" type="button" class="report-item" @click="showDailySummary">
              <span class="report-item__art"><ReportAvatar name="daily-finance" /></span>
              <span class="report-item__name">خلاصه روزانه</span>
            </button>
            <button v-for="r in galleryReports" :key="`${r.tab.key}-${r.table.key}`" type="button" class="report-item" @click="openReport(r.table.key, r.tab.key)">
              <span class="report-item__art"><ReportAvatar :name="r.info.art" /></span>
              <span class="report-item__name">{{ r.table.title }}</span>
            </button>
          </div>
        </section>
      </article>

      <div v-else-if="loading" class="report-sheet" aria-hidden="true">
        <section class="report-list">
          <div class="report-grid">
            <div v-for="n in 8" :key="n" class="report-skeleton-card"><span class="report-skeleton-card__art xr-skeleton"></span><span class="xr-skeleton xr-skeleton--wide"></span></div>
          </div>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* One calm palette for the reports: the system's teal. */
.report-page {
  --rp-ink: var(--xr-text);
  --rp-soft: var(--xr-text-soft);
  --rp-accent: var(--xr-tone-primary-text);
  --rp-line: var(--xr-row-border);
}

.report-main,
.report-sheet {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 16px;
  align-content: start;
}

.report-daily {
  width: 100%;
  max-width: 1080px;
  margin-inline: auto;
}

.daily-summary {
  display: grid;
  gap: 14px;
}

.daily-summary__cards {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 18px;
}

.daily-summary__card {
  display: flex;
  align-items: flex-start;
  gap: 18px;
  min-width: 0;
  min-height: 190px;
  padding: 26px;
  border: 1px solid var(--rp-line);
  border-inline-start: 5px solid transparent;
  border-radius: 14px;
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.daily-summary__card--income { border-inline-start-color: #17845b; }
.daily-summary__card--expense { border-inline-start-color: #c75b4d; }

.daily-summary__icon {
  display: grid;
  flex: none;
  width: 48px;
  height: 48px;
  place-items: center;
  border-radius: 14px;
  background: #e8f5ef;
  color: #13754f;
  font-size: 24px;
}

.daily-summary__card--expense .daily-summary__icon {
  background: #fff0ed;
  color: #a8473b;
}

.daily-summary__card h2 {
  margin: 0 0 8px;
  color: var(--rp-ink);
  font-size: 16px;
  font-weight: 700;
}

.daily-summary__amount {
  display: block;
  color: var(--rp-ink);
  font-size: clamp(24px, 3vw, 34px);
  font-weight: 800;
  line-height: 1.35;
  overflow-wrap: anywhere;
}

.daily-summary__card p {
  margin: 8px 0 0;
  color: var(--rp-soft);
  font-size: 13px;
  line-height: 1.7;
}

.daily-summary__refund {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--rp-soft);
  font-size: 13px;
}

.daily-summary__refund strong { color: var(--rp-ink); }

.daily-summary__loading {
  padding: 28px;
  border-radius: 12px;
  background: var(--xr-surface);
  color: var(--rp-soft);
  text-align: center;
}

/* The shared bar sits in the page's grid, which already spaces it. */
.report-bar {
  margin-bottom: 0;
}

.report-sync {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--rp-soft);
  font-size: 12.5px;
}

.report-sync--warn {
  padding: 8px 12px;
  border-radius: 8px;
  background: var(--xr-tone-orange-bg);
  color: var(--xr-tone-orange-text);
}

/* Written only on paper: the bar that carries the name on screen is not printed. */
.report-print-title {
  display: none;
}

.report-head__title {
  color: var(--rp-ink);
  font-size: 18px;
  font-weight: 700;
}

.report-head__period {
  color: var(--rp-soft);
  font-size: 12px;
}

.report-back {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

/* Print, Excel and PDF, each in its own colour so they are told apart at a glance. */
.report-file {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 36px;
  padding: 0 14px;
  border: 1px solid;
  border-radius: var(--xr-radius-button);
  background: var(--xr-surface);
  font-size: 13.5px;
  font-weight: 600;
  transition: background-color 0.15s ease;
}

.report-file i {
  font-size: 17px;
}

.report-file--print {
  border-color: var(--xr-primary-light);
  color: var(--xr-tone-primary-text);
}

.report-file--print:hover:not(:disabled) {
  background: var(--xr-tone-primary-bg);
}

.report-file--excel {
  border-color: #b9e2cb;
  color: var(--xr-tone-green-text);
}

.report-file--excel:hover:not(:disabled) {
  background: var(--xr-tone-green-bg);
}

.report-file--pdf {
  border-color: #f2c4c0;
  color: var(--xr-tone-red-text);
}

.report-file--pdf:hover:not(:disabled) {
  background: var(--xr-tone-red-bg);
}

.report-file:disabled {
  opacity: 0.55;
}

.report-file:focus-visible {
  outline: 2px solid var(--rp-accent);
  outline-offset: 2px;
}

.report-spin {
  display: inline-block;
  animation: report-spin 0.8s linear infinite;
}

@keyframes report-spin {
  to {
    transform: rotate(360deg);
  }
}

/* A plain sentence on what a figure cannot show, in a soft amber band. */
.report-warning {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 10px 14px;
  border: 1px solid #f0d48a;
  border-radius: 8px;
  background: #fff8e6;
  color: #5c4300;
  font-size: 13px;
  line-height: 1.8;
}

.report-warning i {
  margin-top: 2px;
  font-size: 17px;
}

/* The reports of a tab: one white sheet, the pictures in a grid of equal cells. */
.report-list {
  padding: 22px 24px 26px;
  border-radius: 12px;
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.report-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
  gap: 16px;
}

/* The gallery entries are image-and-title links, not boxed cards. */
.report-item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  min-width: 0;
  height: 100%;
  padding: 20px 14px 16px;
  border: 0;
  border-radius: 0;
  background: transparent;
  color: var(--rp-ink);
  text-align: center;
  cursor: pointer;
  transition: color 0.15s ease;
}

.report-item:hover {
  color: var(--xr-primary-strong);
}

.report-item:focus-visible {
  outline: 2px solid var(--xr-primary);
  outline-offset: 2px;
}

.report-item__art {
  display: block;
  width: 96px;
  height: 96px;
  margin-bottom: 6px;
}

.report-item__art :deep(img),
.report-item__art :deep(svg) {
  display: block;
  width: 100%;
  height: 100%;
}

.report-item__name {
  font-size: 14.5px;
  font-weight: 700;
  line-height: 1.6;
  overflow-wrap: anywhere;
}

.report-item:hover .report-item__name {
  color: var(--xr-primary-strong);
}

.report-missing {
  display: grid;
  justify-items: center;
  gap: 12px;
  padding: 20px 0;
  color: var(--rp-soft);
  text-align: center;
}

.report-error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: 0;
}

/* The last answer stays on screen while the next one comes, so nothing jumps. The delay keeps a fast answer from flickering. */
.report-sheet {
  transition: opacity 0.15s ease 0.12s;
}

.report-sheet--busy {
  opacity: 0.5;
}

.report-print-head {
  display: none;
}

.report-skeleton-card {
  display: grid;
  justify-items: center;
  align-content: center;
  gap: 12px;
  min-height: 170px;
  padding: 20px 14px;
  border: 1px solid var(--rp-line);
  border-radius: var(--xr-radius-tile);
  background: var(--xr-surface);
}

.report-skeleton-card__art {
  width: 82px;
  height: 82px;
  border-radius: 50%;
}

.report-skeleton-tile {
  display: flex;
  align-items: center;
  min-height: 84px;
  padding: 14px;
  border-radius: 10px;
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

@media (max-width: 575.98px) {
  /* Back, print and the two files: two to a row, the same width. */
  .report-back,
  .report-file {
    flex: 1 1 calc(50% - 8px);
    justify-content: center;
    min-height: 44px;
  }

  .report-list {
    padding: 16px 12px 18px;
  }

  .daily-summary__cards { grid-template-columns: minmax(0, 1fr); gap: 12px; }
  .daily-summary__card { min-height: 0; padding: 20px 16px; gap: 14px; }
  .daily-summary__icon { width: 42px; height: 42px; font-size: 21px; }

  .report-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 10px;
  }

  .report-item {
    padding: 14px 8px 12px;
  }

  .report-item__art {
    width: 72px;
    height: 72px;
  }

  .report-item__name {
    font-size: 13.5px;
  }

}
</style>

<!-- These reach inside shared components, and the print rules must also reach the page around this one. -->
<style>
/*
 * Tables inside a report, kept calm: a pale header band, tall rows split by hairlines, no stripes.
 */
.report-sheet .xr-card {
  border-radius: 10px;
}

.report-sheet .xr-card__body {
  padding: 0 10px 12px;
}

.report-sheet .xr-table {
  color: var(--xr-text);
}

.report-sheet .xr-table th {
  padding: 12px 14px;
  border: 0;
  background: var(--xr-switch-bg);
  color: var(--xr-text);
  font-size: 13px;
  font-weight: 700;
}

.report-sheet .xr-table td {
  height: 46px;
  padding: 10px 14px;
  border-top: 0;
  border-bottom: 1px solid var(--xr-row-border);
  font-size: 13.5px;
}

.report-sheet .xr-table tbody tr:last-child td {
  border-bottom: 0;
}

.report-sheet .xr-table tbody tr:hover {
  background: var(--xr-row-hover);
}

.report-sheet .xr-table tfoot td {
  border-top: 1px solid var(--xr-switch-border);
  border-bottom: 0;
  background: var(--xr-row-hover);
  color: var(--xr-text);
  font-weight: 700;
}

/* A named page, so that the slip and form sizes other print pages set never apply to a report. */
@page report-a4 {
  size: A4 portrait;
  margin: 12mm;
}

@media print {
  .layout__content:has(.report-page) > .alert {
    display: none !important;
  }

  .layout__content:has(.report-page) {
    margin: 0;
  }

  .layout__main:has(.report-page) {
    min-height: 0;
    padding: 0;
    border-radius: 0;
  }

  .layout:has(.report-page) {
    min-height: 0;
  }

  .report-page,
  .report-page .report-main {
    display: block;
  }

  .report-page .report-sheet {
    display: block;
    page: report-a4;
    opacity: 1 !important;
    color: #000;
    font-size: 11px;
  }

  .report-page .report-sheet > * + * {
    margin-top: 14px;
  }

  .report-page .report-print-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
    padding-bottom: 6px;
    border-bottom: 1px solid #000;
    color: #000;
    font-size: 10px;
  }

  .report-page .report-print-head__name {
    font-size: 15px;
    font-weight: 700;
  }

  .report-page .report-print-title {
    display: block;
  }

  .report-page .report-head__title {
    color: #000;
    font-size: 18px;
  }

  .report-page .report-head__period {
    color: #000;
    font-size: 11px;
  }

  .report-page .report-warning {
    border-color: #000;
    background: none;
    color: #000;
    font-size: 10px;
  }

  .report-sheet .xr-card {
    border-radius: 0;
    box-shadow: none;
  }

  .report-sheet .xr-card__body {
    padding: 4px 0 0;
  }

  /* Paper has no scrolling: a table shows in full, and its titles repeat on each page. */
  .report-sheet .xr-table-wrap {
    max-height: none !important;
    overflow: visible !important;
  }

  .report-sheet .xr-table {
    color: #000;
    font-size: 10px;
  }

  .report-sheet .xr-table thead {
    display: table-header-group;
  }

  /* A total belongs once at the end, not at the foot of every page. */
  .report-sheet .xr-table tfoot {
    display: table-row-group;
  }

  .report-sheet .xr-table tr {
    break-inside: avoid;
  }

  .report-sheet .xr-table th,
  .report-sheet .xr-table td {
    position: static !important;
    padding: 3px 4px;
    color: #000;
    font-size: 10px;
    white-space: normal;
  }

  .report-sheet .xr-table th {
    background: none;
  }

  .report-sheet .xr-table td {
    height: auto;
    border-top: 1px solid var(--xr-border-table);
    border-bottom: 0;
  }

  .report-sheet .xr-table tfoot td {
    border-top: 1px solid #000;
    background: none;
  }

  .report-page .report-sheet .report-cell__text {
    width: auto;
    max-width: none;
  }

  .report-page .report-sheet .report-table__note {
    color: #000;
    font-size: 9.5px;
  }
}
</style>
