<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, errorMessage } from '@/api/http'
import { usePagedList } from '@/api/paged'
import { useLiveRefresh } from '@/api/realtime'
import UiModal from '@/components/UiModal.vue'
import UiPager from '@/components/UiPager.vue'
import UiListSkeleton from '@/components/UiListSkeleton.vue'
import QueueCallActions from '@/features/clinical/queue/QueueCallActions.vue'
import { callStateOf } from '@/features/clinical/queue/queueState'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { rxStageLabels } from '@/features/clinical/types'
import { useAuthStore } from '@/stores/auth'
import { formatDate, formatDateTime } from '@/utils/date'
import { money, quantity } from '@/utils/money'
import { type PharmacyPatient, type PharmacyPatientHit, type PharmacyQueueBill, type PharmacyQueues, type PharmacyRxRow } from './pharmacy'

interface SaleRow {
  id: number
  saleNumber: string
  patientName: string | null
  invoiceNumber: string
  total: number
  balance: number
  invoiceStatus: string
  isReturned: boolean
  createdAt: string
  deliveredAt: string | null
  version: number
}

const auth = useAuthStore()
const calls = useCallDesk('Pharmacy')
const error = ref('')
const notice = ref('')

// Search: one box for name, father's name, phone, file number or any number on the patient's papers.
const q = ref('')
const searching = ref(false)
const hits = ref<PharmacyPatientHit[] | null>(null)
const chosen = ref<{ patient: PharmacyPatient; prescriptions: PharmacyRxRow[] } | null>(null)

// A newer search cancels the older one, so a slow answer for an earlier term never replaces the current list.
let searchController: AbortController | null = null

async function search() {
  const term = q.value.trim()
  searchController?.abort()
  chosen.value = null
  // One digit is a whole file number now, so it searches; one letter would find too much.
  if (term.length < 2 && !/^[0-9۰-۹]$/.test(term)) {
    hits.value = null
    searching.value = false
    return
  }

  const controller = new AbortController()
  searchController = controller
  searching.value = true
  try {
    const found = await api<PharmacyPatientHit[]>(`/pharmacy/search?q=${encodeURIComponent(term)}`, { signal: controller.signal })
    hits.value = found
    if (found.length === 1) await choose(found[0].id, controller.signal)
  } catch (cause) {
    if (controller.signal.aborted) return
    error.value = errorMessage(cause)
  } finally {
    if (searchController === controller) searching.value = false
  }
}

async function choose(id: number, signal?: AbortSignal) {
  try {
    const result = await api<{ patient: PharmacyPatient; prescriptions: PharmacyRxRow[] }>(`/pharmacy/patients/${id}/prescriptions`, { signal })
    if (!signal?.aborted) chosen.value = result
  } catch (cause) {
    if (signal?.aborted) return
    error.value = errorMessage(cause)
  }
}

const router = useRouter()
const openRx = (id: number) => router.push({ name: 'pharmacy-rx', params: { id } })

/** Enter in the search box: one patient found opens their newest prescription; several open the first patient. */
async function searchEnter() {
  // Typed faster than the debounce: search for what is in the box now, never act on the previous term's results.
  if (pending) {
    clearTimeout(debounce)
    pending = false
    await search()
  }
  if (searching.value) return
  if (chosen.value?.prescriptions.length) await openRx(chosen.value.prescriptions[0].id)
  else if (hits.value?.length && !chosen.value) await choose(hits.value[0].id)
}

let debounce: ReturnType<typeof setTimeout> | undefined
let pending = false
watch(q, () => {
  clearTimeout(debounce)
  pending = true
  debounce = setTimeout(() => {
    pending = false
    search()
  }, 350)
})

// Work lists.
// Four lists: new prescriptions, money and handing over, prescriptions that wait on something, and history.
type TabKey = 'new' | 'bills' | 'followUp' | 'sales'
const tab = ref<TabKey>('new')
const queues = ref<PharmacyQueues | null>(null)
// The sales history is read a page at a time, so older sales stay reachable for a return or a reprint.
const saleList = usePagedList<SaleRow>(() => '/pharmacy/sales')
const sales = saleList.rows
const loading = ref(true)

const tabs = computed(() => [
  { key: 'new' as const, label: 'نسخه‌های جدید', count: queues.value?.new.length },
  { key: 'bills' as const, label: 'پرداخت و تحویل', count: (queues.value?.readyToDeliver.length ?? 0) + (queues.value?.awaitingPayment.length ?? 0) },
  { key: 'followUp' as const, label: 'نسخه‌های در انتظار', count: (queues.value?.partial.length ?? 0) + (queues.value?.needsDoctor.length ?? 0) },
  { key: 'sales' as const, label: 'سوابق فروش', count: undefined },
])

