<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import { money } from '@/utils/money'
import { formatDate } from '@/utils/date'
const rows = ref<{ id: number; staffId: number; staffName: string; expenseNumber: string; amount: number; date: string; kind: string; status: string; note: string | null }[]>([])
const error = ref('')
onMounted(async () => { try { rows.value = await api<typeof rows.value>('/staff/payouts') } catch (cause) { error.value = errorMessage(cause) } })
</script>
<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
  <p class="text-muted">آخرین ۵۰۰ برداشت کارمندان. برداشت جدید را از پروفایل کارمند ثبت کنید.</p>
  <div class="xr-table-wrap"><table class="xr-table"><thead><tr><th>کارمند</th><th>سند</th><th>تاریخ</th><th>مبلغ</th><th>توضیح</th><th>وضعیت</th></tr></thead>
    <tbody><tr v-for="p in rows.filter((r) => r.kind === 'Withdrawal')" :key="p.id"><td><RouterLink :to="`/staff/${p.staffId}`">{{ p.staffName }}</RouterLink></td><td>{{ p.expenseNumber }}</td><td>{{ formatDate(p.date) }}</td><td>{{ money(p.amount) }}</td><td>{{ p.note ?? '—' }}</td><td>{{ p.status === 'Recorded' ? 'ثبت‌شده' : 'لغوشده' }}</td></tr></tbody>
  </table></div>
</template>
