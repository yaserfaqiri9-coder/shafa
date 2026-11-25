<script setup lang="ts">
import { askConfirm } from '@/components/confirm'
import { computed, onMounted, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { fetchAll } from '@/api/paged'
import { createQueueSearch } from '@/features/visits/queueTools'
import { useLiveRefresh } from '@/api/realtime'
import CallNotice from '@/features/calls/CallNotice.vue'
import { useCallDesk } from '@/features/calls/useCallDesk'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'
import OperationsPanel from './OperationsPanel.vue'
import SurgeryPackagesPanel from './SurgeryPackagesPanel.vue'
import ClinicalQueueHeader from './queue/ClinicalQueueHeader.vue'
import QueueCallActions from './queue/QueueCallActions.vue'
import { callStateLabels, callStateOf, sortByKind } from './queue/queueState'
import { eyeLabels, surgeryStatusLabels, type SurgeryRow } from './types'

const auth = useAuthStore()
const calls = useCallDesk('Surgery')
// Only open cases, or the cases finished on one day, are ever loaded.
const all = ref<SurgeryRow[]>([])
const date = ref<string | null>(todayIso())
const search = createQueueSearch(() => 'surgery')
const loading = ref(true)
const error = ref('')
/** Price lists and packages are settings, opened from the header and left with one button. */
const panel = ref<'cases' | 'packages' | 'operations'>('cases')
const tab = ref<'queue' | 'done'>('queue')
const term = ref('')
watch(term, (value) => { search.draft.value = value; search.apply() })

type Kind = 'with' | 'called' | 'next' | 'waiting' | 'absent' | 'done'
function kindOf(row: SurgeryRow): Kind {
  if (tab.value === 'done') return 'done'
  if (row.status === 'InProgress') return 'with'
  return callStateOf(calls, row.visitId) ?? 'waiting'
}
/** A waiting case shows its own step (proposed, scheduled); the call states replace it only while they matter. */
const stateOf = (row: SurgeryRow, kind: Kind) => (kind === 'called' || kind === 'next' || kind === 'absent' ? callStateLabels[kind] : surgeryStatusLabels[row.status])
const rows = computed(() => sortByKind(search.filterRows(all.value).map((row) => ({ row, kind: kindOf(row) }))))
const waitingCount = computed(() => rows.value.filter((i) => i.kind === 'next' || i.kind === 'waiting').length)

async function load() {
  try {
    all.value = await fetchAll<SurgeryRow>(`/surgeries?${tab.value === 'queue' ? 'open=true' : `open=false&date=${date.value ?? todayIso()}`}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function changeStatus(row: SurgeryRow, next: string, select: HTMLSelectElement) {
  error.value = ''
  // One slip of the pointer on a list must not move a case: the step is named and confirmed first.
  if (!(await askConfirm(`پرونده ${row.caseNumber} به «${surgeryStatusLabels[next]}» تغییر کند؟`, { title: 'تغییر وضعیت عملیات', confirmLabel: surgeryStatusLabels[next], danger: next === 'Cancelled' }))) {
    select.value = ''
    return
  }
  try {
    await api(`/surgeries/${row.id}/status`, { method: 'POST', body: { status: next, version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

function canMove(next: string) {
  return next === 'Cancelled' ? auth.can('surgery.cancel') : auth.can('surgery.approve')
}

useLiveRefresh(['PatientCallChanged', 'SurgeryScheduled'], load)
onMounted(load)
watch([tab, date], () => {
  loading.value = true
  all.value = []
  return load()
})
</script>

<template>
  <div class="d-grid gap-3">
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>

    <section v-if="panel !== 'cases'" class="dq-page">
      <div class="dq-head">
        <h1 class="dq-title">{{ panel === 'operations' ? 'تعریف عملیات و قیمت' : 'پکیج‌های عملیات' }}</h1>
        <button type="button" class="btn btn-soft" @click="panel = 'cases'"><i class="ri-arrow-right-line" aria-hidden="true"></i> بازگشت به پرونده‌ها</button>
      </div>
      <OperationsPanel v-if="panel === 'operations'" />
      <SurgeryPackagesPanel v-else />
    </section>

    <section v-else class="dq-page">
      <ClinicalQueueHeader v-model:tab="tab" v-model:term="term" v-model:date="date" title="عملیات" queue-label="پرونده‌های در جریان" done-label="انجام‌شده" :count="tab === 'queue' ? waitingCount : null" dated :shift-filter="false">
        <template #actions>
          <button type="button" class="btn btn-quiet" @click="panel = 'operations'">تعریف عملیات و قیمت</button>
          <button type="button" class="btn btn-quiet" @click="panel = 'packages'">پکیج‌ها</button>
        </template>
      </ClinicalQueueHeader>

      <CallNotice v-if="tab === 'queue'" :desks="[calls]" :numbers="false" />
      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="rows.length === 0" class="xr-state">پرونده عملیاتی نیست.</div>
      <ol v-else class="dq" aria-label="پرونده‌های عملیات">
        <li v-for="{ row, kind } in rows" :key="row.id" class="dq__row" :class="`dq__row--${kind}`">
          <div class="dq__num"><strong>{{ row.queueNumber ? quantity(row.queueNumber) : '—' }}</strong></div>
          <div class="dq__who">
            <div class="dq__name">{{ row.patientName }}</div>
            <div class="dq__detail">
              <span>{{ row.surgeryType }} · {{ eyeLabels[row.eye] }}</span>
              <span v-if="row.scheduledAt">{{ formatDateTime(row.scheduledAt) }}</span>
              <span class="ltr-num">{{ row.caseNumber }}</span>
            </div>
          </div>
          <div class="dq__state">{{ stateOf(row, kind) }}</div>
          <div class="dq__actions">
            <RouterLink :to="`/surgery/${row.id}`" class="btn" :class="kind === 'with' || kind === 'called' ? 'btn-primary' : 'btn-soft'">جزئیات</RouterLink>
            <select
              v-if="auth.can('surgery.edit') && row.nextStatuses.some(canMove)"
              class="form-select"
              :aria-label="`تغییر وضعیت ${row.caseNumber}`"
              @change="changeStatus(row, ($event.target as HTMLSelectElement).value, $event.target as HTMLSelectElement)"
            >
              <option value="" selected disabled>تغییر وضعیت</option>
              <option v-for="next in row.nextStatuses.filter(canMove)" :key="next" :value="next">{{ surgeryStatusLabels[next] }}</option>
            </select>
            <QueueCallActions v-if="kind !== 'with' && kind !== 'done'" :desk="calls" :visit-id="row.visitId" />
          </div>
        </li>
      </ol>
    </section>
  </div>
</template>
