<script setup lang="ts">
import { useNotifications, type NotificationTone } from '@/stores/notifications'

const { items, dismiss } = useNotifications()

const icons: Record<NotificationTone, string> = {
  success: 'ri-checkbox-circle-fill',
  danger: 'ri-error-warning-fill',
  warning: 'ri-alert-fill',
  info: 'ri-information-fill',
}
</script>

<template>
  <div class="global-notifications no-print" aria-live="polite" aria-relevant="additions">
    <TransitionGroup name="global-notification" tag="div" class="global-notifications__stack">
      <div v-for="item in items" :key="item.id" class="global-notification" :class="`global-notification--${item.tone}`" :role="item.tone === 'danger' ? 'alert' : 'status'">
        <i class="global-notification__icon" :class="icons[item.tone]" aria-hidden="true"></i>
        <p class="global-notification__message">{{ item.message }}</p>
        <button type="button" class="global-notification__close" aria-label="بستن پیام" @click="dismiss(item.id)">
          <i class="ri-close-line" aria-hidden="true"></i>
        </button>
        <span class="global-notification__timer" aria-hidden="true"></span>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.global-notifications {
  position: fixed;
  z-index: 2000;
  top: max(16px, env(safe-area-inset-top));
  right: max(16px, env(safe-area-inset-right));
  left: max(16px, env(safe-area-inset-left));
  display: flex;
  justify-content: center;
  pointer-events: none;
  direction: rtl;
}

.global-notifications__stack {
  display: grid;
  gap: 10px;
  width: min(520px, 100%);
}

.global-notification {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 11px;
  min-height: 58px;
  overflow: hidden;
  padding: 14px 16px 16px;
  border: 1px solid var(--notification-border);
  border-radius: 14px;
  background: var(--notification-bg);
  color: var(--notification-text);
  box-shadow: 0 14px 38px rgba(19, 37, 63, 0.18), 0 3px 10px rgba(19, 37, 63, 0.08);
  pointer-events: auto;
  backdrop-filter: blur(12px);
}

.global-notification--success { --notification-bg: rgba(244, 255, 247, 0.98); --notification-border: #b9e6c5; --notification-text: #155b2b; --notification-accent: #239447; }
.global-notification--danger { --notification-bg: rgba(255, 246, 245, 0.98); --notification-border: #f0c1bd; --notification-text: #822e28; --notification-accent: #d4473d; }
.global-notification--warning { --notification-bg: rgba(255, 251, 238, 0.98); --notification-border: #f0d58b; --notification-text: #765719; --notification-accent: #c58a18; }
.global-notification--info { --notification-bg: rgba(242, 250, 255, 0.98); --notification-border: #b8dced; --notification-text: #1b5270; --notification-accent: #2b82aa; }

.global-notification::before {
  position: absolute;
  inset-block: 0;
  inset-inline-start: 0;
  width: 4px;
  background: var(--notification-accent);
  content: '';
}

.global-notification__icon {
  flex: 0 0 auto;
  padding-top: 1px;
  color: var(--notification-accent);
  font-size: 21px;
}

.global-notification__message {
  flex: 1;
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  line-height: 1.65;
  text-align: start;
}

.global-notification__close {
  flex: 0 0 auto;
  width: 28px;
  height: 28px;
  margin: -3px -5px 0 0;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: currentColor;
  cursor: pointer;
  font-size: 18px;
  opacity: .7;
}

.global-notification__close:hover,
.global-notification__close:focus-visible { background: rgba(0, 0, 0, .06); opacity: 1; outline: none; }

.global-notification__timer {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  height: 3px;
  background: var(--notification-accent);
  opacity: .38;
  transform-origin: right;
  animation: global-notification-countdown 3s linear forwards;
}

.global-notification-enter-active,
.global-notification-leave-active { transition: opacity .16s ease, transform .16s ease; }
.global-notification-enter-from { opacity: 0; transform: translateY(-12px) scale(.98); }
.global-notification-leave-to { opacity: 0; transform: translateY(-8px) scale(.98); }

@keyframes global-notification-countdown { to { transform: scaleX(0); } }

@media (max-width: 575.98px) {
  .global-notifications { top: max(10px, env(safe-area-inset-top)); right: 10px; left: 10px; }
  .global-notification { padding: 12px 13px 14px; }
  .global-notification__message { font-size: 13px; }
}

@media print { .global-notifications { display: none !important; } }
</style>
