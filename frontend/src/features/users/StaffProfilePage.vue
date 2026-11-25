<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage, newIdempotencyKey } from '@/api/http'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import { useAuthStore } from '@/stores/auth'
import { money, quantity } from '@/utils/money'
import { formatDate, todayIso } from '@/utils/date'

const props = defineProps<{ id: string }>()
const auth = useAuthStore()
interface Payroll {
  id: number; fullName: string; phone: string | null; employmentStartDate: string | null; employmentEndDate: string | null
  monthlySalary: number; earned: number; paid: number; owed: number; advance: number
  terms: { effectiveFrom: string; monthlySalary: number }[]
  months: { year: number; month: number; from: string; through: string; workingDays: number; monthDays: number; amount: number }[]
  payments: { id: number; expenseNumber: string; amount: number; date: string; kind: string; note: string | null; status: string; version: number }[]
}
const data = ref<Payroll | null>(null)
const error = ref('')
const loading = ref(true)
const saving = ref(false)
const payout = reactive({ open: false, amount: null as number | null, date: todayIso() as string | null, kind: 'Salary', note: '', key: '' })
const cancellation = reactive({ id: null as number | null, version: 0, reason: '' })
const tab = ref<'months' | 'payments' | 'withdrawals'>('months')
async function load() {
  loading.value = true
  error.value = ''
  try { data.value = await api<Payroll>(`/staff/${props.id}/payroll`) }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
}
function open(kind: string) {
  error.value = ''
  Object.assign(payout, { open: true, kind, amount: kind === 'Salary' ? data.value?.owed || null : null, date: todayIso(), note: '', key: newIdempotencyKey() })
}
async function pay() {
  saving.value = true
  error.value = ''
  try {
    await api(`/staff/${props.id}/payouts`, { method: 'POST', body: { amount: payout.amount, date: payout.date, kind: payout.kind, note: payout.note }, idempotencyKey: payout.key })
    payout.open = false
    await load()
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
async function cancel() {
  saving.value = true
  error.value = ''
  try {
    await api(`/expenses/${cancellation.id}/cancel`, { method: 'POST', body: { version: cancellation.version, reason: cancellation.reason } })
    cancellation.id = null
    await load()
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
onMounted(load)
watch(() => props.id, load)
</script>

<template>
  <UiCard :title="data ? `پروفایل ${data.fullName}` : 'پروفایل کارمند'">
    <template #actions><RouterLink to="/staff" class="btn btn-soft">کارمندان</RouterLink>
      <button v-if="data && auth.can('expenses.create')" type="button" class="btn btn-primary" @click="open('Salary')">پرداخت معاش</button>
      <button v-if="data && auth.can('expenses.create')" type="button" class="btn btn-soft" @click="open('Withdrawal')">ثبت برداشت</button>
    </template>
    <div v-if="error && !payout.open && !cancellation.id" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="xr-state">در حال بارگذاری…</div>
    <template v-else-if="data">
      <p>شروع کار: {{ formatDate(data.employmentStartDate) }} <span v-if="data.employmentEndDate"> | ختم کار: {{ formatDate(data.employmentEndDate) }}</span></p>
      <div class="row g-3 mb-4">
        <div class="col-sm-6 col-xl-3"><div class="p-3 border rounded"><small>معاش ماهوار فعلی</small><div class="fs-4">{{ money(data.monthlySalary) }}</div></div></div>
        <div class="col-sm-6 col-xl-3"><div class="p-3 border rounded"><small>معاش کسب‌شده تا پایان ماه گذشته</small><div class="fs-4">{{ money(data.earned) }}</div></div></div>
        <div class="col-sm-6 col-xl-3"><div class="p-3 border rounded"><small>قرض شفاخانه به کارمند</small><div class="fs-4 text-danger">{{ money(data.owed) }}</div></div></div>
        <div class="col-sm-6 col-xl-3"><div class="p-3 border rounded"><small>پیش‌برداشت کارمند</small><div class="fs-4">{{ money(data.advance) }}</div></div></div>
      </div>
      <p class="text-muted">ماه شمسی پس از پایان آن محاسبه می‌شود. معاش ماه شروع و ختم، به نسبت روزهای کار است. برداشت‌ها از معاش قابل پرداخت کم می‌شوند.</p>
      <nav class="xr-tabs mb-3" aria-label="سوابق کارمند">
        <button type="button" class="xr-tab" :class="{ 'xr-tab--active': tab === 'months' }" @click="tab = 'months'">معاش ماهوار</button>
        <button type="button" class="xr-tab" :class="{ 'xr-tab--active': tab === 'payments' }" @click="tab = 'payments'">تمام پرداخت‌ها</button>
        <button type="button" class="xr-tab" :class="{ 'xr-tab--active': tab === 'withdrawals' }" @click="tab = 'withdrawals'">برداشت‌ها</button>
      </nav>
      <div v-if="tab === 'months'" class="xr-table-wrap"><table class="xr-table">
        <thead><tr><th>ماه شمسی</th><th>مدت</th><th>روزهای کار</th><th>معاش کسب‌شده</th></tr></thead>
        <tbody><tr v-for="m in data.months.slice().reverse()" :key="m.from"><td>{{ quantity(m.year) }}/{{ quantity(m.month) }}</td><td>{{ formatDate(m.from) }} — {{ formatDate(m.through) }}</td><td>{{ quantity(m.workingDays) }} از {{ quantity(m.monthDays) }}</td><td>{{ money(m.amount) }}</td></tr><tr v-if="!data.months.length"><td colspan="4">هنوز ماه معاش تکمیل نشده است.</td></tr></tbody>
      </table></div>
      <div v-else class="xr-table-wrap"><table class="xr-table">
        <thead><tr><th>سند</th><th>تاریخ</th><th>نوع</th><th>مبلغ</th><th>توضیح</th><th>وضعیت</th><th></th></tr></thead>
        <tbody><tr v-for="p in data.payments.filter((p) => tab !== 'withdrawals' || p.kind === 'Withdrawal')" :key="p.id">
          <td>{{ p.expenseNumber }}</td><td>{{ formatDate(p.date) }}</td><td>{{ p.kind === 'Salary' ? 'معاش' : 'برداشت' }}</td><td>{{ money(p.amount) }}</td><td>{{ p.note ?? '—' }}</td><td>{{ p.status === 'Recorded' ? 'ثبت‌شده' : 'لغوشده' }}</td>
          <td><button v-if="p.status === 'Recorded' && auth.can('expenses.cancel')" type="button" class="btn btn-soft-danger btn-sm" @click="Object.assign(cancellation, { id: p.id, version: p.version, reason: '' })">لغو</button></td>
        </tr></tbody>
      </table></div>
      <details class="mt-4"><summary>تاریخچهٔ نرخ معاش</summary><ul class="mt-2"><li v-for="term in data.terms.slice().reverse()" :key="term.effectiveFrom">از {{ formatDate(term.effectiveFrom) }}: {{ money(term.monthlySalary) }} در ماه</li></ul></details>
    </template>
  </UiCard>
  <UiModal v-if="payout.open" :title="payout.kind === 'Salary' ? 'پرداخت معاش' : 'ثبت برداشت کارمند'" @close="payout.open = false">
    <form @submit.prevent="pay">
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <label for="pay-amount" class="form-label">مبلغ (افغانی)</label><input id="pay-amount" v-model.number="payout.amount" type="number" min="0.01" step="0.01" max="100000000" class="form-control mb-3" required />
      <ShamsiDateInput v-model="payout.date" label="تاریخ پرداخت" :max="todayIso()" required />
      <label for="pay-note" class="form-label mt-3">توضیح</label><input id="pay-note" v-model="payout.note" class="form-control" maxlength="300" />
      <div class="xr-form-actions"><button type="submit" class="btn btn-primary" :disabled="saving">ثبت پرداخت</button><button type="button" class="btn btn-soft" @click="payout.open = false">انصراف</button></div>
    </form>
  </UiModal>
  <UiModal v-if="cancellation.id" title="لغو پرداخت کارمند" @close="cancellation.id = null">
    <form @submit.prevent="cancel"><div v-if="error" class="alert alert-danger">{{ error }}</div><label for="cancel-pay" class="form-label">دلیل لغو</label><input id="cancel-pay" v-model="cancellation.reason" class="form-control" minlength="3" maxlength="300" required /><div class="xr-form-actions"><button class="btn btn-soft-danger" type="submit" :disabled="saving">تأیید لغو</button></div></form>
  </UiModal>
</template>
