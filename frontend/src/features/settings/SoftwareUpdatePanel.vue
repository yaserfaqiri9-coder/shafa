<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'

interface UpdateStatus {
  configured: boolean
  state: string
  currentVersion: string | null
  availableVersion: string | null
  checkedAt: string | null
  lastError: string | null
  autoInstallWindow?: string | null
  failedVersion?: string | null
}
const auth = useAuthStore()
const status = ref<UpdateStatus | null>(null)
const error = ref('')
const notice = ref('')
const approving = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
const approvable = computed(() => !!status.value?.availableVersion && status.value.availableVersion !== status.value.currentVersion
  && ['awaitingApproval', 'scheduled', 'retryScheduled', 'failed'].includes(status.value.state))
const labels: Record<string, string> = {
  unconfigured: 'اتصال امن به ناشر هنوز آماده نشده است.', unknown: 'وضعیت آپدیت تأیید نشده است.',
  current: 'نرم‌افزار به‌روز است.', downloading: 'نسخه جدید در حال دریافت است.',
  awaitingApproval: 'نسخه جدید دریافت و امضای آن بررسی شده است؛ نصب نیاز به تأیید مدیر دارد.',
  scheduled: 'نسخه جدید دریافت و امضای آن بررسی شده است؛ شب‌هنگام خودکار نصب می‌شود.',
  retryScheduled: 'نصب قبلی ناموفق بود و نسخه قبلی برگردانده شد؛ شب بعد دوباره تلاش می‌شود.',
  installing: 'نصب در حال اجراست.', installed: 'نسخه جدید نصب و بررسی شده است.', failed: 'آپدیت ناموفق بود؛ بررسی مدیر فنی لازم است.',
}
async function load() {
  try { status.value = await api<UpdateStatus>('/settings/software-update') }
  catch (cause) { error.value = errorMessage(cause) }
  finally { clearTimeout(timer); timer = setTimeout(load, 30_000) }
}
async function approve() {
  if (!status.value?.availableVersion) return
  approving.value = true
  error.value = ''
  try {
    await api('/settings/software-update/approve', { method: 'POST', body: { version: status.value.availableVersion } })
    notice.value = 'تأیید نصب ثبت شد. سیستم برای پشتیبان‌گیری، آزمایش بازیابی و نصب موقتاً از دسترس خارج می‌شود.'
  } catch (cause) { error.value = errorMessage(cause); approving.value = false }
}
onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <section class="mb-4">
    <h3 class="h6 mb-2">آپدیت نرم‌افزار</h3>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-info" role="status">{{ notice }}</div>
    <template v-if="status">
      <p role="status">{{ labels[status.state] ?? labels.unknown }}</p>
      <div class="xr-meta mb-2">آخرین بررسی: {{ formatDateTime(status.checkedAt) }}</div>
      <p v-if="status.currentVersion" dir="auto">نسخه فعلی: {{ status.currentVersion }}</p>
      <p v-if="status.availableVersion && approvable" dir="auto">نسخه آماده نصب: {{ status.availableVersion }}<span v-if="status.autoInstallWindow && status.state !== 'awaitingApproval'"> (نصب خودکار {{ status.autoInstallWindow }})</span></p>
      <p v-if="status.lastError" class="text-danger">مدیر فنی باید نتیجه سرویس آپدیت را بررسی کند.</p>
      <template v-if="auth.can('settings.edit') && approvable">
        <p class="form-hint">در ساعت کم‌کار تأیید کنید؛ کاربران تا پایان بررسی سلامت نمی‌توانند با سیستم کار کنند.</p>
        <button class="btn btn-outline-primary" type="button" :disabled="approving" @click="approve">نصب همین حالا: نسخه {{ status.availableVersion }}</button>
      </template>
    </template>
  </section>
</template>