const rxRows = computed(() => (tab.value === 'new' ? queues.value?.new ?? [] : tab.value === 'followUp' ? [...(queues.value?.needsDoctor ?? []), ...(queues.value?.partial ?? [])] : []))
/** Ready to hand over first: the patient has paid and is standing at the counter. */
const billRows = computed(() => (tab.value === 'bills'
  ? [...(queues.value?.readyToDeliver ?? []).map((b) => ({ ...b, ready: true })), ...(queues.value?.awaitingPayment ?? []).map((b) => ({ ...b, ready: false }))]
  : []))
/** Called patient first, then the next one, then the rest; prescriptions keep the server's order otherwise. */
const rxKind = (rx: PharmacyRxRow) => (tab.value === 'new' ? callStateOf(calls, rx.visitId) ?? 'waiting' : 'waiting')

async function load() {
  error.value = ''
  try {
    queues.value = await api<PharmacyQueues>('/pharmacy/queues')
    await saleList.load()
    if (chosen.value) await choose(chosen.value.patient.id)
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const delivering = ref<number | null>(null)

async function deliver(bill: PharmacyQueueBill) {
  delivering.value = bill.saleId
  error.value = ''
  try {
    await api(`/pharmacy/sales/${bill.saleId}/deliver`, { method: 'POST', body: { version: bill.version } })
    notice.value = `بل ${bill.saleNumber} تحویل شد و از موجودی کم شد.`
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    delivering.value = null
  }
}

const returning = reactive({ row: null as SaleRow | null, reason: '', saving: false, error: '' })

async function submitReturn() {
  returning.saving = true
  returning.error = ''
  try {
    await api(`/pharmacy/sales/${returning.row!.id}/return`, { method: 'POST', body: { version: returning.row!.version, reason: returning.reason } })
    returning.row = null
    await load()
  } catch (cause) {
    returning.error = errorMessage(cause)
  } finally {
    returning.saving = false
  }
}

useLiveRefresh(['PrescriptionCreated', 'InventoryChanged', 'PaymentReceived'], load)
onMounted(load)
onBeforeUnmount(() => {
  clearTimeout(debounce)
  searchController?.abort()
})
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success mb-0" role="status">{{ notice }}</div>

    <section class="dq-page">
      <div class="dq-head">
        <h1 class="dq-title">دواخانه</h1>
        <div class="dq-head__actions">
          <RouterLink v-if="auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale' }" class="btn btn-soft"><i class="ri-add-line" aria-hidden="true"></i> فروش مستقل</RouterLink>
        </div>
      </div>

      <!-- One field finds any patient or prescription, old or new; while it has text, its results replace the lists. -->
      <div class="dq-filters">
        <label class="dq-search">
          <i class="ri-search-line" aria-hidden="true"></i>
          <input v-model="q" type="search" placeholder="جستجو: نام، نام پدر، شماره تماس، پرونده، نسخه یا مراجعه" aria-label="جستجوی مریض و نسخه" autofocus @keydown.enter.prevent="searchEnter" />
          <button v-if="q" type="button" class="dq-search__clear" aria-label="پاک کردن جستجو" @click="q = ''"><i class="ri-close-line" aria-hidden="true"></i></button>
        </label>
      </div>

      <template v-if="q.trim()">
        <div v-if="searching" class="xr-state" role="status">در حال جستجو...</div>
        <div v-else-if="hits && hits.length === 0" class="xr-state">مریضی پیدا نشد.</div>
        <ol v-else-if="hits && !chosen" class="dq" aria-label="نتیجه جستجو">
          <li v-for="p in hits" :key="p.id" class="dq__row">
            <div class="dq__num"><strong>{{ quantity(p.prescriptionCount) }}</strong><small class="dq__shift">نسخه</small></div>
            <div class="dq__who">
              <div class="dq__name">{{ p.name }}</div>
              <div class="dq__detail">
                <span v-if="p.fatherName">ولد {{ p.fatherName }}</span>
                <span class="ltr-num">{{ p.fileNumber }}</span>
                <span v-if="p.phone" class="ltr-num">{{ p.phone }}</span>
                <span v-if="p.age">{{ p.age }} ساله</span>
              </div>
            </div>
            <div class="dq__state">{{ p.lastPrescriptionAt ? formatDate(p.lastPrescriptionAt) : '—' }}</div>
            <div class="dq__actions"><button type="button" class="btn btn-soft" @click="choose(p.id)">انتخاب</button></div>
          </li>
        </ol>
        <template v-if="chosen">
          <div class="dq-chosen">
            <strong>{{ chosen.patient.name }}</strong>
            <span v-if="chosen.patient.fatherName">ولد {{ chosen.patient.fatherName }}</span>
            <span class="ltr-num">{{ chosen.patient.fileNumber }}</span>
            <span v-if="chosen.patient.phone" class="ltr-num">{{ chosen.patient.phone }}</span>
            <button v-if="hits && hits.length > 1" type="button" class="btn btn-quiet btn-sm" @click="chosen = null">بازگشت به نتایج</button>
          </div>
          <div v-if="chosen.prescriptions.length === 0" class="xr-state">برای این مریض نسخه‌ای ثبت نشده است.</div>
          <ol v-else class="dq" aria-label="نسخه‌های مریض">
            <li v-for="rx in chosen.prescriptions" :key="rx.id" class="dq__row">
              <div class="dq__num"><strong>{{ rx.queueNumber ? quantity(rx.queueNumber) : '—' }}</strong></div>
              <div class="dq__who">
                <div class="dq__name ltr-num">{{ rx.rxNumber }}</div>
                <div class="dq__detail"><span>{{ formatDateTime(rx.createdAt) }}</span><span v-if="rx.doctorName">{{ rx.doctorName }}</span></div>
              </div>
              <div class="dq__state">{{ rxStageLabels[rx.stage] ?? rx.stage }}</div>
              <div class="dq__actions"><RouterLink :to="{ name: 'pharmacy-rx', params: { id: rx.id } }" class="btn btn-soft">باز کردن</RouterLink></div>
            </li>
          </ol>
        </template>
      </template>

      <template v-else>
        <div class="xr-tabs dq-tabs" role="tablist" aria-label="کارهای دواخانه">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === t.key }" :aria-selected="tab === t.key" @click="tab = t.key">
            {{ t.label }}<span v-if="t.count" class="xr-tab__count">{{ quantity(t.count) }}</span>
          </button>
        </div>

        <CallNotice v-if="tab === 'new'" :desks="[calls]" :numbers="false" />
        <UiListSkeleton v-if="loading" />

        <template v-else-if="tab === 'new' || tab === 'followUp'">
          <div v-if="rxRows.length === 0" class="xr-state">{{ tab === 'new' ? 'نسخه تازه‌ای در هفت روز گذشته نیست. نسخه‌های قدیمی را جستجو کنید.' : 'نسخه‌ای در انتظار نیست.' }}</div>
          <ol v-else class="dq" aria-label="نسخه‌ها">
            <li v-for="rx in rxRows" :key="rx.id" class="dq__row" :class="`dq__row--${rxKind(rx)}`">
              <div class="dq__num"><strong>{{ rx.queueNumber ? quantity(rx.queueNumber) : '—' }}</strong></div>
              <div class="dq__who">
                <div class="dq__name">{{ rx.patientName }}</div>
                <div class="dq__detail">
                  <span v-if="rx.fatherName">ولد {{ rx.fatherName }}</span>
                  <span class="ltr-num">{{ rx.fileNumber }}</span>
                  <span v-if="rx.doctorName">{{ rx.doctorName }}</span>
                  <span>{{ formatDateTime(rx.createdAt) }}</span>
                </div>
              </div>
              <div class="dq__state">{{ rxStageLabels[rx.stage] ?? rx.stage }}</div>
              <div class="dq__actions">
                <RouterLink :to="{ name: 'pharmacy-rx', params: { id: rx.id } }" class="btn" :class="rxKind(rx) === 'called' ? 'btn-primary' : 'btn-soft'">باز کردن نسخه</RouterLink>
                <QueueCallActions v-if="tab === 'new'" :desk="calls" :visit-id="rx.visitId" />
              </div>
            </li>
          </ol>
        </template>

        <template v-else-if="tab === 'bills'">
          <div v-if="billRows.length === 0" class="xr-state">بلی منتظر پرداخت یا تحویل نیست.</div>
          <ol v-else class="dq" aria-label="بل‌های دواخانه">
            <li v-for="b in billRows" :key="b.saleId" class="dq__row" :class="b.ready ? 'dq__row--next' : 'dq__row--blocked'">
              <div class="dq__num"><strong>{{ b.queueNumber ? quantity(b.queueNumber) : '—' }}</strong></div>
              <div class="dq__who">
                <div class="dq__name">{{ b.patientName ?? 'فروش مستقل' }}</div>
                <div class="dq__detail">
                  <span>بل <span class="ltr-num">{{ b.saleNumber }}</span></span>
                  <span>{{ money(b.total) }}</span>
                  <span v-if="b.balance > 0" class="dq__warn">مانده {{ money(b.balance) }}</span>
                  <span>{{ formatDateTime(b.createdAt) }}</span>
                  <span v-if="b.reservationExpired && !b.ready" class="dq__warn" title="موجودی دیگر برای این بل نگه داشته نمی‌شود">رزرو منقضی</span>
                </div>
              </div>
              <div class="dq__state">{{ b.ready ? 'آماده تحویل' : 'منتظر پرداخت' }}</div>
              <div class="dq__actions">
                <button v-if="b.ready && auth.can('pharmacy.create')" type="button" class="btn btn-primary" :disabled="delivering === b.saleId" @click="deliver(b)">تحویل دوا</button>
                <RouterLink v-if="b.prescriptionId" :to="{ name: 'pharmacy-rx', params: { id: b.prescriptionId } }" class="btn btn-soft">نسخه</RouterLink>
              </div>
            </li>
          </ol>
        </template>

        <template v-else>
          <div v-if="sales.length === 0" class="xr-state">فروشی ثبت نشده است.</div>
          <ol v-else class="dq" aria-label="سوابق فروش">
            <li v-for="row in sales" :key="row.id" class="dq__row" :class="{ 'dq__row--away': row.isReturned }">
              <div class="dq__num"><strong class="ltr-num dq__code">{{ row.saleNumber }}</strong></div>
              <div class="dq__who">
                <div class="dq__name">{{ row.patientName ?? 'فروش مستقل' }}</div>
                <div class="dq__detail">
                  <span>{{ row.isReturned ? 'لغو / برگشت شده' : money(row.total) }}</span>
                  <span v-if="row.balance > 0" class="dq__warn">مانده {{ money(row.balance) }}</span>
                  <span>فاکتور <span class="ltr-num">{{ row.invoiceNumber }}</span></span>
                  <span>{{ formatDateTime(row.createdAt) }}</span>
                </div>
              </div>
              <div class="dq__state">{{ row.isReturned ? 'لغو شده' : row.deliveredAt ? 'تحویل شده' : row.balance > 0 ? 'منتظر پرداخت' : 'آماده تحویل' }}</div>
              <div class="dq__actions">
                <RouterLink v-if="auth.can('pharmacy.print')" :to="`/print/sale/${row.id}`" target="_blank" class="btn btn-soft">فاکتور</RouterLink>
                <RouterLink v-if="!row.isReturned && !row.deliveredAt && auth.can('pharmacy.create')" :to="{ name: 'pharmacy-sale', query: { correct: row.id } }" class="btn btn-soft">اصلاح</RouterLink>
                <button v-if="!row.isReturned && auth.can('pharmacy.cancel')" type="button" class="btn btn-quiet dq__absent" @click="Object.assign(returning, { row, reason: '', error: '' })">{{ row.deliveredAt ? 'برگشت' : 'لغو بل' }}</button>
              </div>
            </li>
          </ol>
          <UiPager :page="saleList.page.value" :page-count="saleList.pageCount.value" @go="saleList.goTo($event).catch((cause) => (error = errorMessage(cause)))" />
        </template>
      </template>
    </section>
  </div>

  <UiModal v-if="returning.row" :title="`${returning.row.deliveredAt ? 'برگشت فروش' : 'لغو بل'} ${returning.row.saleNumber}`" @close="returning.row = null">
    <form @submit.prevent="submitReturn">
      <p class="small">{{ returning.row.deliveredAt ? 'با تأیید، تمام دوای این بل را از مریض تحویل بگیرید؛ دوا به موجودی برمی‌گردد.' : 'بل لغو و موجودی نگه‌داشته‌شده آزاد می‌شود.' }} مبلغی که هنوز از مریض نزد شفاخانه است، هم‌زمان برگشت وجه ثبت می‌شود؛ پول را به مریض پس بدهید. رسید اصلی در سابقه باقی می‌ماند.</p>
      <label class="form-label" for="ret-reason">دلیل</label>
      <input id="ret-reason" v-model="returning.reason" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="returning.error" class="alert alert-danger" role="alert">{{ returning.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-danger" :disabled="returning.saving">{{ returning.row.deliveredAt ? 'تأیید برگشت دوا و وجه' : 'تأیید لغو و برگشت وجه' }}</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
