<script setup lang="ts">
import { askConfirm } from '@/components/confirm'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import QueueCallActions from '@/features/clinical/queue/QueueCallActions.vue'
import { callStateOf } from '@/features/clinical/queue/queueState'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import type { OpticalRx } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'

interface OrderRow {
  id: number
  orderNumber: string
  patientName: string | null
  customerPhone: string | null
  opticalPrescriptionId: number | null
  description: string | null
  status: string
  invoiceNumber: string
  total: number
  balance: number
  deliveredAt: string | null
  createdAt: string
  version: number
  nextStatuses: string[]
  isComplimentary: boolean
  complimentaryReason: string | null
  complimentaryApprovedBy: string | null
}

interface ComplimentaryOrderRow {
  id: number
  orderNumber: string
  patientName: string | null
  items: string
  complimentaryReason: string
  complimentaryApprovedBy: string
  deliveredAt: string
}

const statusLabels: Record<string, string> = { Ordered: 'سفارش‌شده', InWorkshop: 'در کارگاه', Ready: 'آماده', Delivered: 'تحویل‌شده', Cancelled: 'لغو شده' }
/** The one next step a row offers as its main button, named as an action. */
const stepLabels: Record<string, string> = { InWorkshop: 'ارسال به کارگاه', Ready: 'آماده شد', Delivered: 'تحویل به مریض' }

const auth = useAuthStore()
const router = useRouter()
const calls = useCallDesk('Optical')

/** Three lists: prescriptions still waiting for an order, every prescription, and sales. */
const tab = ref<'waiting' | 'all' | 'sales'>('waiting')
const search = ref('')
const rxScope = computed(() => (tab.value === 'all' ? 'all' : 'waiting'))
/** Sales: one server status, or the separate complimentary report. */
const salesFilter = ref('')

const prescriptions = ref<OpticalRx[]>([])
// Search goes to the server, so an order from days ago is found by name or phone, not only on the page shown.
const orderList = usePagedList<OrderRow>(() => {
  const params = new URLSearchParams()
  if (salesFilter.value && salesFilter.value !== 'free') params.set('status', salesFilter.value)
  const q = plain(search.value)
  if (q) params.set('q', q)
  const query = params.toString()
  return `/optical/orders${query ? `?${query}` : ''}`
})
const orders = orderList.rows
const complimentaryOrders = ref<ComplimentaryOrderRow[]>([])
const loading = ref(true)
const error = ref('')

function plain(text: string | null | undefined) {
  // Persian and Arabic digits match the Latin ones staff see on cards and receipts.
  return (text ?? '').replace(/[۰-۹]/g, (d) => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٠-٩]/g, (d) => String('٠١٢٣٤٥٦٧٨٩'.indexOf(d))).toLowerCase().trim()
}

function matches(...fields: (string | number | null | undefined)[]) {
  const q = plain(search.value)
  return !q || fields.some((field) => plain(field == null ? '' : String(field)).includes(q))
}

const waitingCount = computed(() => prescriptions.value.filter((rx) => !rx.orderStatus).length)
const visibleRx = computed(() => prescriptions.value
  .filter((rx) => rxScope.value === 'all' || !rx.orderStatus)
  .filter((rx) => matches(rx.patientName, rx.rxNumber, rx.patientFileNumber, rx.queueNumber)))
// Cancelled orders leave the everyday list; the «لغو شده» filter still shows them.
const visibleOrders = computed(() => orders.value.filter((row) => (salesFilter.value || row.status !== 'Cancelled') && matches(row.patientName, row.customerPhone, row.orderNumber, row.invoiceNumber, row.description)))
const visibleFree = computed(() => complimentaryOrders.value.filter((row) => matches(row.patientName, row.orderNumber, row.items)))

async function loadPrescriptions() {
  const q = search.value.trim()
  prescriptions.value = await api<OpticalRx[]>(`/optical-prescriptions${q ? `?q=${encodeURIComponent(q)}` : ''}`)
}

