import { computed, ref } from 'vue'

interface InstallPrompt extends Event {
  prompt(): Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

const pending = ref<InstallPrompt | null>(null)
const standalone = ref(false)
export const online = ref(navigator.onLine)
export const installAvailable = computed(() => !standalone.value)
export const secureInstall = window.isSecureContext
export const isWindows = /Windows/i.test(navigator.userAgent)
export const isAppleMobile = /iPad|iPhone|iPod/.test(navigator.userAgent)
  || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)

export function initializePwa() {
  const display = window.matchMedia('(display-mode: standalone)')
  const updateDisplay = () => {
    standalone.value = display.matches || (navigator as Navigator & { standalone?: boolean }).standalone === true
  }
  updateDisplay()
  display.addEventListener('change', updateDisplay)
  window.addEventListener('online', () => { online.value = true })
  window.addEventListener('offline', () => { online.value = false })
  window.addEventListener('beforeinstallprompt', (event) => {
    event.preventDefault()
    pending.value = event as InstallPrompt
  })
  window.addEventListener('appinstalled', () => {
    pending.value = null
    standalone.value = true
  })
  // Development must always fetch current source. No patient records or API responses are cached.
  if (import.meta.env.PROD && secureInstall && 'serviceWorker' in navigator) {
    const register = () => navigator.serviceWorker.register('/sw.js', { updateViaCache: 'none' }).catch(() => {
      // Installation help and normal online use remain available if registration is blocked.
    })
    if (document.readyState === 'complete') void register()
    else window.addEventListener('load', register, { once: true })
  }
}

/** A prompt is single-use, including when dismissed or rejected by the browser. */
export async function requestInstall(): Promise<'accepted' | 'dismissed' | 'manual'> {
  const event = pending.value
  if (!event) return 'manual'
  pending.value = null
  try {
    await event.prompt()
    return (await event.userChoice).outcome
  } catch {
    return 'manual'
  }
}
