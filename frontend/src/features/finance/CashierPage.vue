<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { usePagedList } from '@/api/paged'
import UiPager from '@/components/UiPager.vue'
import { useLiveRefresh } from '@/api/realtime'
import UiModal from '@/components/UiModal.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import { useAuthStore } from '@/stores/auth'
import UiRowMenu from '@/components/UiRowMenu.vue'
import { formatDateTime, todayIso } from '@/utils/date'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { money, quantity } from '@/utils/money'
import { invoiceSubject, type InvoiceDetail, type InvoiceRow, type PaymentRow, type SessionRow } from './types'

const auth = useAuthStore()
const tab = ref<'invoices' | 'receipts'>('invoices')
const dayDetails = ref(false)
const date = ref<string | null>(todayIso())
const allOutstanding = ref(false)
const historical = ref(false)
const invoiceList = usePagedList<InvoiceRow>(() => `/invoices?unpaid=true&q=${encodeURIComponent(q.value)}${!allOutstanding.value ? `&from=${day.value}&to=${day.value}` : ''}`, 15)
const invoices = invoiceList.rows
const day = computed(() => date.value ?? todayIso())
interface DaySummary { receipts: number; cashReceipts: number; otherReceipts: number; refunds: number; expenses: number; netReceived: number; sessions: SessionRow[]; unassignedCashOut: number }
const summary = ref<DaySummary | null>(null)
interface RefundRow { id: number; refundNumber: string; receiptNumber: string; amount: number; reason: string; createdAt: string; patientName: string | null }
const refundList = usePagedList<RefundRow>(() => `/refunds?from=${day.value}&to=${day.value}&q=${encodeURIComponent(q.value)}`, 10)
const historyList = usePagedList<{ id: number; invoiceNumber: string; patientName: string | null; balance: number; createdAt: string; description: string | null }>(() => `/cash/outstanding-at?date=${day.value}&q=${encodeURIComponent(q.value)}`, 15)
const sessions = ref<SessionRow[]>([])
const registers = ref<{ id: number; name: string }[]>([])
const q = ref('')
const loading = ref(true)
/** What an action of the cashier refused. Kept apart from list loading, so a background refresh never wipes it before it is read. */
const error = ref('')
const loadError = ref('')

const openSession = computed(() => sessions.value.find((s) => !s.closedAt) ?? null)
const deskActions = computed(() => {
  const items = [{ label: 'جزئیات روز', icon: 'ri-file-list-line', onSelect: () => { dayDetails.value = true } }]
  if (openSession.value && auth.can('cash.edit')) items.push({ label: 'تحویل پول', icon: 'ri-bank-line', onSelect: () => {
    Object.assign(transfer, { open: true, amount: 0, reason: '', error: '', version: openSession.value!.version, key: newIdempotencyKey() })
  } })
  if (auth.can('expenses.view') || auth.can('ledger.view')) items.push({ label: 'مصارف و حسابداری', icon: 'ri-book-line', onSelect: () => {
    window.location.assign('/accounting?tab=expenses')
  } })
  return items
})


async function loadInvoices() {
  try { await invoiceList.load(); loadError.value = '' }
  catch (cause) { loadError.value = errorMessage(cause) }
  finally { loading.value = false }
}

async function loadSessions() {
  sessions.value = await api<SessionRow[]>('/cash/sessions?mine=true')
}

/** Receipts of the selected day remain available after the drawer closes. */
const receiptList = usePagedList<PaymentRow>(() => `/payments?from=${day.value}&to=${day.value}&q=${encodeURIComponent(q.value)}`, 15)
const showReceipts = computed(() => auth.can('payments.view'))

async function loadReceipts() {
  if (showReceipts.value) await receiptList.load()
  else receiptList.rows.value = []
}

async function refresh() {
  try {
    await Promise.all([loadInvoices(), loadSessions(), refundList.load(), historical.value ? historyList.load() : Promise.resolve(), api<DaySummary>(`/cash/day?date=${day.value}`).then((value) => { summary.value = value })])
    await loadReceipts()
  } catch (cause) {
    loadError.value = errorMessage(cause)
  }
}

const opening = reactive({ registerId: null as number | null, amount: 0, saving: false })

