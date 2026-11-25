import { shallowRef } from 'vue'

/** One question at a time, asked in the system's own dialog instead of the browser's plain box. */
export interface ConfirmRequest {
  title: string
  message: string
  confirmLabel: string
  /** Red confirm button: the action deletes, cancels, voids or takes stock. */
  danger: boolean
  resolve: (ok: boolean) => void
}

export const pendingConfirm = shallowRef<ConfirmRequest | null>(null)

/** Resolves true only when the person presses the confirm button; closing, Esc or a newer question answer false. */
export function askConfirm(message: string, options: { title?: string; confirmLabel?: string; danger?: boolean } = {}): Promise<boolean> {
  pendingConfirm.value?.resolve(false)
  return new Promise((resolve) => {
    pendingConfirm.value = {
      title: options.title ?? 'تأیید',
      message,
      confirmLabel: options.confirmLabel ?? 'بله',
      danger: options.danger ?? false,
      resolve: (ok) => {
        pendingConfirm.value = null
        resolve(ok)
      },
    }
  })
}