async function loadSales() {
  if (salesFilter.value === 'free') {
    complimentaryOrders.value = await api<ComplimentaryOrderRow[]>('/optical/reports/complimentary')
  } else {
    await orderList.load()
  }
}

async function load() {
  try {
    await Promise.all([loadPrescriptions(), loadSales()])
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

// Search also reaches older prescriptions the first page does not hold.
let searchTimer: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(() => Promise.all([loadPrescriptions(), salesFilter.value === 'free' ? null : orderList.first()]).catch((cause) => (error.value = errorMessage(cause))), 300)
})
watch(salesFilter, () => {
  orderList.page.value = 1
  loadSales().catch((cause) => (error.value = errorMessage(cause)))
})

function allowed(status: string) {
  return status === 'Cancelled' ? auth.can('optical.cancel') : auth.can('optical.edit')
}

function nextStep(row: OrderRow) {
  return row.nextStatuses.find((status) => status !== 'Cancelled' && allowed(status)) ?? null
}

async function moveTo(row: OrderRow, status: string) {
  error.value = ''
  // One slip of the pointer must not move an order: the step is named and confirmed first.
  if (!(await askConfirm(`سفارش ${row.orderNumber}: ${stepLabels[status] ?? statusLabels[status]}؟`, { confirmLabel: stepLabels[status] ?? statusLabels[status] }))) return
  try {
    await api(`/optical/orders/${row.id}/status`, { method: 'POST', body: { status, version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

const cancelling = reactive({ row: null as OrderRow | null, reason: '', saving: false, error: '' })

async function submitCancel() {
  cancelling.saving = true
  cancelling.error = ''
  try {
    await api(`/optical/orders/${cancelling.row!.id}/status`, { method: 'POST', body: { status: 'Cancelled', version: cancelling.row!.version, reason: cancelling.reason } })
    cancelling.row = null
    await load()
  } catch (cause) {
    cancelling.error = errorMessage(cause)
  } finally {
    cancelling.saving = false
  }
}

function menuItems(row: OrderRow) {
  const main = nextStep(row)
  const items: { label: string; icon?: string; danger?: boolean; onSelect: () => void }[] = []
  // Any further allowed step that is not the main button, such as delivering an order that skipped the workshop.
  for (const status of row.nextStatuses.filter((s) => s !== 'Cancelled' && s !== main && allowed(s))) {
    items.push({ label: stepLabels[status] ?? statusLabels[status], icon: 'ri-arrow-left-line', onSelect: () => moveTo(row, status) })
  }
  if (auth.can('optical.print')) {
    items.push({ label: 'چاپ رسید', icon: 'ri-printer-line', onSelect: () => window.open(router.resolve(`/print/optical-order/${row.id}`).href, '_blank') })
  }
  if (row.nextStatuses.includes('Cancelled') && auth.can('optical.cancel') && auth.can('optical.create')) {
    items.push({ label: 'اصلاح سفارش', icon: 'ri-edit-line', onSelect: () => router.push({ name: 'optical-order', query: { correct: row.id } }) })
  }
  if (row.nextStatuses.includes('Cancelled') && auth.can('optical.cancel')) {
    items.push({ label: 'لغو سفارش', icon: 'ri-close-circle-line', danger: true, onSelect: () => Object.assign(cancelling, { row, reason: '', saving: false, error: '' }) })
  }
  return items
}

const rxKind = (rx: OpticalRx) => callStateOf(calls, rx.visitId) ?? 'waiting'
const eyeLine = (sph?: string | null, cyl?: string | null, axis?: string | null, add?: string | null) => [sph, cyl, axis, add].map((v) => v ?? '—').join(' / ')

useLiveRefresh(['OpticalPrescriptionCreated', 'OpticalOrderChanged', 'InventoryChanged', 'PaymentReceived'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <section class="dq-page">
      <div class="dq-head">
        <h1 class="dq-title">عینک</h1>
        <div class="dq-head__actions">
          <RouterLink v-if="auth.can('optical.create')" :to="{ name: 'optical-order' }" class="btn btn-soft"><i class="ri-add-line" aria-hidden="true"></i> فروش بدون نسخه</RouterLink>
        </div>
      </div>

      <div class="xr-tabs dq-tabs" role="tablist" aria-label="بخش‌های عینک">
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'waiting' }" :aria-selected="tab === 'waiting'" @click="tab = 'waiting'">
          منتظر سفارش <span v-if="waitingCount" class="xr-tab__count">{{ quantity(waitingCount) }}</span>
        </button>
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'all' }" :aria-selected="tab === 'all'" @click="tab = 'all'">همه نسخه‌ها</button>
        <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'sales' }" :aria-selected="tab === 'sales'" @click="tab = 'sales'">فروشات</button>
      </div>

      <div class="dq-filters">
        <label class="dq-search">
          <i class="ri-search-line" aria-hidden="true"></i>
          <input v-model="search" type="search" placeholder="جستجو: نام مریض یا مشتری، تلفن، شماره نسخه یا سفارش" aria-label="جستجوی مریض، شماره نسخه یا سفارش" />
          <button v-if="search" type="button" class="dq-search__clear" aria-label="پاک کردن جستجو" @click="search = ''"><i class="ri-close-line" aria-hidden="true"></i></button>
        </label>
        <select v-if="tab === 'sales'" v-model="salesFilter" class="form-select dq-select" aria-label="فیلتر فروشات">
          <option value="">در جریان و تحویل‌شده</option>
          <option value="Ordered">سفارش‌شده</option>
          <option value="InWorkshop">در کارگاه</option>
          <option value="Ready">آماده تحویل</option>
          <option value="Delivered">تحویل‌شده</option>
          <option value="Cancelled">لغو شده</option>
          <option v-if="auth.can('reports.view')" value="free">تحویل رایگان</option>
        </select>
      </div>

      <template v-if="tab !== 'sales'">
        <CallNotice :desks="[calls]" :numbers="false" />
        <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
        <div v-else-if="visibleRx.length === 0" class="xr-state">{{ rxScope === 'waiting' && !search ? 'نسخه‌ای منتظر سفارش نیست.' : 'نسخه‌ای پیدا نشد.' }}</div>
        <ol v-else class="dq" aria-label="نسخه‌های عینک">
          <li v-for="rx in visibleRx" :key="rx.id" class="dq__row" :class="`dq__row--${rxKind(rx)}`">
            <div class="dq__num"><strong>{{ rx.queueNumber ? quantity(rx.queueNumber) : '—' }}</strong></div>
            <div class="dq__who">
              <div class="dq__name">{{ rx.patientName }}</div>
              <div class="dq__detail">
                <span class="ltr-num">{{ rx.rxNumber }}</span>
                <span>{{ formatDateTime(rx.createdAt) }}</span>
              </div>
              <!-- Both eyes, right above left, as on paper: SPH / CYL / AXIS / ADD. -->
              <div class="dq__detail optical-rx">
                <span><span class="xr-eye xr-eye--od">OD</span> <span class="ltr-num">{{ eyeLine(rx.sphereOd, rx.cylinderOd, rx.axisOd, rx.addOd) }}</span></span>
                <span><span class="xr-eye xr-eye--os">OS</span> <span class="ltr-num">{{ eyeLine(rx.sphereOs, rx.cylinderOs, rx.axisOs, rx.addOs) }}</span></span>
                <span v-if="rx.pd">PD <span class="ltr-num">{{ rx.pd }}</span></span>
              </div>
            </div>
            <div class="dq__state">{{ rx.orderStatus ? statusLabels[rx.orderStatus] : 'بدون سفارش' }}</div>
            <div class="dq__actions">
              <RouterLink v-if="!rx.orderStatus && auth.can('optical.create')" :to="{ name: 'optical-order', query: { rx: rx.id } }" class="btn" :class="rxKind(rx) === 'called' ? 'btn-primary' : 'btn-soft'">ثبت فروش</RouterLink>
              <RouterLink v-if="auth.can('opticalrx.print')" :to="`/print/optical/${rx.id}`" target="_blank" class="btn btn-quiet btn-icon" aria-label="چاپ نسخه" title="چاپ نسخه"><i class="ri-printer-line" aria-hidden="true"></i></RouterLink>
              <QueueCallActions v-if="!rx.orderStatus" :desk="calls" :visit-id="rx.visitId" />
            </div>
          </li>
        </ol>
      </template>

      <template v-else-if="salesFilter === 'free'">
        <div v-if="visibleFree.length === 0" class="xr-state">تحویل رایگان ثبت نشده است.</div>
        <ol v-else class="dq" aria-label="تحویل‌های رایگان">
          <li v-for="row in visibleFree" :key="row.id" class="dq__row">
            <div class="dq__num"><strong class="ltr-num dq__code">{{ row.orderNumber }}</strong></div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName ?? 'بدون نسخه' }}</div>
              <div class="dq__detail"><span>{{ row.items }}</span><span>{{ row.complimentaryReason }} — {{ row.complimentaryApprovedBy }}</span></div>
            </div>
            <div class="dq__state">رایگان</div>
            <div class="dq__actions"><span class="dq__state">{{ formatDateTime(row.deliveredAt) }}</span></div>
          </li>
        </ol>
      </template>

      <template v-else>
        <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
        <div v-else-if="visibleOrders.length === 0" class="xr-state">فروشی پیدا نشد.</div>
        <ol v-else class="dq" aria-label="فروشات عینک">
          <li v-for="row in visibleOrders" :key="row.id" class="dq__row" :class="{ 'dq__row--next': row.status === 'Ready', 'dq__row--away': row.status === 'Cancelled' }">
            <div class="dq__num"><strong class="ltr-num dq__code">{{ row.orderNumber }}</strong></div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName ?? 'بدون نسخه' }}</div>
              <div class="dq__detail">
                <span v-if="row.customerPhone" class="ltr-num">{{ row.customerPhone }}</span>
                <span v-if="row.description">{{ row.description }}</span>
                <span>{{ row.isComplimentary ? 'رایگان' : money(row.total) }}</span>
                <span v-if="row.balance > 0" class="dq__warn">مانده {{ money(row.balance) }}</span>
                <span>{{ formatDateTime(row.createdAt) }}</span>
              </div>
            </div>
            <div class="dq__state">{{ statusLabels[row.status] }}</div>
            <div class="dq__actions">
              <button v-if="nextStep(row)" type="button" class="btn btn-primary" @click="moveTo(row, nextStep(row)!)">{{ stepLabels[nextStep(row)!] ?? statusLabels[nextStep(row)!] }}</button>
              <UiRowMenu v-if="menuItems(row).length" :label="`عملیات سفارش ${row.orderNumber}`" :items="menuItems(row)" />
            </div>
          </li>
        </ol>
        <UiPager :page="orderList.page.value" :page-count="orderList.pageCount.value" @go="orderList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
      </template>
    </section>
  </div>

  <UiModal v-if="cancelling.row" :title="`لغو سفارش ${cancelling.row.orderNumber}`" @close="cancelling.row = null">
    <form @submit.prevent="submitCancel">
      <p>اجناس به موجودی و مبلغ به حالت قبل برمی‌گردد. سفارشی که پرداخت شده باشد از اینجا لغو نمی‌شود.</p>
      <label class="form-label" for="oc-reason">دلیل</label>
      <input id="oc-reason" v-model="cancelling.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="cancelling.error" class="alert alert-danger" role="alert">{{ cancelling.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="cancelling.saving">لغو سفارش</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.optical-rx { direction: ltr; justify-content: flex-end; }
</style>