async function open() {
  opening.saving = true
  error.value = ''
  try {
    await api('/cash/sessions', { method: 'POST', body: { registerId: opening.registerId, openingAmount: opening.amount } })
    await refresh()
  } catch (cause) {
    // A register has one open session at a time; the plain duplicate message would not say what to do.
    error.value = cause instanceof ApiError && cause.code === 'duplicate'
      ? 'این صندوق را کارمند دیگری باز کرده است. صندوق دیگری انتخاب کنید یا از او بخواهید صندوق را ببندد.'
      : errorMessage(cause)
  } finally {
    opening.saving = false
  }
}

const closing = reactive({ open: false, target: null as SessionRow | null, counted: 0, note: '', saving: false, error: '' })
const closingSession = computed(() => closing.target ?? openSession.value)
function startClose(session: SessionRow) {
  Object.assign(closing, { open: true, target: session, counted: session.currentExpected, note: '', error: '' })
}

async function close() {
  closing.saving = true
  closing.error = ''
  try {
    await api(`/cash/sessions/${closingSession.value!.id}/close`, { method: 'POST', body: { countedAmount: closing.counted, note: closing.note, version: closingSession.value!.version } })
    closing.open = false
    await refresh()
  } catch (cause) {
    closing.error = errorMessage(cause)
  } finally {
    closing.saving = false
  }
}

const pay = reactive({ detail: null as InvoiceDetail | null, amount: 0, discount: 0, discountMode: 'Amount' as 'Amount' | 'Percent', saving: false, error: '', key: '', receipt: null as PaymentRow | null })
const payDiscountAmount = computed(() => {
  if (!pay.detail || pay.discount <= 0) return 0
  const base = pay.detail.invoice.total
  return pay.discountMode === 'Percent' ? Math.round(base * pay.discount) / 100 : pay.discount
})
const payFinal = computed(() => Math.max(0, (pay.detail?.invoice.balance ?? 0) - payDiscountAmount.value))
const payHasExistingDiscount = computed(() => (pay.detail?.invoice.discount ?? 0) > 0)
// The cashier normally settles the whole discounted balance; a partial amount can still be entered deliberately.
watch(payFinal, (amount) => {
  if (pay.detail && !pay.receipt) pay.amount = amount
})

