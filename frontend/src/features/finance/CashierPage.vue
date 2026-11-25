<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { fetchAll, usePagedList } from '@/api/paged'
import UiPager from '@/components/UiPager.vue'
import { useLiveRefresh } from '@/api/realtime'
import UiModal from '@/components/UiModal.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import { useAuthStore } from '@/stores/auth'
import UiRowMenu from '@/components/UiRowMenu.vue'
import { formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { invoiceSubject, type InvoiceDetail, type InvoiceRow, type PaymentRow, type SessionRow } from './types'

const auth = useAuthStore()
const invoices = ref<InvoiceRow[]>([])
const opticalOrders = ref<{ id: number; orderNumber: string; patientName: string | null; invoiceId: number; invoiceNumber: string; total: number; balance: number }[]>([])
const sessions = ref<SessionRow[]>([])
const registers = ref<{ id: number; name: string }[]>([])
const q = ref('')
const loading = ref(true)
/** What an action of the cashier refused. Kept apart from list loading, so a background refresh never wipes it before it is read. */
const error = ref('')
const loadError = ref('')

const openSession = computed(() => sessions.value.find((s) => !s.closedAt) ?? null)
const pendingTotal = computed(() => invoices.value.reduce((sum, i) => sum + i.balance, 0))

let controller: AbortController | null = null
async function loadInvoices() {
  controller?.abort()
  controller = new AbortController()
  const { signal } = controller
  try {
    const params = new URLSearchParams({ unpaid: 'true' })
    if (q.value.trim()) params.set('q', q.value.trim())
    // Every open invoice, not the first page: the count and the total above are built from this list.
    invoices.value = await fetchAll<InvoiceRow>(`/invoices?${params}`, signal)
    loadError.value = ''
  } catch (cause) {
    if (signal.aborted) return
    loadError.value = errorMessage(cause)
  }
  loading.value = false
}

async function loadOpticalOrders() {
  if (!auth.can('payments.create')) return
  opticalOrders.value = await api<typeof opticalOrders.value>('/optical/orders/unpaid')
}

async function loadSessions() {
  sessions.value = await api<SessionRow[]>('/cash/sessions?mine=true')
}

/** What this register has taken so far, newest first, so a lost receipt is printed again without leaving the desk. */
const receiptList = usePagedList<PaymentRow>(() => `/payments?sessionId=${openSession.value?.id ?? 0}`, 10)
const showReceipts = computed(() => !!openSession.value && auth.can('payments.view'))

async function loadReceipts() {
  if (showReceipts.value) await receiptList.load()
  else receiptList.rows.value = []
}

async function refresh() {
  try {
    await Promise.all([loadInvoices(), loadOpticalOrders(), loadSessions()])
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
    await loadSessions()
    await loadReceipts()
  } catch (cause) {
    // A register has one open session at a time; the plain duplicate message would not say what to do.
    error.value = cause instanceof ApiError && cause.code === 'duplicate'
      ? 'این صندوق را کارمند دیگری باز کرده است. صندوق دیگری انتخاب کنید یا از او بخواهید صندوق را ببندد.'
      : errorMessage(cause)
  } finally {
    opening.saving = false
  }
}

const closing = reactive({ open: false, counted: 0, saving: false, error: '' })

async function close() {
  closing.saving = true
  closing.error = ''
  try {
    await api(`/cash/sessions/${openSession.value!.id}/close`, { method: 'POST', body: { countedAmount: closing.counted, version: openSession.value!.version } })
    closing.open = false
    await loadSessions()
    await loadReceipts()
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
    if (cause instanceof ApiError && cause.code === 'invoice_changed') {
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

/** Unpaid and undiscounted: the patient left without paying, so the invoice is voided and leaves every receivable. */
const canDrop = (row: InvoiceRow) => row.paid === 0 && row.discount === 0 && auth.can('invoices.cancel')
const dropReason = 'مریض پرداخت نکرد'
function startDrop(row: InvoiceRow) {
  Object.assign(voiding, { row, reason: dropReason, error: '' })
}

function cashierRowItems(row: InvoiceRow) {
  const items: { label: string; ariaLabel?: string; icon?: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[] = []
  if (auth.can('invoices.print')) items.push({ label: 'فاکتور', icon: 'ri-printer-line', onSelect: () => { window.open(`/print/invoice/${row.id}`, '_blank', 'noopener') } })
  if (canDrop(row) && auth.can('invoices.create')) {
    items.push({ label: 'اصلاح', icon: 'ri-edit-line', onSelect: () => { window.location.assign(`/cashier/invoice?correct=${encodeURIComponent(row.id)}`) } })
  }
  return items
}

/** Three lists at one desk: invoices to collect, optical orders waiting for money, and this register's receipts. */
const tab = ref<'invoices' | 'optical' | 'receipts'>('invoices')

let debounce: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(debounce)
  debounce = setTimeout(loadInvoices, 300)
})

useLiveRefresh(['InvoiceIssued', 'PaymentReceived'], refresh)
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
          <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'invoices' }" :aria-selected="tab === 'invoices'" @click="tab = 'invoices'">
            قابل دریافت <span v-if="invoices.length" class="xr-tab__count">{{ quantity(invoices.length) }}</span>
          </button>
          <button v-if="opticalOrders.length" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'optical' }" :aria-selected="tab === 'optical'" @click="tab = 'optical'">
            سفارش‌های عینک <span class="xr-tab__count">{{ quantity(opticalOrders.length) }}</span>
          </button>
          <button v-if="showReceipts" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'receipts' }" :aria-selected="tab === 'receipts'" @click="tab = 'receipts'">دریافت‌های این صندوق</button>
        </div>
        <div v-if="openSession" class="cash-bar__session" :title="`${openSession.registerName} · باز از ${formatDateTime(openSession.openedAt)} · شروع ${money(openSession.openingAmount)}`">
          <span class="cash-bar__dot" aria-hidden="true"></span>
          <strong>{{ openSession.registerName }}</strong>
          <span>نقد {{ money(openSession.cashCollected) }}</span>
          <span class="dq__warn">مانده {{ money(pendingTotal) }}</span>
        </div>
        <span v-else-if="!auth.can('cash.create')" class="xr-badge xr-badge--orange">صندوق بسته است</span>
        <div class="cash-bar__actions">
          <RouterLink v-if="auth.can('invoices.create')" :to="{ name: 'invoice-new' }" class="btn btn-soft"><i class="ri-add-line" aria-hidden="true"></i> فاکتور مستقل</RouterLink>
          <button v-if="openSession && auth.can('cash.edit')" type="button" class="btn btn-quiet" @click="closing.open = true; closing.counted = 0; closing.error = ''">بستن صندوق</button>
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

      <template v-if="tab === 'invoices'">
        <div class="dq-filters">
          <label class="dq-search">
            <i class="ri-search-line" aria-hidden="true"></i>
            <input v-model="q" type="search" placeholder="جستجو: شماره فاکتور، پرونده یا نام" aria-label="جستجوی فاکتور" />
            <button v-if="q" type="button" class="dq-search__clear" aria-label="پاک کردن جستجو" @click="q = ''"><i class="ri-close-line" aria-hidden="true"></i></button>
          </label>
        </div>
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
                  <span v-if="row.queueNumber" class="cash-table__sub">نوبت {{ quantity(row.queueNumber) }}</span>
                </td>
                <td>{{ row.section ?? '—' }}</td>
                <td>{{ invoiceSubject(row) }}</td>
                <td class="text-nowrap">
                  <span class="dq__money">{{ money(row.balance) }}</span>
                  <span v-if="row.paid" class="cash-table__sub">پرداخت‌شده {{ money(row.paid) }}</span>
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
      </template>

      <template v-else-if="tab === 'optical'">
        <p class="form-hint mb-0">مریض رسید سفارش را به صندوق می‌آورد؛ با «وجه دریافت شد» همان فاکتور قفل‌شده پرداخت می‌شود.</p>
        <ol class="dq" aria-label="سفارش‌های عینک در انتظار پرداخت">
          <li v-for="order in opticalOrders" :key="order.id" class="dq__row">
            <div class="dq__num"><strong class="ltr-num dq__code">{{ order.orderNumber }}</strong></div>
            <div class="dq__who">
              <div class="dq__name">{{ order.patientName ?? 'سفارش مستقل' }}</div>
              <div class="dq__detail"><span>فاکتور <span class="ltr-num">{{ order.invoiceNumber }}</span></span><span>مجموع {{ money(order.total) }}</span></div>
            </div>
            <div class="dq__state dq__money">{{ money(order.balance) }}</div>
            <div class="dq__actions">
              <button type="button" class="btn btn-primary" :disabled="!openSession" @click="openPayment({ id: order.invoiceId, balance: order.balance })">وجه دریافت شد</button>
              <RouterLink class="btn btn-soft" :to="{ name: 'print', params: { kind: 'optical-order', id: order.id } }">چاپ رسید</RouterLink>
            </div>
          </li>
        </ol>
      </template>

      <template v-else>
        <div v-if="receiptList.rows.value.length === 0" class="xr-state">هنوز در این صندوق دریافتی ثبت نشده است.</div>
        <div v-else class="xr-table-wrap">
          <table class="xr-table cash-table" aria-label="دریافت‌های این صندوق">
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
                  <span v-if="row.refunded" class="cash-table__sub dq__warn">برگشت {{ money(row.refunded) }}</span>
                </td>
                <td>
                  <div class="dq__actions"><RouterLink v-if="auth.can('payments.print')" :to="`/print/receipt/${row.id}`" target="_blank" class="btn btn-soft">چاپ دوباره</RouterLink></div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <UiPager :page="receiptList.page.value" :page-count="receiptList.pageCount.value" @go="receiptList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </template>
    </section>
  </div>

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

  <UiModal v-if="closing.open && openSession" title="بستن صندوق" @close="closing.open = false">
    <form @submit.prevent="close">
      <p>دریافت نقدی این جلسه: {{ money(openSession.cashCollected) }}<br />مبلغ مورد انتظار: {{ money(openSession.openingAmount + openSession.cashCollected) }}</p>
      <p class="small text-muted">این مبلغ فقط موجودی آغاز و دریافت نقدی این جلسه است. برگشت وجه و مصارف از آن کم نشده است.</p>
      <label class="form-label" for="c-counted">مبلغ شمارش‌شده</label>
      <input id="c-counted" v-model.number="closing.counted" class="form-control mb-3" type="number" min="0" step="0.01" required />
      <div v-if="closing.error" class="alert alert-danger" role="alert">{{ closing.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="closing.saving">بستن صندوق</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
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
