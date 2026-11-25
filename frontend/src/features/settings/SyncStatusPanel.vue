<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { latinDigits, formatDateTime } from '@/utils/date'

interface SyncStatus {
  mode: 'local' | 'mirror'
  enabled: boolean
  configured: boolean
  configurationProblem?: string | null
  lastSuccessAt: string | null
  lastAttemptAt: string | null
  lastError: string | null
  pending: number | null
  intervalMinutes: number
  connected: boolean | null
}

const auth = useAuthStore()
const status = ref<SyncStatus | null>(null)
const error = ref('')
const notice = ref('')
const saving = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined

async function load() {
  try {
    status.value = await api<SyncStatus>('/sync/status')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    clearTimeout(timer)
    timer = setTimeout(load, 30_000)
  }
}

async function runNow() {
  notice.value = ''
  try {
    await api('/sync/run', { method: 'POST' })
    notice.value = 'درخواست همگام‌سازی ثبت شد؛ نتیجه پس از پایان نمایش داده می‌شود.'
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  }
}

async function setEnabled(event: Event) {
  const checked = (event.target as HTMLInputElement).checked
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    status.value = await api<SyncStatus>('/sync/enabled', { method: 'PUT', body: { enabled: checked } })
  } catch (cause) {
    error.value = errorMessage(cause)
    ;(event.target as HTMLInputElement).checked = status.value?.enabled ?? false
  } finally {
    saving.value = false
  }
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <section class="mb-4">
    <h3 class="h6 mb-2">همگام‌سازی با نسخه آنلاین</h3>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
    <template v-if="status && status.mode === 'local'">
      <label v-if="auth.can('settings.edit')" class="form-check mb-3">
        <input class="form-check-input" type="checkbox" :checked="status.enabled" :disabled="saving" @change="setEnabled" />
        <span class="form-check-label">فعال‌سازی همگام‌سازی آنلاین</span>
      </label>
      <div v-if="!status.enabled" class="alert alert-secondary" role="status">
        {{ status.configured ? 'همگام‌سازی خاموش است؛ تغییرات محلی محفوظ می‌مانند.' : status.configurationProblem || 'اتصال امن اولیه هنوز تأیید نشده است؛ آماده‌سازی یک‌باره توسط مدیر فنی لازم است.' }}
      </div>
      <template v-else>
        <p class="form-hint">
          تغییرات هر {{ latinDigits(status.intervalMinutes) }} دقیقه به آنلاین فرستاده می‌شود و درخواست‌های آنلاین برای تأیید و اجرا به سرور محلی می‌رسند.
          اگر اینترنت قطع باشد، تغییرات نگه داشته می‌شوند و پس از وصل‌شدن خودکار فرستاده می‌شوند.
        </p>
        <div v-if="status.lastError" class="alert alert-warning" role="status">
          آخرین تلاش ناموفق بود: <span dir="auto">{{ status.lastError }}</span>
        </div>
        <div class="row g-3 mb-3">
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین همگام‌سازی موفق</div><strong>{{ formatDateTime(status.lastSuccessAt) }}</strong></div>
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">آخرین تلاش</div><strong>{{ formatDateTime(status.lastAttemptAt) }}</strong></div>
          <div class="col-sm-6 col-lg-3"><div class="xr-meta">تغییرات در انتظار</div><strong>{{ latinDigits(status.pending ?? 0) }}</strong></div>
          <div class="col-sm-6 col-lg-3">
            <div class="xr-meta">ارتباط</div>
            <strong :class="status.connected ? 'text-success' : 'text-danger'">{{ status.connected === null ? '—' : status.connected ? 'وصل' : 'قطع' }}</strong>
          </div>
        </div>
        <button v-if="auth.can('settings.edit')" type="button" class="btn btn-outline-primary" @click="runNow">همگام‌سازی حالا</button>
      </template>
    </template>
  </section>
</template>
