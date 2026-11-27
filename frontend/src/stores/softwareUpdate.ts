import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, errorMessage } from '@/api/http'
import { askConfirm } from '@/components/confirm'

export interface SoftwareUpdateStatus {
  configured: boolean
  state: string
  currentVersion: string | null
  availableVersion: string | null
  runningVersion?: string | null
  checkedAt: string | null
  lastError: string | null
  packageVerified?: boolean
}

export const useSoftwareUpdateStore = defineStore('softwareUpdate', () => {
  const status = ref<SoftwareUpdateStatus | null>(null)
  const error = ref('')
  const notice = ref('')
  const approving = ref(false)
  const requestedVersion = ref<string | null>(null)
  let requestedAt = 0
  const loadedVersion = import.meta.env.VITE_RELEASE_VERSION as string | undefined
  let users = 0
  let loading = false
  let timer: ReturnType<typeof setTimeout> | undefined

  const ready = computed(() => !!status.value?.configured && !!status.value.availableVersion
    && status.value.availableVersion !== status.value.currentVersion
    && (['awaitingApproval', 'scheduled', 'retryScheduled'].includes(status.value.state)
      || (status.value.state === 'failed' && status.value.packageVerified === true)))
  const needsReload = computed(() => !!loadedVersion && !!status.value?.runningVersion
    && loadedVersion !== status.value.runningVersion)

  async function load() {
    if (loading) return
    loading = true
    clearTimeout(timer)
    try {
      const next = await api<SoftwareUpdateStatus>('/settings/software-update')
      status.value = next
      error.value = ''
      if (next.currentVersion === requestedVersion.value || next.state === 'failed'
        || (next.state === 'awaitingApproval' && requestedAt > 0 && Date.now() - requestedAt > 10 * 60_000)) {
        requestedVersion.value = null
        approving.value = false
      }
    } catch (cause) {
      error.value = errorMessage(cause)
    } finally {
      loading = false
      if (users > 0) timer = setTimeout(load, document.hidden ? 60_000 : 15_000)
    }
  }

  function visible() { if (!document.hidden) void load() }
  function start() {
    if (++users !== 1) return
    document.addEventListener('visibilitychange', visible)
    void load()
  }
  function stop() {
    users = Math.max(0, users - 1)
    if (users) return
    clearTimeout(timer)
    document.removeEventListener('visibilitychange', visible)
    status.value = null
    error.value = ''
    notice.value = ''
    approving.value = false
    requestedVersion.value = null
  }

  async function approve() {
    const version = status.value?.availableVersion
    if (!version || !ready.value || approving.value) return
    approving.value = true
    const agreed = await askConfirm('پیش از نصب، کارهای باز همهٔ کاربران را ذخیره کنید. از اطلاعات بکاپ گرفته و بازیابی آن آزمایش می‌شود؛ سپس سیستم برای نصب و بررسی سلامت موقتاً قطع می‌شود.',
      { title: 'نصب نسخهٔ جدید الشفا', confirmLabel: 'کارها ذخیره است؛ نصب کن' })
    if (!agreed) { approving.value = false; return }
    error.value = ''
    try {
      await api('/settings/software-update/approve', { method: 'POST', body: { version } })
      requestedVersion.value = version
      requestedAt = Date.now()
      notice.value = 'درخواست نصب ثبت شد. آماده‌سازی و بکاپ آغاز می‌شود؛ تا پایان نصب از ثبت کار جدید خودداری کنید.'
      void load()
    } catch (cause) {
      error.value = errorMessage(cause)
      approving.value = false
    }
  }

  function reload() { window.location.reload() }
  return { status, error, notice, approving, ready, needsReload, start, stop, load, approve, reload }
})
