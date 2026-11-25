<script setup lang="ts">
import { computed } from 'vue'
import ReportAvatar from './ReportAvatar.vue'
import type { ReportTile } from './types'

/** unknown: the system cannot give a true figure, so the value is a word and the card is marked. */
const props = defineProps<{ label: string; value: string; kind: ReportTile['kind']; note?: string; negative?: boolean; unknown?: boolean }>()

/** The gallery picture that fits the figure best, read from its label; the kind decides when no word matches. */
const pictures: [RegExp, string][] = [
  [/مصرف|مصارف/, 'expenses'],
  [/طلب|فاکتورهای باز|کهنه/, 'outstanding'],
  [/تخفیف|برگشت|باطل/, 'adjustments'],
  [/دریافت|صندوق|کسری/, 'cash'],
  [/درآمد|نتیجه|عواید|سود|زیان/, 'income'],
  [/فروش/, 'sales'],
  [/موجودی|جنس|اجناس|بسته|تاریخ/, 'stock'],
  [/تشخیص|بیماری/, 'diseases'],
  [/عملیات/, 'surgeries'],
  [/درخواست|انجام|باز|لغو/, 'diagnostics'],
  [/داکتر|معاین|نسخه/, 'doctors'],
  [/مریض|مراجع/, 'patients'],
]
const picture = computed(
  () => pictures.find(([pattern]) => pattern.test(props.label))?.[1] ?? (props.kind === 'money' ? 'cash' : props.kind === 'quantity' ? 'stock' : 'overview'),
)
/** The figure stands alone in large type; its unit moves to the small line under it. */
const figure = computed(() => props.value.replace(/\s*افغانی$/, ''))
const small = computed(() => [props.kind === 'money' && !props.unknown ? 'افغانی' : '', props.note ?? ''].filter(Boolean).join(' · '))
</script>

<template>
  <div class="rs" :class="{ 'rs--unknown': unknown }" :title="note ? `${label}: ${value} — ${note}` : `${label}: ${value}`">
    <div class="rs__text">
      <div class="rs__label">{{ label }}</div>
      <div class="rs__value" :class="{ 'rs__value--negative': negative }">{{ figure }}</div>
      <div v-if="small" class="rs__note">{{ small }}</div>
    </div>
    <span class="rs__picture" aria-hidden="true"><ReportAvatar :name="picture" /></span>
  </div>
</template>

<style scoped>
/* A quiet white card: the label, the figure, one small line; the picture sits at the far end. Always the same height. */
.rs {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 84px;
  height: 100%;
  padding: 12px 18px;
  overflow: hidden;
  border-radius: 10px;
  background: var(--xr-surface);
  box-shadow: var(--xr-shadow-card);
}

.rs__text {
  flex: 1;
  min-width: 0;
}

.rs__value {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Labels and notes explain the figure: they wrap to a second line rather than lose their end to an ellipsis. */
.rs__label,
.rs__note {
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  overflow-wrap: anywhere;
}

.rs__label {
  color: var(--xr-switch-text);
  font-size: 12.5px;
  font-weight: 500;
  line-height: 1.6;
}

.rs__value {
  color: var(--xr-text);
  font-size: 20px;
  font-weight: 700;
  line-height: 1.4;
  font-variant-numeric: tabular-nums;
}

.rs--unknown {
  box-shadow: inset 0 0 0 1px #f0d48a, var(--xr-shadow-card);
}

.rs--unknown .rs__value {
  color: #9a6b00;
}

.rs__value--negative {
  color: var(--xr-tone-red-text);
}

.rs__note {
  color: var(--xr-text-soft);
  font-size: 11px;
  line-height: 1.5;
}

.rs__picture {
  flex: none;
  width: 60px;
  height: 60px;
}

@media (max-width: 575.98px) {
  /* Two cards share a phone's width: labels wrap instead of losing their meaning to an ellipsis. */
  .rs {
    padding: 10px 12px;
  }

  .rs__label,
  .rs__note {
    -webkit-line-clamp: 3;
  }

  .rs__value {
    font-size: 17px;
  }

  .rs__picture {
    width: 44px;
    height: 44px;
  }
}
</style>
