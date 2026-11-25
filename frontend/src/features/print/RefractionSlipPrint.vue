<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api/http'
import RefractionPrescription from '@/features/clinical/RefractionPrescription.vue'
import { prescriptionForm } from '@/features/clinical/refractionPrescription'
import type { Refraction } from '@/features/clinical/types'
import type { VisitRow } from '@/features/visits/types'
import { shamsiPaper, usePaperPage } from './paper'

const props = defineProps<{ visitId: string }>()
const artworkReady = ref(false)
const page = usePaperPage(async () => {
  const [visit, refraction] = await Promise.all([
    api<VisitRow>(`/visits/${props.visitId}`), api<Refraction | undefined>(`/refractions/${props.visitId}`),
  ])
  return { visit, refraction: refraction ?? null }
})
const form = computed(() => prescriptionForm(page.data.value?.refraction))
async function print() {
  await document.fonts.ready
  window.print()
}
onMounted(async () => {
  const artwork = new Image()
  artwork.src = '/print/alshafa-refraction-reference.png'
  await Promise.all([page.load(), artwork.decode().then(() => { artworkReady.value = true }).catch(() => {
    page.error.value = 'تصویر نسخه دریافت نشد. اتصال را بررسی و صفحه را تازه کنید.'
  })])
})
</script>

<template>
  <div class="refraction-print">
    <div v-if="page.error.value" class="alert alert-danger m-3 no-print" role="alert">{{ page.error.value }}</div>
    <p v-else-if="page.loading.value" class="no-print" role="status">در حال آماده‌سازی نسخه...</p>
    <p v-else-if="!page.data.value?.refraction" class="alert alert-warning m-3 no-print" role="alert">برای این مراجعه نسخه‌ای ثبت نشده است.</p>
    <template v-else-if="page.data.value">
      <div class="refraction-tools no-print">
        <button type="button" class="btn btn-primary" :disabled="!artworkReady" @click="print">چاپ نسخه</button>
        <span>چاپ A5 افقی، مقیاس ۱۰۰٪؛ تناسب تصویر اصلی حفظ شده است.</span>
      </div>
      <div class="refraction-preview">
        <RefractionPrescription :model-value="form" :patient-name="page.data.value.visit.patientName" :date="shamsiPaper(page.data.value.visit.visitDate)" readonly />
      </div>
    </template>
  </div>
</template>

<style scoped>
.refraction-tools { display: flex; align-items: center; gap: 16px; padding: 16px; flex-wrap: wrap; }
.refraction-preview { width: 100%; overflow-x: auto; }
.refraction-preview :deep(.refraction-prescription) { width: 196mm; margin: 0 auto; }
@media print {
  @page { size: A5 landscape; margin: 0; }
  .refraction-print { width: 210mm; height: 148mm; margin: 0; padding: 0.5mm 0; box-sizing: border-box; }
  .refraction-preview { overflow: visible; }
  .refraction-preview :deep(.refraction-prescription) { width: 196mm; height: 147mm; margin: 0 auto; }
}
</style>