async function openPayment(row: Pick<InvoiceRow, 'id' | 'balance'>) {
  Object.assign(pay, { detail: null, amount: row.balance, discount: 0, discountMode: 'Amount', saving: false, error: '', key: newIdempotencyKey(), receipt: null })
  try {
    pay.detail = await api<InvoiceDetail>(`/invoices/${row.id}`)
    pay.amount = pay.detail.invoice.balance
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

async function submitPayment() {
  pay.saving = true
  pay.error = ''
  try {
    pay.receipt = await api<PaymentRow>('/payments/settle', {
      method: 'POST',
      idempotencyKey: pay.key,
      body: { invoiceId: pay.detail!.invoice.id, amount: pay.amount, invoiceVersion: pay.detail!.invoice.version, discount: pay.discount > 0 ? { amount: pay.discount, mode: pay.discountMode } : null },
    })
    await refresh()
  } catch (cause) {
    pay.error = errorMessage(cause)
    // A definite refusal may be retried with new values; an unknown outcome keeps the key so a retry cannot double-charge.
    if (cause instanceof ApiError && !cause.outcomeUnknown) pay.key = newIdempotencyKey()
    if (cause instanceof ApiError && (cause.code === 'invoice_changed' || cause.code === 'concurrency_conflict')) {
      pay.detail = await api<InvoiceDetail>(`/invoices/${pay.detail!.invoice.id}`)
      pay.amount = pay.detail.invoice.balance
      pay.discount = 0
    }
  } finally {
    pay.saving = false
  }
}

const voiding = reactive({ row: null as InvoiceRow | null, reason: '', saving: false, error: '' })

async function submitVoid() {
  voiding.saving = true
  voiding.error = ''
  try {
    await api(`/invoices/${voiding.row!.id}/void`, { method: 'POST', body: { reason: voiding.reason, version: voiding.row!.version } })
    voiding.row = null
    await loadInvoices()
  } catch (cause) {
    voiding.error = errorMessage(cause)
  } finally {
    voiding.saving = false
  }
}

/** No net receipt remains: cancellation reverses the charge and any discount together. */
const canDrop = (row: InvoiceRow) => row.paid - row.refunded === 0 && auth.can('invoices.cancel')
const dropReason = 'مریض پرداخت نکرد'
function startDrop(row: InvoiceRow) {
  Object.assign(voiding, { row, reason: dropReason, error: '' })
}

function cashierRowItems(row: InvoiceRow) {
  const items: { label: string; ariaLabel?: string; icon?: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[] = []
  if (auth.can('invoices.print')) items.push({ label: 'فاکتور', icon: 'ri-printer-line', onSelect: () => { window.open(`/print/invoice/${row.id}`, '_blank', 'noopener') } })
  if (canDrop(row) && row.correctable && auth.can('invoices.create')) {
    items.push({ label: 'اصلاح', icon: 'ri-edit-line', onSelect: () => { window.location.assign(`/cashier/invoice?correct=${encodeURIComponent(row.id)}`) } })
  }
  return items
}

const adjustment = reactive({ row: null as PaymentRow | null, cancel: false, amount: 0, reason: '', saving: false, error: '', key: '' })
const transfer = reactive({ open: false, amount: 0, destination: 'Vault', reason: '', version: 0, saving: false, error: '', key: '' })
async function saveTransfer() {
  transfer.saving = true
  transfer.error = ''
  try {
    await api(`/cash/sessions/${openSession.value!.id}/transfer`, { method: 'POST', idempotencyKey: transfer.key,
      body: { amount: transfer.amount, destination: transfer.destination, reason: transfer.reason, version: transfer.version } })
    transfer.open = false
    await refresh()
  } catch (cause) {
    transfer.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) transfer.key = newIdempotencyKey()
  } finally { transfer.saving = false }
}
function openAdjustment(row: PaymentRow, cancel: boolean) {
  Object.assign(adjustment, { row, cancel, amount: row.amount, reason: '', saving: false, error: '', key: newIdempotencyKey() })
}
async function saveAdjustment() {
  adjustment.saving = true
  adjustment.error = ''
  try {
    await api(`/payments/${adjustment.row!.id}/${adjustment.cancel ? 'cancel' : 'correct'}`, {
      method: 'POST', idempotencyKey: adjustment.key,
      body: { amount: adjustment.amount, reason: adjustment.reason, version: adjustment.row!.version },
    })
    adjustment.row = null
    await refresh()
  } catch (cause) {
    adjustment.error = errorMessage(cause)
    if (cause instanceof ApiError && !cause.outcomeUnknown) adjustment.key = newIdempotencyKey()
    if (cause instanceof ApiError && cause.status === 409 && !cause.outcomeUnknown) {
      error.value = adjustment.error
      adjustment.row = null
      await refresh()
    }
  } finally { adjustment.saving = false }
}
watch([day, allOutstanding, historical], async () => {
  invoiceList.page.value = 1; receiptList.page.value = 1; refundList.page.value = 1; historyList.page.value = 1
  await refresh()
})
let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(() => { invoiceList.page.value = 1; receiptList.page.value = 1; refundList.page.value = 1; historyList.page.value = 1; refresh() }, 350)
})

