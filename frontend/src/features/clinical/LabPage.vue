<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { testOptions } from '@/api/lookups'
import { useLiveRefresh } from '@/api/realtime'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import { fetchAll } from '@/api/paged'
import { createQueueSearch } from '@/features/visits/queueTools'
import { useCallDesk } from '@/features/calls/useCallDesk'
import PatientPicker from '@/features/patients/PatientPicker.vue'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'
import ClinicalQueueHeader from './queue/ClinicalQueueHeader.vue'
import { callStateLabels, callStateOf, sortByKind } from './queue/queueState'
import type { DiagnosticOrder } from './types'

const auth = useAuthStore()
const imagingCalls = useCallDesk('Imaging')
const labCalls = useCallDesk('Lab')
// Refraction has its own queue and form. Only open work, or one day's results, is ever loaded.
const tab = ref<'queue' | 'done'>('queue')
const date = ref<string | null>(todayIso())
const all = ref<DiagnosticOrder[]>([])
const term = ref('')
const search = createQueueSearch(() => 'lab')
watch(term, (value) => { search.draft.value = value; search.apply() })
const deskOf = (row: DiagnosticOrder) => (row.kind === 'Imaging' ? imagingCalls : labCalls)

type Kind = 'with' | 'called' | 'next' | 'waiting' | 'absent' | 'blocked' | 'done'
function kindOf(row: DiagnosticOrder): Kind {
  if (row.status === 'ResultReady') return 'done'
  if (row.status === 'InProgress') return 'with'
  if (row.paid === false) return 'blocked'
  return callStateOf(deskOf(row), row.visitId) ?? 'waiting'
}
const labels: Record<Kind, string> = { ...callStateLabels, with: 'در جریان', blocked: 'منتظر پرداخت', done: 'نتیجه آماده' }
const rows = computed(() => sortByKind(search.filterRows(all.value).map((row) => ({ row, kind: kindOf(row) }))))
const waitingCount = computed(() => rows.value.filter((i) => i.kind === 'next' || i.kind === 'waiting').length)
const detail = (row: DiagnosticOrder) => [row.testName, row.note, row.orderNumber].filter(Boolean).join(' · ')
const loading = ref(true)
const error = ref('')

