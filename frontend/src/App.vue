<script setup lang="ts">
import { bootError } from '@/router'
import { online } from '@/pwa'
import GlobalNotifications from '@/components/GlobalNotifications.vue'
import UiConfirmHost from '@/components/UiConfirmHost.vue'

function retry() {
  window.location.reload()
}
</script>

<template>
  <GlobalNotifications />
  <div v-if="!online" class="connection-status no-print" role="status">اتصال شبکه قطع است؛ برای مشاهده اطلاعات تازه و ثبت تغییرات، اتصال به سرور را برقرار کنید.</div>
  <div v-if="bootError" class="boot-error" role="alert">
    <i class="ri-error-warning-fill boot-error__icon" aria-hidden="true"></i>
    <p class="boot-error__message">{{ bootError }}</p>
    <button type="button" class="btn btn-primary" @click="retry">تلاش دوباره</button>
  </div>
  <RouterView v-else />
  <UiConfirmHost />
</template>

<style scoped>
.connection-status {
  position: fixed;
  inset-inline: 12px;
  bottom: max(12px, env(safe-area-inset-bottom));
  z-index: 1100;
  width: fit-content;
  max-width: calc(100% - 24px);
  margin-inline: auto;
  padding: 10px 16px;
  border: 1px solid #e6b85c;
  border-radius: 10px;
  background: #fffcf3;
  color: #3f414d;
  box-shadow: 0 4px 20px #0002;
  pointer-events: none;
}
.boot-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 16px;
  min-height: 100vh;
  padding: 16px;
  background: var(--xr-page-bg);
  text-align: center;
}

.boot-error__icon {
  color: var(--xr-orange);
  font-size: 48px;
}

.boot-error__message {
  margin: 0;
  font-size: 18px;
}
</style>
