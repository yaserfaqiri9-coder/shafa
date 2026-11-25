<script setup lang="ts">
import { computed } from 'vue'
import type { CallDeskHandle } from '@/features/calls/useCallDesk'
import { formatTime } from '@/utils/date'
import { quantity } from '@/utils/money'
import { callStateOf } from './queueState'

/** Only the buttons that fit the patient's place in the queue; the page adds its own main button beside them. */
const props = defineProps<{ desk: CallDeskHandle; visitId?: number | null; tokenId?: number | null }>()

const entry = computed(() => (props.visitId || props.tokenId ? props.desk.entry(props.visitId, props.tokenId) : undefined))
const state = computed(() => callStateOf(props.desk, props.visitId, props.tokenId))
const someoneCalled = computed(() => props.desk.rows.value.some((r) => r.isServing))
const busy = computed(() => !!entry.value && props.desk.isSending(entry.value))
const playing = computed(() => ['Queued', 'Playing'].includes(entry.value?.lastCall?.status ?? ''))
const number = computed(() => quantity(entry.value?.queueNumber ?? 0))

/** What the speaker did with the last call, so the desk knows it was heard. */
const heard = computed(() => {
  const last = entry.value?.lastCall
  if (!last) return null
  const text: Record<string, string> = { Queued: 'در نوبت پخش', Playing: 'در حال پخش', Played: 'اعلان شد', Failed: 'پخش نشد', Cancelled: 'پخش لغو شد' }
  return { text: text[last.status] ?? '', time: formatTime(last.finishedAt ?? last.requestedAt), failed: last.status === 'Failed', reason: last.failureReason }
})

function call(again: boolean) { if (entry.value) void props.desk.call(entry.value, again) }
function mark(outcome: 'Attended' | 'NoShow') { if (entry.value) void props.desk.mark(entry.value, outcome) }
</script>

<template>
  <span v-if="entry" class="dq-call" role="group" :aria-label="`فراخوان نوبت ${number}`">
    <template v-if="state === 'next' && !someoneCalled">
      <button type="button" class="btn btn-primary" :disabled="busy" :aria-label="`صدا زدن نوبت ${number}`" @click="call(!!entry.lastCall)"><i class="ri-volume-up-line" aria-hidden="true"></i> صدا زدن</button>
      <button type="button" class="btn btn-quiet dq__absent" :disabled="busy" :aria-label="`نوبت ${number} غایب است`" @click="mark('NoShow')">غایب</button>
    </template>
    <template v-else-if="state === 'called'">
      <button type="button" class="btn btn-soft" :disabled="busy || playing" :aria-label="`صدا زدن دوباره نوبت ${number}`" @click="call(true)"><i class="ri-volume-up-line" aria-hidden="true"></i> دوباره</button>
      <button type="button" class="btn btn-quiet dq__absent" :disabled="busy" :aria-label="`نوبت ${number} نیامد`" @click="mark('NoShow')">نیامد</button>
    </template>
    <button v-else-if="state === 'absent'" type="button" class="btn btn-soft" :disabled="busy" :aria-label="`نوبت ${number} آمد`" @click="mark('Attended')">آمد</button>
    <small v-if="heard && state !== 'absent'" class="dq-call__heard" :class="{ 'dq-call__heard--failed': heard.failed }" :title="heard.reason ?? undefined">{{ heard.text }} {{ heard.time }}</small>
  </span>
</template>

<style scoped>
.dq-call { display: inline-flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.dq-call__heard { font-size: 12px; color: var(--xr-text-muted, #6b7280); white-space: nowrap; }
.dq-call__heard--failed { color: #b91c1c; font-weight: 600; }
</style>
