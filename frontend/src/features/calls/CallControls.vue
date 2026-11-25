<script setup lang="ts">
import { computed } from 'vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import { formatTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import type { CallDeskHandle } from './useCallDesk'

const props = defineProps<{ desk: CallDeskHandle; visitId?: number | null; tokenId?: number | null; unavailableReason?: string | null }>()

const statusLabels: Record<string, { label: string; tone: string }> = {
  Queued: { label: 'در نوبت پخش', tone: 'orange' },
  Playing: { label: 'در حال پخش', tone: 'cyan' },
  Played: { label: 'اعلان شد', tone: 'primary' },
  Failed: { label: 'پخش نشد', tone: 'red' },
  Cancelled: { label: 'پخش لغو شد', tone: 'orange' },
}

const row = computed(() => props.desk.entry(props.visitId, props.tokenId))
const last = computed(() => row.value?.lastCall ?? null)
const active = computed(() => last.value?.status === 'Queued' || last.value?.status === 'Playing')
const recall = computed(() => !!row.value && (!row.value.isCurrentQueue || !!last.value))
const number = computed(() => quantity(row.value?.queueNumber ?? 0))
const sending = computed(() => (row.value ? props.desk.isSending(row.value) : false))
const attended = computed(() => last.value?.outcome === 'Attended')

const state = computed(() => {
  if (row.value?.present === false) return { label: 'غایب', tone: 'red' }
  if (row.value?.isServing) return { label: 'مریض فعلی', tone: 'cyan' }
  if (attended.value) return { label: 'حاضر شد', tone: 'green' }
  if (last.value?.outcome === 'NoShow') return { label: 'عدم حضور', tone: 'red' }
  return last.value ? statusLabels[last.value.status] : null
})

// Only the next likely step stays on the row; the rest waits in the menu until a patient has been called.
const menu = computed(() => {
  if (!row.value) return []
  const items = []
  if (row.value.present === false || !attended.value) items.push({ label: 'مریض حاضر شد', ariaLabel: `نوبت ${number.value} حاضر شد`, icon: 'ri-check-line', disabled: sending.value, onSelect: () => void props.desk.mark(row.value!, 'Attended') })
  if (row.value.present !== false) {
    items.push({ label: 'عدم حضور', ariaLabel: `عدم حضور نوبت ${number.value}`, icon: 'ri-close-line', danger: true, disabled: sending.value, onSelect: () => void props.desk.mark(row.value!, 'NoShow') })
  }
  return items
})
</script>

<template>
  <div v-if="row || props.unavailableReason" class="call-controls" role="group" :aria-label="row ? `فراخوان نوبت ${number}` : (props.unavailableReason ?? 'فراخوان صوتی در دسترس نیست')">
    <div class="call-controls__actions">
      <button
        type="button"
        class="btn btn-quiet btn-sm text-nowrap"
        :disabled="!row || active || sending || row.present === false || row.canCall === false"
        :title="!row ? props.unavailableReason ?? undefined : undefined"
        :aria-label="row ? `${recall ? 'فراخوان مجدد' : 'فراخوان'} نوبت ${number}` : (props.unavailableReason ?? 'فراخوان صوتی در دسترس نیست')"
        @click="row && desk.call(row, recall)"
      >
        <i class="ri-volume-up-line" aria-hidden="true"></i>
        {{ row ? (recall ? 'فراخوان مجدد' : 'فراخوان') + ' ' + number : 'فراخوان صوتی' }}
      </button>
      <UiRowMenu v-if="menu.length" :label="`گزینه‌های بیشتر نوبت ${number}`" :items="menu" />
    </div>
    <div v-if="state" class="xr-note text-nowrap" :class="`xr-note--${state.tone}`" :title="last?.failureReason ?? last?.destination">
      <span>{{ state.label }}</span>
      <span v-if="last" class="ltr-num" title="زمان آخرین اعلان">{{ formatTime(last!.finishedAt ?? last!.requestedAt) }}</span>
    </div>
  </div>
</template>

<style scoped>
.call-controls {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
}

.call-controls__actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
</style>