async function load() {
  try {
    all.value = await fetchAll<DiagnosticOrder>(`/diagnostic-orders?excludeRefraction=true&${tab.value === 'queue' ? 'open=true' : `open=false&date=${date.value ?? todayIso()}`}`)
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

async function start(row: DiagnosticOrder) {
  error.value = ''
  try {
    await api(`/diagnostic-orders/${row.id}/start`, { method: 'POST', body: { version: row.version } })
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
}

// Done: the result is typed here and saved in one step; the order closes and the patient returns to the doctor.
const finishing = reactive({ row: null as DiagnosticOrder | null, text: '', saving: false, error: '' })

function openFinish(row: DiagnosticOrder) {
  Object.assign(finishing, { row, text: '', saving: false, error: '' })
}

async function saveFinish() {
  const row = finishing.row
  if (!row) return
  if (!finishing.text.trim()) {
    finishing.error = 'نتیجه را بنویسید.'
    return
  }
  finishing.saving = true
  finishing.error = ''
  try {
    await api(`/diagnostic-orders/${row.id}/result`, { method: 'POST', body: { resultText: finishing.text, version: row.version } })
    finishing.row = null
    await load()
  } catch (cause) {
    finishing.error = errorMessage(cause)
    // Retry must carry the fresh version, or a stale row fails again.
    await load()
    const latest = all.value.find((o) => o.id === row.id)
    if (latest && latest.status !== 'ResultReady') finishing.row = latest
  } finally {
    finishing.saving = false
  }
}

const tests = ref<ComboOption[]>([])
const fresh = reactive({ open: false, patientId: null as number | null, testId: null as number | null, note: '', saving: false, error: '', key: '' })

async function openNew() {
  Object.assign(fresh, { open: true, patientId: null, testId: null, note: '', saving: false, error: '', key: newIdempotencyKey() })
  try {
    tests.value = await testOptions()
  } catch (cause) {
    fresh.error = errorMessage(cause)
  }
}

async function saveNew() {
  if (!fresh.patientId) {
    fresh.error = 'مریض را انتخاب کنید.'
    return
  }
  fresh.saving = true
  fresh.error = ''
  try {
    await api('/diagnostic-orders', { method: 'POST', idempotencyKey: fresh.key, body: { patientId: fresh.patientId, testId: fresh.testId, note: fresh.note } })
    fresh.open = false
    await load()
  } catch (cause) {
    fresh.error = errorMessage(cause)
    if (cause instanceof ApiError && cause.status !== 0) fresh.key = newIdempotencyKey()
  } finally {
    fresh.saving = false
  }
}

useLiveRefresh(['PatientCallChanged', 'DiagnosticOrderCreated', 'DiagnosticResultReady', 'PaymentReceived'], load)
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

    <section class="dq-page">
      <ClinicalQueueHeader v-model:tab="tab" v-model:term="term" v-model:date="date" title="آزمایشگاه و عکس‌برداری" queue-label="درخواست‌های در جریان" done-label="نتیجه‌های آماده" :count="tab === 'queue' ? waitingCount : null" dated :shift-filter="false">
        <template v-if="auth.can('diagnostics.create')" #actions>
          <button type="button" class="btn btn-soft" @click="openNew"><i class="ri-add-line" aria-hidden="true"></i> درخواست مستقل</button>
        </template>
      </ClinicalQueueHeader>

      <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
      <div v-else-if="rows.length === 0" class="xr-state">{{ tab === 'queue' ? 'درخواستی در جریان نیست.' : 'در این روز نتیجه‌ای ثبت نشده است.' }}</div>
      <div v-if="!loading && rows.length" class="dq-cols" aria-hidden="true"><span>نوبت</span><span>نام مریض</span><span>وضعیت</span><span>عملیات</span></div>
      <ol v-if="!loading && rows.length" class="dq" aria-label="درخواست‌های آزمایش">
        <li v-for="{ row, kind } in rows" :key="row.id" class="dq__row" :class="`dq__row--${kind}`">
          <div class="dq__num"><strong>{{ row.queueNumber ? quantity(row.queueNumber) : '—' }}</strong></div>
          <div class="dq__who">
            <div class="dq__name">{{ row.patientName }}</div>
            <div class="dq__detail"><span>{{ detail(row) }}</span><span>{{ formatDateTime(row.createdAt) }}</span></div>
          </div>
          <div class="dq__state">{{ labels[kind] }}</div>
          <div class="dq__actions">
            <template v-if="kind === 'done'">
              <RouterLink v-if="auth.can('diagnostics.print')" :to="`/print/lab/${row.id}`" target="_blank" class="btn btn-soft">چاپ جواب</RouterLink>
              <RouterLink v-if="auth.can('diagnostics.approve')" :to="`/lab/${row.id}/result`" class="btn btn-soft">اصلاح نتیجه</RouterLink>
            </template>
            <template v-else-if="kind !== 'blocked'">
              <button v-if="auth.can('diagnostics.approve')" type="button" class="btn btn-primary" @click="openFinish(row)"><i class="ri-check-line" aria-hidden="true"></i> انجام شد</button>
              <button v-else-if="row.status === 'Requested' && auth.can('diagnostics.edit')" type="button" class="btn btn-soft" @click="start(row)">شروع</button>
            </template>
          </div>
        </li>
      </ol>
    </section>
  </div>

  <UiModal v-if="finishing.row" :title="`انجام شد — ${finishing.row.patientName}`" @close="finishing.row = null">
    <form @submit.prevent="saveFinish">
      <p class="form-hint">{{ detail(finishing.row) }}</p>
      <div class="mb-3">
        <label class="form-label" for="l-result">نتیجه</label>
        <textarea id="l-result" v-model="finishing.text" class="form-control" rows="6" maxlength="4000" required></textarea>
      </div>
      <div class="mb-3"><AttachmentPanel owner-type="DiagnosticOrder" :owner-id="finishing.row.id" :can-upload="auth.can('diagnostics.edit')" /></div>
      <div v-if="finishing.error" class="alert alert-danger" role="alert">{{ finishing.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="finishing.saving">ثبت و پایان</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="fresh.open" title="درخواست مستقل آزمایش" @close="fresh.open = false">
    <form @submit.prevent="saveNew">
      <div class="mb-3"><PatientPicker v-model="fresh.patientId" required /></div>
      <div class="mb-3"><UiComboBox v-model="fresh.testId" label="آزمایش" :options="tests" required chip /></div>
      <div class="mb-3">
        <label class="form-label" for="l-note">توضیح</label>
        <input id="l-note" v-model="fresh.note" class="form-control" maxlength="500" />
      </div>
      <div v-if="fresh.error" class="alert alert-danger" role="alert">{{ fresh.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="fresh.saving">ثبت درخواست</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>
