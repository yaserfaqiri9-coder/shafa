<script setup lang="ts">
import { nextTick, ref } from 'vue'
import UiModal from './UiModal.vue'
import { installAvailable, isAppleMobile, isWindows, requestInstall, secureInstall } from '@/pwa'

defineProps<{ compact?: boolean }>()
const help = ref(false)
const busy = ref(false)
async function install(event: MouseEvent) {
  if (busy.value) return
  const trigger = event.currentTarget as HTMLButtonElement
  busy.value = true
  let outcome: 'accepted' | 'dismissed' | 'manual'
  try {
    outcome = await requestInstall()
  } finally {
    busy.value = false
  }
  if (outcome === 'manual') {
    await nextTick()
    trigger.focus()
    help.value = true
  }
}
</script>

<template>
  <button v-if="installAvailable" type="button" :class="compact ? 'header__icon pwa-install-icon' : 'btn btn-soft'"
    aria-label="نصب چشم‌الشفا" title="نصب چشم‌الشفا" :disabled="busy" @click="install">
    <i class="ri-download-cloud-2-line" aria-hidden="true"></i>
    <span v-if="!compact">نصب برنامه</span>
  </button>
  <UiModal v-if="help" title="نصب چشم‌الشفا" @close="help = false">
    <div class="pwa-guide">
      <img src="/icons/apple-touch-icon.png" width="72" height="72" alt="نشان چشم‌الشفا" />
      <p>به وای‌فای شفاخانه وصل شوید، آیکن چشم‌الشفا را نصب کنید و با حساب خود وارد شوید. برای استفاده داخل شفاخانه انترنت خارجی لازم نیست.</p>
      <div v-if="!secureInstall" class="alert alert-warning" role="status">برای نصب کامل برنامه، آدرس امن HTTPS سیستم را از مسئول شفاخانه بگیرید.</div>
      <template v-if="isAppleMobile">
        <h3>آیفون و آیپد</h3>
        <ol><li>همین آدرس را در Safari باز کنید.</li><li>گزینه اشتراک‌گذاری را بزنید.</li><li>«Add to Home Screen» و سپس «Add» را انتخاب کنید.</li></ol>
      </template>
      <template v-else-if="isWindows">
        <h3>Windows</h3>
        <ol><li>آدرس محلی شفاخانه را در Edge یا Chrome باز کنید.</li><li>گزینه نصب برنامه در نوار آدرس یا منوی مرورگر را بزنید.</li><li>نصب را تأیید کنید؛ آیکن مستقیماً سیستم محلی را باز می‌کند.</li></ol>
      </template>
      <template v-else>
        <h3>اندروید</h3>
        <ol><li>همین آدرس را در Chrome باز کنید.</li><li>از منوی سه‌نقطه، «نصب برنامه» یا «افزودن به صفحه اصلی» را انتخاب کنید.</li><li>نصب یا افزودن را تأیید کنید.</li></ol>
        <p class="text-muted">در رایانه نیز می‌توانید از گزینه نصب برنامه در منوی مرورگر استفاده کنید.</p>
      </template>
      <p class="mb-0">استفاده از سیستم به اتصال به سرور نیاز دارد. نصب برنامه صلاحیت حساب را تغییر نمی‌دهد. درخواست آنلاین پس از تأیید سرور محلی قطعی می‌شود.</p>
    </div>
  </UiModal>
</template>

<style scoped>
.pwa-guide { display: grid; gap: 12px; overflow-wrap: anywhere; }
.pwa-guide p, .pwa-guide ol { margin: 0; }
.pwa-guide h3 { font-size: 16px; font-weight: 600; }
.pwa-guide li + li { margin-top: 8px; }
.pwa-install-icon { display: inline-flex; flex: none; align-items: center; justify-content: center; width: 44px; min-height: 44px; border: 0; background: transparent; color: var(--xr-primary); font-size: 20px; }
</style>
