<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useSoftwareUpdateStore } from '@/stores/softwareUpdate'
import { formatDateTime } from '@/utils/date'

const auth = useAuthStore()
const update = useSoftwareUpdateStore()
const labels: Record<string, string> = {
  unconfigured: 'اتصال امن به ناشر هنوز آماده نشده است.', unknown: 'وضعیت آپدیت تأیید نشده است.',
  current: 'نرم‌افزار به‌روز است.', downloading: 'نسخهٔ جدید در حال دریافت است؛ پس از بررسی، دکمهٔ نصب نمایش داده می‌شود.',
  awaitingApproval: 'نسخهٔ جدید دریافت و امضای آن بررسی شده است؛ با دکمهٔ نصب می‌توانید آن را نصب کنید.',
  scheduled: 'نسخهٔ جدید آماده است؛ برای نصب دکمهٔ زیر را بزنید.',
  retryScheduled: 'نصب قبلی موفق نشد؛ پس از بررسی می‌توانید دوباره درخواست نصب بدهید.',
  installing: 'نصب و بررسی سلامت در حال اجراست.', installed: 'نسخهٔ جدید نصب و بررسی شده است.',
  failed: 'آپدیت موفق نشد؛ نتیجه را با مدیر فنی بررسی کنید.',
  compatibilityReviewRequired: 'سازگاری سینک نیاز به بررسی فنی دارد.',
}
onMounted(update.start)
onBeforeUnmount(update.stop)
</script>

<template>
  <section class="mb-4">
    <h3 class="h6 mb-2">آپدیت نرم‌افزار</h3>
    <p class="form-hint">نسخه‌های جدید هر یک دقیقه بررسی می‌شوند. نصب تنها پس از تأیید مدیر انجام می‌شود.</p>
    <div v-if="update.error" class="alert alert-danger" role="alert">{{ update.error }}</div>
    <div v-if="update.notice" class="alert alert-info" role="status">{{ update.notice }}</div>
    <template v-if="update.status">
      <p role="status">{{ labels[update.status.state] ?? labels.unknown }}</p>
      <div class="xr-meta mb-2">آخرین بررسی: {{ formatDateTime(update.status.checkedAt) }}</div>
      <p v-if="update.status.currentVersion" dir="auto">نسخهٔ فعلی: {{ update.status.currentVersion }}</p>
      <p v-if="update.ready" dir="auto">نسخهٔ آمادهٔ نصب: {{ update.status.availableVersion }}</p>
      <p v-if="update.status.lastError" class="text-danger">مدیر فنی باید نتیجهٔ سرویس آپدیت را بررسی کند.</p>
      <button v-if="auth.can('settings.edit') && update.ready" class="btn btn-primary" type="button" :disabled="update.approving" @click="update.approve">نصب نسخهٔ جدید</button>
      <button v-if="update.needsReload" class="btn btn-success" type="button" @click="update.reload">کارم ذخیره است؛ تازه‌سازی صفحه</button>
    </template>
  </section>
</template>