useLiveRefresh(['InvoiceIssued', 'PaymentReceived', 'InventoryChanged', 'CashChanged', 'VisitStatusChanged'], refresh)
onMounted(async () => {
  try {
    registers.value = await api<typeof registers.value>('/cash/registers')
    opening.registerId = registers.value[0]?.id ?? null
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await refresh()
})
onBeforeUnmount(() => clearTimeout(debounce))
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0 d-flex align-items-center justify-content-between gap-2" role="alert">
      <span>{{ error }}</span>
      <button type="button" class="btn-close" aria-label="بستن پیام" @click="error = ''"></button>
    </div>
    <div v-if="loadError" class="alert alert-danger mb-0" role="alert">{{ loadError }}</div>

    <section class="dq-page">
      <h1 class="visually-hidden">صندوق</h1>
      <!-- One row: the lists, the open register in brief, and the desk's buttons. -->
      <div class="cash-bar">
        <div class="xr-tabs dq-tabs cash-bar__tabs" role="tablist" aria-label="کارهای صندوق">
          <button id="cash-invoices-tab" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'invoices' }" :aria-selected="tab === 'invoices'" aria-controls="cash-invoices" @click="tab = 'invoices'">
            فاکتورهای ارجاع‌شده <span v-if="invoiceList.total.value" class="xr-tab__count">{{ quantity(invoiceList.total.value) }}</span>
          </button>
          <button v-if="showReceipts" id="cash-receipts-tab" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'receipts' }" :aria-selected="tab === 'receipts'" aria-controls="cash-receipts" @click="tab = 'receipts'">
            دریافت‌شده‌ها <span v-if="receiptList.total.value" class="xr-tab__count">{{ quantity(receiptList.total.value) }}</span>
          </button>
        </div>
        <div v-if="openSession" class="cash-bar__session" :title="`${openSession.registerName} · باز از ${formatDateTime(openSession.openedAt)} · شروع ${money(openSession.openingAmount)}`">
          <span class="cash-bar__dot" aria-hidden="true"></span>
          <strong>{{ openSession.registerName }}</strong>
          <span>موجودی {{ money(openSession.currentExpected) }}</span>
        </div>
        <span v-else-if="!auth.can('cash.create')" class="xr-badge xr-badge--orange">صندوق بسته است</span>
        <div class="cash-bar__actions">
          <RouterLink v-if="auth.can('invoices.create')" :to="{ name: 'invoice-new' }" class="btn btn-soft"><i class="ri-add-line" aria-hidden="true"></i> فاکتور مستقل</RouterLink>
          <button v-if="openSession && auth.can('cash.edit')" type="button" class="btn btn-quiet" @click="startClose(openSession)">بستن صندوق</button>
          <UiRowMenu label="گزینه‌های صندوق" :items="deskActions" />
        </div>
      </div>
      <form v-if="!openSession && auth.can('cash.create')" class="dq-chosen cash-open" aria-label="صندوق" @submit.prevent="open">
        <span class="xr-badge xr-badge--orange">صندوق بسته است</span>
        <select v-model.number="opening.registerId" class="form-select" aria-label="صندوق" required>
          <option v-for="r in registers" :key="r.id" :value="r.id">{{ r.name }}</option>
        </select>
        <label class="cash-open__amount">موجودی شروع
          <input v-model.number="opening.amount" class="form-control" type="number" min="0" step="0.01" required />
        </label>
        <button type="submit" class="btn btn-primary" :disabled="opening.saving">باز کردن صندوق</button>
      </form>

      <div class="dq-filters cash-filters">
        <label class="dq-search">
          <i class="ri-search-line" aria-hidden="true"></i>
          <input v-model="q" type="search" aria-label="جستجوی مالی" placeholder="جستجو: نام، پرونده، فاکتور یا رسید" />
          <button v-if="q" type="button" class="dq-search__clear" aria-label="پاک کردن جستجو" @click="q = ''"><i class="ri-close-line" aria-hidden="true"></i></button>
        </label>
        <ShamsiDateInput v-model="date" label="تاریخ" required />
        <label v-if="tab === 'invoices'" class="cash-filters__all"><input v-model="allOutstanding" type="checkbox" /> تمام روزها</label>
      </div>
      <section v-if="tab === 'invoices'" id="cash-invoices" role="tabpanel" aria-labelledby="cash-invoices-tab">
        <UiListSkeleton v-if="loading" />
        <div v-else-if="invoices.length === 0" class="xr-state">فاکتور قابل دریافتی نیست.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table cash-table" aria-label="فاکتورهای قابل دریافت">
            <thead>
              <tr><th>تاریخ</th><th>نام مریض</th><th>بخش</th><th>بابت</th><th>مبلغ</th><th><span class="visually-hidden">عملیات</span></th></tr>
            </thead>
            <tbody>
              <tr v-for="row in invoices" :key="row.id">
                <td class="text-nowrap">{{ formatDateTime(row.createdAt) }}</td>
                <td>
                  <strong>{{ row.patientName ?? 'بدون مریض' }}</strong>
                  <span class="cash-table__sub">فاکتور {{ row.invoiceNumber }}</span>
                  <span v-if="row.queueNumber" class="cash-table__sub">نوبت {{ quantity(row.queueNumber) }}</span>
                </td>
                <td>{{ row.section ?? '—' }}</td>
                <td>{{ invoiceSubject(row) }}</td>
                <td class="text-nowrap">
                  <span class="dq__money">{{ money(row.balance) }}</span>
                  <span v-if="row.paid - row.refunded > 0" class="cash-table__sub">پرداخت‌شده {{ money(row.paid - row.refunded) }}</span>
                  <span v-if="row.paid - row.refunded > 0 && row.balance > 0" class="cash-table__sub">پرداخت ناقص</span>
                </td>
                <td>
                  <div class="dq__actions">
                    <button v-if="auth.can('payments.create')" type="button" class="btn btn-primary" :disabled="!openSession" :title="openSession ? undefined : 'اول صندوق را باز کنید'" @click="openPayment(row)">دریافت</button>
                    <button v-if="canDrop(row)" type="button" class="btn btn-soft-danger" @click="startDrop(row)">پرداخت نکرد</button>
                    <UiRowMenu v-if="cashierRowItems(row).length" :label="`عملیات فاکتور ${row.invoiceNumber}`" :items="cashierRowItems(row)" />
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="invoiceList.page.value" :page-count="invoiceList.pageCount.value" @go="invoiceList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </section>

      <section v-if="tab === 'receipts' && showReceipts" id="cash-receipts" role="tabpanel" aria-labelledby="cash-receipts-tab">
        <div v-if="receiptList.rows.value.length === 0" class="xr-state">در روز و جستجوی منتخب دریافتی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table cash-table" aria-label="دریافت‌های روز منتخب">
            <thead>
              <tr><th>تاریخ</th><th>نام مریض</th><th>بخش</th><th>بابت</th><th>مبلغ</th><th><span class="visually-hidden">عملیات</span></th></tr>
            </thead>
            <tbody>
              <tr v-for="row in receiptList.rows.value" :key="row.id">
                <td class="text-nowrap">{{ formatDateTime(row.createdAt) }}</td>
                <td>
                  <strong>{{ row.patientName ?? 'بدون مریض' }}</strong>
                  <span v-if="row.queueNumber" class="cash-table__sub">نوبت {{ quantity(row.queueNumber) }}</span>
                </td>
                <td>{{ row.section ?? '—' }}</td>
                <td>{{ invoiceSubject({ description: row.description ?? null, itemCount: row.itemCount ?? 0 }) }}</td>
                <td class="text-nowrap">
                  <strong>{{ money(row.amount) }}</strong>
                  <span class="cash-table__sub">{{ row.method === 'Cash' ? 'نقد' : 'غیرنقدی' }}</span>
                  <span class="cash-table__sub">رسید {{ row.receiptNumber }} · فاکتور {{ row.invoiceNumber }}</span>
                  <span v-if="row.refunded" class="cash-table__sub dq__warn" title="مجموع برگشت این رسید تا اکنون؛ برگشت روز منتخب در فهرست جدا آمده است">برگشت {{ money(row.refunded) }} · خالص {{ money(row.amount - row.refunded) }}</span>
                  <span v-if="row.replacementPaymentId" class="cash-table__sub">اصلاح‌شده</span>
                </td>
                <td>
                  <div class="dq__actions"><RouterLink v-if="auth.can('payments.print')" :to="`/print/receipt/${row.id}`" target="_blank" class="btn btn-soft">چاپ دوباره</RouterLink>
                    <template v-if="auth.can('payments.refund') && !row.refunded"><button type="button" class="btn btn-soft" @click="openAdjustment(row, false)">اصلاح</button><button type="button" class="btn btn-soft-danger" @click="openAdjustment(row, true)">لغو دریافت</button></template>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="receiptList.page.value" :page-count="receiptList.pageCount.value" @go="receiptList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </section>
    </section>
  </div>

  <UiModal v-if="dayDetails" title="جزئیات روز" @close="dayDetails = false; historical = false">
      <div v-if="summary" class="cash-day-summary" aria-label="خلاصه روز منتخب">
        <span>دریافتی <strong>{{ money(summary.receipts) }}</strong></span>
        <span>برگشت پول <strong>{{ money(summary.refunds) }}</strong></span>
        <span>مصارف <strong>{{ money(summary.expenses) }}</strong></span>
        <span>خالص روز <strong>{{ money(summary.netReceived) }}</strong></span>
        <small v-if="summary.otherReceipts">غیرنقدی: {{ money(summary.otherReceipts) }}</small>
      </div>
      <p v-if="summary?.unassignedCashOut" class="alert alert-warning" role="status">گردش نقدی خارج از صندوق شخصی: {{ money(summary.unassignedCashOut) }}. این مبلغ در شمارش صندوق شما منظور نشده است.</p>
      <details class="mt-3"><summary>برگشت‌های روز منتخب ({{ quantity(refundList.total.value) }})</summary>
        <p class="form-hint">به تاریخ برگشت پول، حتی اگر رسید اصلی مربوط روز دیگری باشد.</p>
        <div class="xr-table-wrap"><table class="xr-table"><thead><tr><th>مریض / رسید</th><th>مبلغ</th><th>دلیل</th><th>زمان</th></tr></thead><tbody><tr v-for="row in refundList.rows.value" :key="row.id"><td>{{ row.patientName }}<small class="cash-table__sub">{{ row.receiptNumber }}</small></td><td>{{ money(row.amount) }}</td><td>{{ row.reason }}</td><td>{{ formatDateTime(row.createdAt) }}</td></tr></tbody></table></div>
        <UiPager :page="refundList.page.value" :page-count="refundList.pageCount.value" @go="refundList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </details>
      <details class="mt-3" @toggle="historical = ($event.target as HTMLDetailsElement).open"><summary>بدهی‌ها در پایان روز منتخب</summary>
        <p class="form-hint">وضعیت تاریخی تا پایان همان روز به وقت کابل؛ پرداخت یا لغو روزهای بعد از این مانده کم نمی‌شود.</p>
        <div class="xr-table-wrap"><table class="xr-table"><thead><tr><th>فاکتور</th><th>مریض</th><th>بابت</th><th>مانده آن روز</th></tr></thead><tbody><tr v-for="row in historyList.rows.value" :key="row.id"><td>{{ row.invoiceNumber }}</td><td>{{ row.patientName }}</td><td>{{ row.description }}</td><td>{{ money(row.balance) }}</td></tr></tbody></table></div>
        <UiPager :page="historyList.page.value" :page-count="historyList.pageCount.value" @go="historyList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </details>
      <details v-if="summary?.sessions.length" class="mt-3"><summary>صندوق‌های روز منتخب</summary><div class="xr-table-wrap"><table class="xr-table"><thead><tr><th>صندوق / کارمند</th><th>بازشدن</th><th>بسته‌شدن</th><th>مورد انتظار</th><th>شمارش‌شده</th><th>کسری / اضافه</th><th>دلیل</th></tr></thead><tbody><tr v-for="session in summary.sessions" :key="session.id"><td>{{ session.registerName }} · {{ session.openedBy }}</td><td>{{ formatDateTime(session.openedAt) }}</td><td>{{ session.closedAt ? formatDateTime(session.closedAt) : 'باز' }}</td><td>{{ money(session.expectedAmount ?? session.currentExpected) }}</td><td>{{ session.countedAmount === null ? '—' : money(session.countedAmount) }}</td><td>{{ session.difference === null ? '—' : money(session.difference) }}</td><td>{{ session.closeNote ?? '—' }}<button v-if="!session.closedAt && auth.can('payments.refund')" type="button" class="btn btn-soft btn-sm" @click="dayDetails = false; startClose(session)">بررسی و بستن</button></td></tr></tbody></table></div></details>
  </UiModal>

  <UiModal v-if="pay.detail" :title="`فاکتور ${pay.detail.invoice.invoiceNumber}`" @close="pay.detail = null">
    <ul class="list-unstyled small mb-3">
      <li v-for="item in pay.detail.items.filter((i) => !i.isVoid)" :key="item.id">{{ item.description }}: {{ money(item.lineTotal) }}</li>
    </ul>
    <div class="cashier-settlement__summary">
      <span>مبلغ اصلی <strong>{{ money(pay.detail.invoice.total) }}</strong></span>
      <span v-if="pay.detail.invoice.discount">تخفیف پیشین <strong>{{ money(pay.detail.invoice.discount) }}</strong></span>
      <span>قابل پرداخت <strong>{{ money(payFinal) }}</strong></span>
    </div>
    <div v-if="pay.receipt" class="alert alert-success" role="status">
      دریافت ثبت شد. شماره رسید: <span class="ltr-num">{{ pay.receipt.receiptNumber }}</span>
      <template v-if="pay.receipt.queueNumber !== null">، شماره نوبت: <strong>{{ quantity(pay.receipt.queueNumber) }}</strong></template>
      <!-- Printing is the last step of a payment, so it also puts the dialog away for the next patient. -->
      <RouterLink v-if="auth.can('payments.print')" :to="`/print/receipt/${pay.receipt.id}`" target="_blank" class="btn btn-primary btn-sm ms-2" @click="pay.detail = null">چاپ رسید</RouterLink>
    </div>
    <form v-else @submit.prevent="submitPayment">
      <div class="row g-3 mb-3">
        <div class="col-6">
          <label class="form-label" for="pay-amount">مبلغ دریافتی</label>
          <input id="pay-amount" v-model.number="pay.amount" class="form-control" type="number" min="0.01" step="0.01" :max="payFinal" required />
        </div>
        <div class="col-6">
          <label class="form-label" for="pay-discount">تخفیف</label>
          <div class="input-group">
            <input id="pay-discount" v-model.number="pay.discount" class="form-control" type="number" min="0" step="0.01" :max="pay.discountMode === 'Percent' ? 100 : pay.detail.invoice.balance" :disabled="payHasExistingDiscount" />
            <select v-model="pay.discountMode" class="form-select" style="max-width: 94px" :disabled="payHasExistingDiscount"><option value="Amount">AFN</option><option value="Percent">%</option></select>
          </div>
          <small v-if="payHasExistingDiscount" class="form-text">تخفیف این فاکتور قبلاً ثبت شده است.</small>
          <small v-else-if="pay.discount > 0" class="form-text">تخفیف محاسبه‌شده: {{ money(payDiscountAmount) }}</small>
        </div>
      </div>
      <div v-if="pay.error" class="alert alert-danger" role="alert">{{ pay.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="pay.saving">ثبت دریافت</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="closing.open && closingSession" title="بستن صندوق" @close="closing.open = false">
    <form @submit.prevent="close">
      <p>{{ closingSession.registerName }} · {{ closingSession.openedBy }}<br />دریافت نقدی این جلسه: {{ money(closingSession.cashCollected) }}<br />مبلغ مورد انتظار: {{ money(closingSession.currentExpected) }}</p>
      <p class="small text-muted">موجودی آغاز + دریافت نقدی − برگشت و مصارف مرتبط با همین صندوق. گردش خارج از این صندوق جدا گزارش می‌شود.</p>
      <label class="form-label" for="c-counted">مبلغ شمارش‌شده</label>
      <input id="c-counted" v-model.number="closing.counted" class="form-control mb-3" type="number" min="0" step="0.01" required />
      <label class="form-label" for="c-note">توضیح کسری یا اضافه</label>
      <input id="c-note" v-model="closing.note" class="form-control mb-3" maxlength="300" :required="closing.counted !== closingSession.currentExpected || closingSession.id !== openSession?.id" />
      <p v-if="closing.counted !== closingSession.currentExpected">تفاوت: {{ money(closing.counted - closingSession.currentExpected) }}</p>
      <div v-if="closing.error" class="alert alert-danger" role="alert">{{ closing.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="closing.saving">بستن صندوق</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="transfer.open && openSession" title="تحویل پول صندوق" @close="transfer.open = false">
    <form @submit.prevent="saveTransfer">
      <label class="form-label">مبلغ<input v-model.number="transfer.amount" type="number" class="form-control mb-3" min="0.01" step="0.01" :max="openSession.currentExpected" required /></label>
      <label class="form-label">مقصد<select v-model="transfer.destination" class="form-select mb-3"><option value="Vault">گاوصندوق</option><option value="Bank">بانک</option></select></label>
      <label class="form-label">شرح تحویل<input v-model="transfer.reason" class="form-control mb-3" minlength="3" maxlength="300" required /></label>
      <div v-if="transfer.error" class="alert alert-danger" role="alert">{{ transfer.error }}</div>
      <div class="xr-form-actions"><button type="submit" class="btn btn-primary" :disabled="transfer.saving">ثبت تحویل پول</button><button type="button" class="btn btn-soft" data-modal-cancel>انصراف</button></div>
    </form>
  </UiModal>

  <UiModal v-if="adjustment.row" :title="adjustment.cancel ? 'لغو دریافت' : 'اصلاح دریافت'" @close="adjustment.row = null">
    <form @submit.prevent="saveAdjustment">
      <p>رسید {{ adjustment.row.receiptNumber }} · {{ adjustment.row.patientName }}</p>
      <p class="form-hint">سابقه رسید حفظ می‌شود. اثر اصلاح در صندوق فعلی ثبت و مانده فاکتور هماهنگ می‌شود؛ لغو دریافت به معنی برگشت جنس نیست.</p>
      <label v-if="!adjustment.cancel" class="form-label">مبلغ درست دریافتی<input v-model.number="adjustment.amount" class="form-control mb-3" type="number" min="0.01" step="0.01" required /></label>
      <label class="form-label">دلیل<input v-model="adjustment.reason" class="form-control mb-3" minlength="3" maxlength="300" required /></label>
      <div v-if="adjustment.error" class="alert alert-danger" role="alert">{{ adjustment.error }}</div>
      <div class="xr-form-actions"><button type="submit" class="btn" :class="adjustment.cancel ? 'btn-danger' : 'btn-primary'" :disabled="adjustment.saving">{{ adjustment.cancel ? 'تأیید لغو دریافت' : 'ثبت اصلاح' }}</button><button type="button" class="btn btn-soft" data-modal-cancel>انصراف</button></div>
    </form>
  </UiModal>

  <UiModal v-if="voiding.row" :title="`پرداخت نکرد: ${voiding.row.patientName ?? voiding.row.invoiceNumber}`" @close="voiding.row = null">
    <form @submit.prevent="submitVoid">
      <p class="mb-3">فاکتور <span class="ltr-num">{{ voiding.row.invoiceNumber }}</span> به مبلغ {{ money(voiding.row.balance) }} باطل می‌شود، از صندوق خارج می‌شود و در هیچ گزارشی مانده قابل دریافت حساب نمی‌شود.</p>
      <label class="form-label" for="v-reason">دلیل</label>
      <input id="v-reason" v-model="voiding.reason" class="form-control mb-3" maxlength="300" required />
      <div v-if="voiding.error" class="alert alert-danger" role="alert">{{ voiding.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="voiding.saving">تأیید، خارج شود</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

</template>

<style scoped>
.cash-filters { align-items: center; }
.cash-filters :deep(.mb-3) { margin-bottom: 0 !important; }
.cash-filters__all { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; margin: 0; }
.cash-day-summary { display: flex; flex-wrap: wrap; gap: 12px 24px; padding: 16px 0; }
.cash-day-summary span { display: grid; gap: 4px; }
.cash-day-summary strong { font-size: 1.2rem; }

.cash-open .form-select, .cash-open .form-control { width: auto; min-height: 38px; padding-block: 4px; }
.cash-open__amount { display: inline-flex; align-items: center; gap: 8px; margin: 0; white-space: nowrap; }
.cash-open__amount .form-control { max-width: 130px; }
.dq__money { color: #b91c1c; font-weight: 700; font-size: 15px; }

.cash-table td { vertical-align: middle; }
.cash-table__sub { display: block; font-size: 12px; color: var(--xr-text-muted, #6b7280); }
.cash-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px; }
.cash-bar__tabs { flex: 0 0 auto; }
.cash-bar__session { display: inline-flex; align-items: center; gap: 6px 12px; flex-wrap: wrap; padding: 6px 12px; border-radius: 999px; background: var(--xr-switch-bg, #f1f5f9); font-size: 14px; white-space: nowrap; }
.cash-bar__dot { width: 8px; height: 8px; border-radius: 50%; background: #16a34a; }
.cash-bar__actions { display: flex; gap: 8px; margin-inline-start: auto; }

.cashier-settlement__summary { display: flex; flex-wrap: wrap; gap: 10px 20px; margin: 0 0 16px; padding: 10px 12px; background: var(--xr-page-bg); border-radius: 8px; }
.cashier-settlement__summary span { display: inline-flex; gap: 6px; }

@media (max-width: 767.98px) {
  .cash-open .form-select, .cash-open .form-control { min-height: 44px; font-size: 16px; }
  .cash-open__amount { flex-wrap: wrap; }
}
</style>
