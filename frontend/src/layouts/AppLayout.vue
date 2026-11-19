<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api/http'
import { warmLookups } from '@/api/lookups'
import { useAudioStation, useAudioStationHost } from '@/features/calls/audioStation'
import { connectRealtime, disconnectRealtime, liveConnected } from '@/api/realtime'
import { prefetchForRoles } from '@/router/pages'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime } from '@/utils/date'
import OnlineCommandsPanel from '@/features/settings/OnlineCommandsPanel.vue'
import AppFooter from './AppFooter.vue'
import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'

const MINI_KEY = 'alshifa.sidebar.mini'
const BACKUP_CHECK_MS = 10 * 60_000

const auth = useAuthStore()
useAudioStationHost()
const { needsAttention: stationNeedsClick } = useAudioStation()
const sidebarOpen = ref(false)
const mobileMenuQuery = window.matchMedia('(max-width: 991.98px)')
const mobileMenu = ref(mobileMenuQuery.matches)
function updateMobileMenu() {
  mobileMenu.value = mobileMenuQuery.matches
  if (!mobileMenu.value) sidebarOpen.value = false
}
const sidebarMini = ref(readMini())
const sidebarCollapsed = ref(sidebarMini.value)
const mounted = ref(false)
const backupProblem = ref<{ lastSuccessAt: string | null } | null>(null)
let backupTimer: ReturnType<typeof setInterval> | undefined

// The online copy shows how fresh its data is, since it only changes when the hospital server sends.
const onlineCopy = computed(() => auth.user?.online === true)
const mirrorUpdatedAt = ref<string | null>(null)
let mirrorTimer: ReturnType<typeof setInterval> | undefined

async function checkMirror() {
  try {
    mirrorUpdatedAt.value = (await api<{ lastSuccessAt: string | null }>('/sync/status')).lastSuccessAt
  } catch {
    // Shown again on the next check.
  }
}

async function checkBackup() {
  try {
    const status = await api<{ needsAttention: boolean; lastSuccessAt: string | null }>('/settings/backup-status')
    backupProblem.value = status.needsAttention ? status : null
  } catch {
    // A failed check says nothing about the backup; the connection warning already covers an unreachable server.
  }
}

function readMini() {
  try {
    return localStorage.getItem(MINI_KEY) === '1'
  } catch {
    return false
  }
}

watch(sidebarMini, (value) => {
  try {
    localStorage.setItem(MINI_KEY, value ? '1' : '0')
  } catch {
    // The choice simply is not remembered when the browser blocks storage.
  }
})

// Shows a thin line only when a page really is slow to arrive, so normal clicks stay free of flicker.
const router = useRouter()
const navigating = ref(false)
let progressTimer: ReturnType<typeof setTimeout> | undefined
const stopBefore = router.beforeEach(() => {
  clearTimeout(progressTimer)
  progressTimer = setTimeout(() => (navigating.value = true), 120)
})
const stopAfter = router.afterEach(() => {
  clearTimeout(progressTimer)
  navigating.value = false
})

onMounted(() => {
  mobileMenuQuery.addEventListener('change', updateMobileMenu)
  prefetchForRoles(auth.user?.roles ?? [])
  warmLookups(auth.can)
  connectRealtime()
  // Give the first connection a moment before warning about it.
  setTimeout(() => (mounted.value = true), 3000)
  if (onlineCopy.value) {
    checkMirror()
    mirrorTimer = setInterval(checkMirror, 60_000)
  } else if (auth.can('settings.view')) {
    checkBackup()
    backupTimer = setInterval(checkBackup, BACKUP_CHECK_MS)
  }
})

onBeforeUnmount(() => {
  mobileMenuQuery.removeEventListener('change', updateMobileMenu)
  stopBefore()
  stopAfter()
  clearTimeout(progressTimer)
  disconnectRealtime()
  clearInterval(backupTimer)
  clearInterval(mirrorTimer)
})
</script>

