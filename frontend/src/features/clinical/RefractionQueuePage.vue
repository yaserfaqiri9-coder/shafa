<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { createQueueSearch, createShiftFilter } from '@/features/visits/queueTools'
import { todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'
import ClinicalQueueHeader from './queue/ClinicalQueueHeader.vue'
import QueueCallActions from './queue/QueueCallActions.vue'
import { callStateLabels, callStateOf, sortByKind } from './queue/queueState'

interface QueueRow {
  id: number
  visitId: number
  queueNumber: number
  queueShift: 'Morning' | 'Afternoon'
  queueRound: number
  patientFileNumber: string
  patientName: string
  status: string
  paid: boolean
}

const calls = useCallDesk('Refraction')
const tab = ref<'queue' | 'done'>('queue')
const term = ref('')
const date = ref<string | null>(todayIso())
const shift = ref<'all' | 'Morning' | 'Afternoon'>('all')
const search = createQueueSearch(() => 'refraction')
watch(term, (value) => { search.draft.value = value; search.apply() })
const shifts = createShiftFilter()
const rows = ref<QueueRow[]>([])
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    const query = tab.value === 'done' ? `?done=true&date=${date.value ?? todayIso()}` : ''
    rows.value = await api<QueueRow[]>(`/refractions/queue${query}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

type Kind = 'with' | 'called' | 'next' | 'waiting' | 'absent' | 'blocked'
/** Not paid yet: listed so the desk knows the patient is coming, but nothing can be done until the cashier. */
const kindOf = (row: QueueRow): Kind => (row.status === 'InProgress' ? 'with' : !row.paid ? 'blocked' : callStateOf(calls, row.visitId) ?? 'waiting')
const labels: Record<Kind, string> = { ...callStateLabels, with: 'در جریان', blocked: 'منتظر پرداخت' }
const list = computed(() => sortByKind(search.filterRows(rows.value).map((row) => ({ row, kind: kindOf(row) }))))
const waitingCount = computed(() => list.value.filter((i) => i.kind === 'next' || i.kind === 'waiting').length)
const morningLeft = (row: QueueRow) => shifts.tag(rows.value, row)?.text === 'از صبح'

const seenList = computed(() => search.filterRows(rows.value).filter((r) => shift.value === 'all' || r.queueShift === shift.value))
const seenMixed = computed(() => new Set(rows.value.map((r) => r.queueShift)).size > 1)

watch([tab, date], () => {
  loading.value = true
  rows.value = []
  load()
})
useLiveRefresh(['PatientCallChanged', 'DiagnosticOrderCreated', 'PaymentReceived', 'VisitStatusChanged', 'DiagnosticResultReady'], load)
onMounted(load)
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <section class="dq-page">
      <ClinicalQueueHeader v-model:tab="tab" v-model:term="term" v-model:date="date" v-model:shift="shift" title="رفرکشن" :count="tab === 'queue' ? waitingCount : null" dated />

      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>

      <template v-else-if="tab === 'queue'">
        <CallNotice :desks="[calls]" :numbers="false" />
        <div v-if="list.length === 0" class="xr-state">درخواست رفرکشن نیست.</div>
        <ol v-else class="dq" aria-label="صف رفرکشن">
          <li v-for="{ row, kind } in list" :key="row.id" class="dq__row" :class="`dq__row--${kind}`">
            <div class="dq__num">
              <strong>{{ quantity(row.queueNumber) }}</strong>
              <small v-if="morningLeft(row)" class="dq__shift dq__shift--left">صبح</small>
            </div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName }}</div>
              <div class="dq__detail"><span>{{ row.patientFileNumber }}</span></div>
            </div>
            <div class="dq__state">{{ labels[kind] }}</div>
            <div class="dq__actions">
              <template v-if="kind !== 'blocked'">
                <RouterLink :to="`/refraction/${row.visitId}`" class="btn" :class="kind === 'with' || kind === 'called' ? 'btn-primary' : 'btn-soft'">
                  {{ kind === 'with' ? 'ادامه رفرکشن' : 'ثبت رفرکشن' }}
                </RouterLink>
                <QueueCallActions v-if="kind !== 'with'" :desk="calls" :visit-id="row.visitId" />
              </template>
            </div>
          </li>
        </ol>
      </template>

      <template v-else>
        <div v-if="seenList.length === 0" class="xr-state">در این روز رفرکشنی ثبت نشده است.</div>
        <ol v-else class="dq" aria-label="رفرکشن‌های تکمیل‌شده">
          <li v-for="row in seenList" :key="row.id" class="dq__row">
            <div class="dq__num">
              <strong>{{ quantity(row.queueNumber) }}</strong>
              <small v-if="seenMixed" class="dq__shift">{{ row.queueShift === 'Afternoon' ? 'چاشت' : 'صبح' }}</small>
            </div>
            <div class="dq__who">
              <div class="dq__name">{{ row.patientName }}</div>
              <div class="dq__detail"><span>{{ row.patientFileNumber }}</span></div>
            </div>
            <div class="dq__state">تکمیل</div>
            <div class="dq__actions"><RouterLink :to="`/refraction/${row.visitId}`" class="btn btn-soft">مشاهده رفرکشن</RouterLink></div>
          </li>
        </ol>
      </template>
    </section>
  </div>
</template>
