<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useSoftwareUpdateStore } from '@/stores/softwareUpdate'

const auth = useAuthStore()
const update = useSoftwareUpdateStore()
onMounted(update.start)
onBeforeUnmount(update.stop)
</script>

<template>
  <div v-if="update.needsReload" class="alert alert-success mx-3 mt-3 mb-0 no-print" role="status">
    نسخهٔ جدید سیستم نصب شده است. پس از ذخیرهٔ کار باز، صفحه را تازه کنید.
    <button type="button" class="btn btn-success btn-sm ms-2" @click="update.reload">کارم ذخیره است؛ تازه‌سازی صفحه</button>
  </div>
  <div v-else-if="update.ready || update.approving || update.status?.state === 'installing'" class="alert alert-info mx-3 mt-3 mb-0 no-print" role="status" aria-live="polite">
    <template v-if="update.notice || update.status?.state === 'installing'">{{ update.notice || 'نسخهٔ جدید در حال نصب و بررسی سلامت است؛ کمی صبر کنید.' }}</template>
    <template v-else>
      نسخهٔ جدید الشفا <b dir="ltr">{{ update.status?.availableVersion }}</b> دریافت و تأیید شده و آمادهٔ نصب است.
      <button v-if="auth.can('settings.edit')" type="button" class="btn btn-primary btn-sm ms-2" :disabled="update.approving" @click="update.approve">نصب نسخهٔ جدید</button>
      <span v-else>برای نصب به مدیر سیستم اطلاع دهید.</span>
    </template>
    <div v-if="update.error" class="mt-2" role="alert">{{ update.error }}</div>
  </div>
</template>