<template>
  <div v-if="navigating" class="xr-route-progress" role="progressbar" aria-label="در حال باز کردن صفحه"></div>
  <div class="layout" :class="{ 'layout--mini': sidebarCollapsed }">
    <AppSidebar
      :open="sidebarOpen"
      :mini="sidebarMini"
      @close="sidebarOpen = false"
      @toggle-mini="sidebarMini = !sidebarMini"
      @collapse-change="sidebarCollapsed = $event"
    />
    <div class="layout__main" :inert="mobileMenu && sidebarOpen" :class="{ 'layout__main--menu-open': mobileMenu && sidebarOpen }">
      <AppHeader @toggle-sidebar="sidebarOpen = !sidebarOpen" />
      <main class="layout__content">
        <div v-if="onlineCopy" class="alert alert-info" role="status">
          <i class="ri-eye-line" aria-hidden="true"></i>
          نسخه آنلاین: ثبت عملیات پس از تأیید سرور محلی قطعی می‌شود.
          {{ mirrorUpdatedAt ? `اطلاعات تا ${formatDateTime(mirrorUpdatedAt)} به‌روز است.` : 'هنوز اطلاعاتی از سرور شفاخانه نرسیده است.' }}
        </div>
        <div v-if="mounted && !liveConnected" class="alert alert-warning" role="status">
          اتصال زنده قطع است. اطلاعات این صفحه ممکن است تازه نباشد؛ سیستم خودکار دوباره وصل می‌شود.
        </div>
        <div v-if="backupProblem" class="alert alert-danger" role="alert">
          پشتیبان‌گیری خودکار مشکل دارد. آخرین پشتیبان موفق: {{ formatDateTime(backupProblem.lastSuccessAt) }}. به مسئول سیستم اطلاع دهید.
        </div>
        <div v-if="stationNeedsClick" class="alert alert-warning" role="status">این رایانه مرکز پخش صوت است. برای فعال‌شدن صدا یک بار روی صفحه کلیک کنید.</div>
        <OnlineCommandsPanel v-if="onlineCopy" />
        <RouterView />
      </main>
      <AppFooter />
    </div>
  </div>
</template>

<style scoped>
.layout {
  --xr-sidebar-current: var(--xr-sidebar-width);
  height: 100dvh;
  line-height: var(--xr-lh-app);
  overflow: hidden;
  background: var(--xr-gradient-shell);
}

/* Collapsing needs hover to slide the labels back out, so touch screens keep the full menu. */
@media (hover: hover) {
  .layout--mini {
    --xr-sidebar-current: var(--xr-sidebar-mini);
  }
}

.layout__main {
  position: relative;
  display: flex;
  flex-direction: column;
  height: 100dvh;
  overflow-y: auto;
  scrollbar-gutter: stable;
  direction: rtl;
  margin-inline-start: var(--xr-sidebar-current);
  padding: var(--xr-main-pad-top) var(--xr-page-gutter) 0;
  background: var(--xr-page-bg);
  transition: margin 0.3s ease-in-out;
}

.layout__content {
  flex: 1;
  flex-shrink: 0;
  min-width: 0;
}

.layout__main--menu-open { overflow-y: hidden; }

/* System notices are one slim line each, so even several of them leave the page in view. */
.layout__content > .alert {
  margin-bottom: 10px;
  padding: 6px 14px;
  font-size: 13px;
}

@media print {
  .layout {
    height: auto;
    overflow: visible;
    background: #fff;
  }

  .layout__main {
    height: auto;
    overflow: visible;
    margin: 0;
    padding: 0;
    background: #fff;
  }
}

@media (max-width: 991.98px) {
  .layout__main {
    margin-inline-start: 0;
    padding: max(12px, env(safe-area-inset-top)) max(16px, env(safe-area-inset-right)) env(safe-area-inset-bottom) max(16px, env(safe-area-inset-left));
    border-radius: 0;
  }

  .layout__content {
    margin-top: 16px;
  }
}

@media (max-width: 575.98px) {
  .layout__main {
    padding-inline: max(10px, env(safe-area-inset-right)) max(10px, env(safe-area-inset-left));
  }
}
</style>
