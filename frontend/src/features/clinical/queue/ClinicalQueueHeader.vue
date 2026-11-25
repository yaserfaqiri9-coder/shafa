<script setup lang="ts">
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { todayIso } from '@/utils/date'
import { quantity } from '@/utils/money'

/** Title, the two-list switch and one filter row: the same on every clinical queue page. */
withDefaults(defineProps<{
  title: string
  queueLabel?: string
  doneLabel?: string
  /** Shown on the queue switch, for example "5 منتظر". */
  count?: number | null
  countLabel?: string
  /** Shown on the seen switch, for example "2 نتیجه آماده"; hidden when null. */
  doneCount?: number | null
  doneCountLabel?: string
  /** The seen list can be read by day, and by shift where rows carry one. */
  dated?: boolean
  shiftFilter?: boolean
}>(), { queueLabel: 'صف امروز', doneLabel: 'مریضان دیده‌شده', count: null, countLabel: 'منتظر', doneCount: null, doneCountLabel: '', dated: false, shiftFilter: true })

const tab = defineModel<'queue' | 'done'>('tab', { required: true })
const term = defineModel<string>('term', { required: true })
const date = defineModel<string | null>('date', { default: null })
const shift = defineModel<'all' | 'Morning' | 'Afternoon'>('shift', { default: 'all' })
const shifts = [['all', 'همه'], ['Morning', 'صبح'], ['Afternoon', 'چاشت']] as const
</script>

<template>
  <div class="dq-head">
    <h1 class="dq-title">{{ title }}</h1>
    <div v-if="$slots.actions" class="dq-head__actions"><slot name="actions" /></div>
  </div>

  <div class="xr-tabs dq-tabs" role="tablist" :aria-label="`فهرست‌های ${title}`">
    <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'queue' }" :aria-selected="tab === 'queue'" @click="tab = 'queue'">
      {{ queueLabel }} <span v-if="count !== null" class="xr-tab__count">{{ quantity(count) }} {{ countLabel }}</span>
    </button>
    <button type="button" role="tab" class="xr-tab" :class="{ 'xr-tab--active': tab === 'done' }" :aria-selected="tab === 'done'" @click="tab = 'done'">
      {{ doneLabel }} <span v-if="doneCount" class="xr-tab__count xr-tab__count--ready">{{ quantity(doneCount) }} {{ doneCountLabel }}</span>
    </button>
  </div>

  <div class="dq-filters">
    <label class="dq-search">
      <i class="ri-search-line" aria-hidden="true"></i>
      <input v-model="term" type="search" placeholder="جستجو: نام مریض، شماره پرونده یا نوبت" aria-label="جستجوی مریض" />
      <button v-if="term" type="button" class="dq-search__clear" aria-label="پاک کردن جستجو" @click="term = ''"><i class="ri-close-line" aria-hidden="true"></i></button>
    </label>
    <template v-if="dated && tab === 'done'">
      <div class="dq-date"><ShamsiDateInput v-model="date" label="تاریخ" :max="todayIso()" /></div>
      <div v-if="shiftFilter" class="xr-tabs" role="group" aria-label="نوبت صبح یا چاشت">
        <button v-for="option in shifts" :key="option[0]" type="button" class="xr-tab" :class="{ 'xr-tab--active': shift === option[0] }"
          :aria-pressed="shift === option[0]" @click="shift = option[0]">{{ option[1] }}</button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.xr-tab .xr-tab__count--ready { background: #16a34a; color: #fff; }
</style>
