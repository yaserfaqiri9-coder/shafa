import { computed, defineComponent, h, ref } from 'vue'
import { useAuthStore } from '@/stores/auth'

export const OnlineDatabaseBackupPanel = defineComponent({
  name: 'OnlineDatabaseBackupPanel',
  setup() {
    const auth = useAuthStore()
    const allowed = computed(() => auth.user?.online && auth.can('settings.edit') && auth.user.roles.some(role => role === 'Manager' || role === 'SuperAdmin'))
    const busy = ref(false)
    const error = ref('')
    const notice = ref('')
    async function download() {
      if (busy.value || !allowed.value) return
      busy.value = true
      error.value = ''
      notice.value = ''
      try {
        const readToken = () => {
          const value = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1]
          return value ? decodeURIComponent(value) : ''
        }
        if (!readToken()) await fetch('/api/auth/csrf', { credentials: 'same-origin' })
        const response = await fetch('/api/settings/database-backup/download', {
          method: 'POST', credentials: 'same-origin', headers: { 'X-XSRF-TOKEN': readToken() },
        })
        if (!response.ok) {
          const problem = await response.json().catch(() => null) as { title?: string } | null
          throw new Error(problem?.title ?? 'بکاپ آماده نشد. دوباره تلاش کنید.')
        }
        if (!response.headers.get('content-type')?.includes('application/gzip')) throw new Error('فایل بکاپ معتبر دریافت نشد. دوباره تلاش کنید.')
        const blob = await response.blob()
        if (!blob.size) throw new Error('فایل بکاپ خالی دریافت شد. دوباره تلاش کنید.')
        const disposition = response.headers.get('content-disposition') ?? ''
        const filename = /filename="([A-Za-z0-9_.-]+\.sql\.gz)"/.exec(disposition)?.[1] ?? 'alshifa-database.sql.gz'
        const url = URL.createObjectURL(blob)
        const link = document.createElement('a')
        link.href = url
        link.download = filename
        document.body.append(link)
        link.click()
        link.remove()
        setTimeout(() => URL.revokeObjectURL(url), 60_000)
        notice.value = 'بکاپ آماده شد و دانلود فایل آغاز گردید.'
      } catch (cause) {
        error.value = cause instanceof Error ? cause.message : 'ارتباط با سرور برقرار نشد. دوباره تلاش کنید.'
      } finally { busy.value = false }
    }
    return () => h('section', { class: 'online-database-backup', 'aria-label': 'بکاپ دستی دیتابیس' }, [
      h('h3', { class: 'h6 mb-2' }, 'بکاپ دستی دیتابیس'),
      h('p', { class: 'form-hint' }, 'تمام جدول‌ها و داده‌های دیتابیس آنلاین در یک فایل SQL فشرده دانلود می‌شود. به تنظیمات اضافی نیاز نیست.'),
      h('p', { class: 'form-hint' }, 'فایل‌های تنظیمات سرور و فایل‌های ضمیمه در این بکاپ شامل نیستند.'),
      error.value ? h('div', { class: 'alert alert-danger', role: 'alert' }, error.value) : null,
      notice.value ? h('div', { class: 'alert alert-success', role: 'status' }, notice.value) : null,
      allowed.value ? h('button', { type: 'button', class: 'btn btn-primary', disabled: busy.value, 'aria-busy': busy.value, onClick: download }, [
        h('i', { class: busy.value ? 'ri-loader-4-line' : 'ri-download-cloud-2-line', 'aria-hidden': 'true' }),
        busy.value ? ' در حال آماده‌سازی بکاپ…' : ' دانلود بکاپ دیتابیس',
      ]) : h('p', { class: 'text-muted' }, 'دریافت بکاپ فقط برای مدیر دارای صلاحیت تنظیمات در دسترس است.'),
    ])
  },
})
