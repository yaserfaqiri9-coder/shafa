<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import type { VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { shamsiPaper } from '@/features/print/paper'
import PatientBanner from './PatientBanner.vue'
import RefractionPrescription from './RefractionPrescription.vue'
import { prescriptionForm, prescriptionRequest } from './refractionPrescription'
import type { Refraction } from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ visitId: string }>()
const auth = useAuthStore()
const visit = ref<VisitRow | null>(null)
const record = ref<Refraction | null>(null)
const form = ref(prescriptionForm())
const amending = ref(false)
const amendReason = ref('')
const loading = ref(true)
const saving = ref(false)
const sending = ref(false)
const error = ref('')
const notice = ref('')
const sendToOptical = ref(false)
const returnToDoctor = ref(true)
const completed = computed(() => !!record.value?.completedAt)
const canEdit = computed(() => auth.can('preexam.edit') && (!completed.value || amending.value))
const { dirty, markSaved, applying } = useUnsavedGuard(form)

function fill(value: Refraction | null) {
  record.value = value
  applying(() => { form.value = prescriptionForm(value) })
}
let loadNumber = 0
async function load() {
  const current = ++loadNumber
  loading.value = true
  error.value = ''
  notice.value = ''
  amending.value = false
  amendReason.value = ''
  visit.value = null
  fill(null)
  try {
    const [loadedVisit, existing] = await Promise.all([
      api<VisitRow>(`/visits/${props.visitId}`), api<Refraction | undefined>(`/refractions/${props.visitId}`),
    ])
    if (current !== loadNumber) return
    visit.value = loadedVisit
    fill(existing ?? null)
  } catch (cause) {
    if (current === loadNumber) error.value = errorMessage(cause)
  } finally {
    if (current === loadNumber) loading.value = false
  }
}

async function save(complete: boolean) {
  if (!canEdit.value || loading.value || saving.value) return
  const savingVisitId = props.visitId
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const body = prescriptionRequest(form.value, record.value, amending.value ? amendReason.value : undefined)
    if (body.measurements.length > 1000) {
      throw new Error('اندازه‌گیری‌های قبلی همراه انتخاب‌های عدسی از ظرفیت ذخیره بیشتر است. برای حفظ سوابق، با مدیر سیستم تماس بگیرید.')
    }
    const saved = await api<Refraction>(`/refractions/${savingVisitId}`, { method: 'PUT', body })
    if (props.visitId !== savingVisitId) return
    // Keep the new version even when the subsequent completion fails.
    record.value = saved
    if (complete && !saved.completedAt) {
      await api(`/refractions/${savingVisitId}/complete`, {
        method: 'POST', body: { returnToDoctor: returnToDoctor.value, sendToOptical: sendToOptical.value },
      })
      const completedRecord = await api<Refraction>(`/refractions/${savingVisitId}`)
      if (props.visitId !== savingVisitId) return
      fill(completedRecord)
      notice.value = returnToDoctor.value ? 'نسخه ذخیره و تکمیل شد؛ مریض به صف داکتر برگشت.' : 'نسخه ذخیره و تکمیل شد.'
    } else {
      fill(saved)
      notice.value = amending.value ? 'اصلاحیه ثبت شد.' : 'ذخیره شد.'
    }
    amending.value = false
    amendReason.value = ''
    markSaved()
  } catch (cause) {
    error.value = cause instanceof Error && !(cause as { code?: string }).code ? cause.message : errorMessage(cause)
  } finally {
    saving.value = false
  }
}

async function sendNow() {
  sending.value = true
  error.value = ''
  try {
    await api(`/refractions/${props.visitId}/send-to-optical`, { method: 'POST' })
    record.value = await api<Refraction>(`/refractions/${props.visitId}`)
    notice.value = 'نتیجه به بخش عینک فرستاده شد.'
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    sending.value = false
  }
}
watch(() => props.visitId, load, { immediate: true })
</script>

<template>
  <div class="d-grid gap-3">
    <PatientBanner v-if="visit" :visit="visit" />
    <p v-if="loading" role="status">در حال دریافت نسخه...</p>
    <div v-if="error" class="alert alert-danger mb-0" role="alert">{{ error }}</div>
    <form v-if="visit && !loading" class="xr-card" @submit.prevent="save(false)">
      <div class="xr-card__header">
        <h2 class="xr-card__title">نسخه عینک</h2>
        <div class="d-flex gap-2">
          <RouterLink v-if="record && auth.can('preexam.view') && !dirty && !saving" :to="`/print/refraction/${visitId}`" target="_blank" class="btn btn-soft">چاپ نسخه</RouterLink>
          <button v-else type="button" class="btn btn-soft" disabled>چاپ نسخه</button>
          <RouterLink :to="auth.can('preexam.edit') ? { name: 'refraction-queue' } : `/chart/${visitId}`" class="btn btn-soft">بازگشت</RouterLink>
        </div>
      </div>
      <p v-if="completed && !amending" class="alert alert-primary m-3" role="status">نسخه تکمیل شده است. برای ویرایش، اصلاحیه با دلیل ثبت کنید.</p>
      <p v-if="dirty" class="mx-3 mb-0" role="status">تغییرات ذخیره نشده‌اند؛ پیش از چاپ ذخیره کنید.</p>
      <fieldset :disabled="saving || !canEdit" class="prescription-editor">
        <RefractionPrescription v-model="form" :patient-name="visit.patientName" :date="shamsiPaper(visit.visitDate)" :readonly="!canEdit" />
      </fieldset>
      <div v-if="amending" class="xr-action-bar">
        <input v-model="amendReason" class="form-control" minlength="3" maxlength="500" required placeholder="دلیل اصلاح" aria-label="دلیل اصلاح" />
        <button type="submit" class="btn btn-primary text-nowrap" :disabled="saving">ذخیره اصلاحیه</button>
        <button type="button" class="btn btn-soft-danger text-nowrap" :disabled="saving" @click="load">انصراف</button>
      </div>
      <div v-else-if="canEdit" class="xr-action-bar">
        <label class="form-check-label text-nowrap"><input v-model="sendToOptical" class="form-check-input ms-2" type="checkbox" :disabled="saving" />ارسال نتیجه به بخش عینک</label>
        <label class="form-check-label text-nowrap"><input v-model="returnToDoctor" class="form-check-input ms-2" type="checkbox" :disabled="saving" />برگشت مریض به صف داکتر</label>
        <button type="button" class="btn btn-primary" :disabled="saving" @click="save(true)">ذخیره و تکمیل</button>
        <button type="submit" class="btn btn-soft" :disabled="saving">ذخیره</button>
      </div>
      <div v-else-if="completed && auth.can('preexam.edit')" class="xr-action-bar">
        <button type="button" class="btn btn-soft" :disabled="sending" @click="amending = true">ثبت اصلاحیه</button>
        <button v-if="!record?.opticalRxNumber" type="button" class="btn btn-soft" :disabled="sending" @click="sendNow">ارسال به بخش عینک</button>
        <span v-else>نسخه بخش عینک: {{ record.opticalRxNumber }}</span>
      </div>
      <p v-if="notice" class="alert alert-success m-3" role="status">{{ notice }}</p>
    </form>
  </div>
</template>

<style scoped>
.prescription-editor { border: 0; margin: 0; padding: 0; overflow-x: auto; }
.prescription-editor :deep(.refraction-prescription) { min-width: 900px; }
</style>
