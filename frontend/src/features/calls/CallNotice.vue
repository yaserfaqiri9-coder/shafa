<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import type { CallDeskHandle } from './useCallDesk'
import { quantity } from '@/utils/money'

const auth = useAuthStore()
const props = withDefaults(defineProps<{ desks: CallDeskHandle[]; numbers?: boolean }>(), { numbers: true })

const offline = computed(() => props.desks.some((desk) => desk.enabled && !desk.stationOnline.value))
const errors = computed(() => [...new Set(props.desks.map((desk) => desk.error.value).filter(Boolean))])
const labels: Record<string, string> = { Reception: 'پذیرش', Cashier: 'صندوق', Vision: 'معاینه ابتدایی', Doctor: 'داکتر', Refraction: 'رفرکشن', Imaging: 'عکس‌برداری', Lab: 'لابراتوار', Pharmacy: 'دواخانه', Optical: 'عینک‌سازی', Surgery: 'عملیات' }
</script>

<template>
  <p v-for="desk in (numbers ? desks.filter(d => d.enabled) : [])" :key="desk.desk" class="form-hint" role="status">
    {{ labels[desk.desk] }} — نوبت فعلی: <strong>{{ desk.currentNumber.value === null ? '—' : quantity(desk.currentNumber.value) }}</strong>
    · نوبت بعدی حاضر و آماده: <strong>{{ desk.nextNumber.value === null ? '—' : quantity(desk.nextNumber.value) }}</strong>
  </p>
  <div v-if="offline" class="alert alert-info" role="status">
    مرکز پخش صوت فعال نیست؛ فراخوان از بلندگوی همین رایانه پخش می‌شود.
    <RouterLink v-if="auth.can('calls.manage')" :to="{ name: 'audio-station' }" class="alert-link">فعال‌سازی پخش مرکزی</RouterLink>
  </div>
  <div v-for="message in errors" :key="message" class="alert alert-danger" role="alert">{{ message }}</div>
</template>
