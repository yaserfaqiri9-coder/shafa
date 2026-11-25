<script setup lang="ts">
import { createQueueSearch, createShiftFilter } from '@/features/visits/queueTools'
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { patientAge } from '@/features/patients/types'
import type { VisitRow } from '@/features/visits/types'
import { formatDate, todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'
import ClinicalQueueHeader from './queue/ClinicalQueueHeader.vue'
import QueueCallActions from './queue/QueueCallActions.vue'
import { callStateLabels, callStateOf } from './queue/queueState'

interface FollowUpRow {
  id: number
  patientId: number
  patientName: string
  dueDate: string
  note: string | null
}

// Calling and opening the chart stay separate: a chart may be opened many times, a patient is called once.
const calls = useCallDesk('Doctor')
const rows = ref<VisitRow[]>([])
const followUps = ref<FollowUpRow[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    ;[rows.value, followUps.value] = await Promise.all([
      api<VisitRow[]>('/clinical/worklist?kind=doctor'),
      api<FollowUpRow[]>('/follow-ups'),
    ])
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

const tab = ref<'queue' | 'done'>('queue')
const term = ref('')
const queueSearch = createQueueSearch(() => 'doctor')
watch(term, (value) => { queueSearch.draft.value = value; queueSearch.apply() })
const shifts = createShiftFilter()

type Kind = 'with' | 'called' | 'next' | 'waiting' | 'away' | 'absent'
function kindOf(row: VisitRow): Kind {
  if (row.status === 'InConsultation') return 'with'
  if (row.status === 'AwaitingDiagnostic' || row.status === 'FollowUpRequired') return 'away'
  return callStateOf(calls, row.id) ?? 'waiting'
}
const labels: Record<Kind, string> = { ...callStateLabels, with: 'نزد شما', away: 'رفرکشن / آزمایش' }

/** Card order only: morning before afternoon, then the number. Calling or opening a file moves nobody. */
const byCard = (a: VisitRow, b: VisitRow) =>
  Number(a.queueShift === 'Afternoon') - Number(b.queueShift === 'Afternoon') || (a.queueNumber ?? 0) - (b.queueNumber ?? 0) || a.id - b.id
const list = computed(() => queueSearch.filterRows(rows.value).slice().sort(byCard).map((row) => ({ row, kind: kindOf(row) })))
const waitingCount = computed(() => list.value.filter((i) => i.kind === 'next' || i.kind === 'waiting').length)
/** Only a morning patient still here in the afternoon gets a word next to the number; numbers restart at 1. */
const morningLeft = (row: VisitRow) => shifts.tag(rows.value, row)?.text === 'از صبح'

// A management account on the online copy sees every doctor's patients; then each row names its doctor.
const manyDoctors = computed(() => new Set([...rows.value, ...seen.value].map((r) => r.doctorName).filter(Boolean)).size > 1)
function detail(row: VisitRow) {
  const age = patientAge({ ageYears: row.patientAgeYears, ageMonths: row.patientAgeMonths })
  return [age, row.reason, manyDoctors.value && row.doctorName ? `داکتر ${row.doctorName}` : null].filter(Boolean).join(' · ')
}

/** Yesterday's afternoon, for example: day from the date field, shift from the switch beside it. */
const seenDate = ref<string | null>(todayIso())
const seenShift = ref<'all' | 'Morning' | 'Afternoon'>('all')
const seen = ref<VisitRow[]>([])
const seenLoading = ref(false)
const seenList = computed(() => queueSearch.filterRows(seen.value).filter((r) => seenShift.value === 'all' || r.queueShift === seenShift.value))
const seenMixed = computed(() => new Set(seen.value.map((r) => r.queueShift)).size > 1)
/** Results back today, shown on the seen tab so the doctor notices them from the queue too. */
const readyCount = computed(() => (seenDate.value === todayIso() ? seen.value.filter((r) => r.resultReady).length : 0))

/** What the seen list says of a patient: the result mark comes first, everything else is one plain word. */
function seenState(row: VisitRow) {
  if (row.resultReady) return 'نتیجه آماده است'
  if (row.status === 'AwaitingDiagnostic') return 'در رفرکشن / آزمایش'
  if (row.status === 'Completed') return 'تکمیل'
  return 'دیده شد'
}

async function loadSeen() {
  seenLoading.value = true
  try {
    seen.value = await api<VisitRow[]>(`/clinical/worklist?kind=doctor&done=true&date=${seenDate.value ?? todayIso()}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    seenLoading.value = false
  }
}

watch(seenDate, loadSeen)
useLiveRefresh(['PatientCallChanged', 'VisitCreated', 'VisitStatusChanged', 'PreExamCompleted', 'DiagnosticResultReady'], () => Promise.all([load(), loadSeen()]))
onMounted(() => Promise.all([load(), loadSeen()]))
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <section class="dq-page">
      <ClinicalQueueHeader v-model:tab="tab" v-model:term="term" v-model:date="seenDate" v-model:shift="seenShift" title="صف داکتر" :count="waitingCount" queue-label="صف انتظار"
        :done-count="readyCount || null" done-count-label="نتیجه آماده" dated />

      <template v-if="tab === 'queue'">
        <CallNotice :desks="[calls]" :numbers="false" />
        <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
        <div v-else-if="list.length === 0" class="xr-state">مریضی در صف نیست.</div>
        <div v-if="!loading && list.length" class="dq-cols" aria-hidden="true"><span>نوبت</span><span>نام مریض</span><span>وضعیت</span><span>عملیات</span></div>
        <ol v-if="!loading && list.length" class="dq" aria-label="صف مریضان">
          <li v-for="{ row, kind } in list" :key="row.id" class="dq__row" :class="`dq__row--${kind}`">
            <div class="dq__num">
              <strong>{{ quantity(row.queueNumber) }}</strong>
              <small v-if="morningLeft(row)" class="dq__shift dq__shift--left">صبح</small>
            </div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName }}</div>
              <div class="dq__detail">
                <span v-if="detail(row)">{{ detail(row) }}</span>
              </div>
            </div>
            <div class="dq__state">{{ labels[kind] }}</div>
            <div class="dq__actions">
              <RouterLink :to="`/chart/${row.id}`" class="btn" :class="kind === 'with' || kind === 'called' ? 'btn-primary' : 'btn-soft'">
                {{ kind === 'with' ? 'ادامه معاینه' : kind === 'called' ? 'باز کردن پرونده' : 'پرونده' }}
              </RouterLink>
              <QueueCallActions v-if="kind !== 'with'" :desk="calls" :visit-id="row.id" />
            </div>
          </li>
        </ol>
      </template>

      <template v-else>
        <div v-if="seenLoading" class="xr-state" role="status">در حال بارگذاری...</div>
        <div v-else-if="seenList.length === 0" class="xr-state">در این روز مریضی دیده نشده است.</div>
        <div v-if="!seenLoading && seenList.length" class="dq-cols" aria-hidden="true"><span>نوبت</span><span>نام مریض</span><span>وضعیت</span><span>عملیات</span></div>
        <ol v-if="!seenLoading && seenList.length" class="dq" aria-label="مریضان دیده‌شده">
          <li v-for="row in seenList" :key="row.id" class="dq__row" :class="{ 'dq__row--ready': row.resultReady }">
            <div class="dq__num">
              <strong>{{ quantity(row.queueNumber) }}</strong>
              <small v-if="seenMixed" class="dq__shift">{{ row.queueShift === 'Afternoon' ? 'چاشت' : 'صبح' }}</small>
            </div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName }}</div>
              <div class="dq__detail"><span v-if="detail(row)">{{ detail(row) }}</span></div>
            </div>
            <div class="dq__state">
              <span v-if="row.resultReady" class="dq__ready"><i class="ri-checkbox-circle-fill" aria-hidden="true"></i> {{ seenState(row) }}</span>
              <template v-else>{{ seenState(row) }}</template>
            </div>
            <div class="dq__actions"><RouterLink :to="`/chart/${row.id}`" class="btn" :class="row.resultReady ? 'btn-primary' : 'btn-soft'">پرونده</RouterLink></div>
          </li>
        </ol>
      </template>
    </section>

    <details v-if="tab === 'queue' && followUps.length" class="dq-more">
      <summary>مراجعات بعدی ({{ quantity(followUps.length) }})</summary>
      <ul>
        <li v-for="row in followUps" :key="row.id">
          <span>{{ formatDate(row.dueDate) }}</span>
          <RouterLink :to="`/patients/${row.patientId}`">{{ row.patientName }}</RouterLink>
          <span v-if="row.note">{{ row.note }}</span>
        </li>
      </ul>
    </details>
  </div>
</template>
