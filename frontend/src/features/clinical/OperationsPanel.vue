<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiInactiveToggle, { useInactiveFilter } from '@/components/UiInactiveToggle.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { money, quantity } from '@/utils/money'
import { removeRecord } from '@/utils/removeRecord'
import type { SurgeryPackage } from './types'

interface OperationRow {
  id: number; name: string; price: number | null; isActive: boolean; version: number
  packages: { packageId: number; name: string; quantity: number }[]
}
const auth = useAuthStore()
const rows = ref<OperationRow[]>([])
const packages = ref<SurgeryPackage[]>([])
const error = ref('')
const notice = ref('')
const saving = ref(false)
const { showInactive, shown, inactiveCount } = useInactiveFilter(rows)
const form = reactive({ open: false, id: null as number | null, name: '', price: 0, isActive: true, version: null as number | null, packages: [] as { packageId: number | null; quantity: number }[] })
async function load() {
  try { [rows.value, packages.value] = await Promise.all([api<OperationRow[]>('/operations?all=true'), api<SurgeryPackage[]>('/surgery-packages')]) }
  catch (cause) { error.value = errorMessage(cause) }
}
function open(row?: OperationRow) {
  error.value = ''
  Object.assign(form, { open: true, id: row?.id ?? null, name: row?.name ?? '', price: row?.price ?? 0, isActive: row?.isActive ?? true, version: row?.version ?? null,
    packages: row?.packages.map((p) => ({ packageId: p.packageId, quantity: p.quantity })) ?? [] })
}
async function save() {
  saving.value = true
  error.value = ''
  try {
    await api(form.id ? `/operations/${form.id}` : '/operations', { method: form.id ? 'PUT' : 'POST', body: { name: form.name, price: form.price, isActive: form.isActive, version: form.version, packages: form.packages } })
    form.open = false
    await load()
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
async function remove(row: OperationRow) {
  error.value = ''
  try {
    notice.value = await removeRecord(`/operations/${row.id}?version=${row.version}`, row.name)
    await load()
  } catch (cause) { error.value = errorMessage(cause) }
}
onMounted(load)
</script>

<template>
  <div class="mb-3 d-flex align-items-center justify-content-between gap-3">
    <p class="mb-0 text-muted">نام و فیس هر عملیات و تعداد پکیج‌های مصرفی آن را تعیین کنید. پکیج‌ها پس از تسویهٔ کامل فیس، یک بار از موجودی کسر می‌شوند.</p>
    <div class="d-flex align-items-center gap-3">
      <UiInactiveToggle v-model="showInactive" :count="inactiveCount" />
      <button v-if="auth.can('surgery.approve')" type="button" class="btn btn-primary text-nowrap" @click="open()">عملیات جدید</button>
    </div>
  </div>
  <div v-if="error && !form.open" class="alert alert-danger" role="alert">{{ error }}</div>
  <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
  <div class="xr-table-wrap"><table class="xr-table">
    <thead><tr><th>نام عملیات</th><th>قیمت</th><th>پکیج مصرفی برای یک عملیات</th><th>وضعیت</th><th></th></tr></thead>
    <tbody><tr v-for="row in shown" :key="row.id">
      <td>{{ row.name }}</td><td>{{ money(row.price) }}</td>
      <td><div v-for="p in row.packages" :key="p.packageId">{{ p.name }} × {{ quantity(p.quantity) }}</div><span v-if="!row.packages.length">بدون پکیج</span></td>
      <td>{{ row.isActive ? 'فعال' : 'غیرفعال' }}</td>
      <td class="text-nowrap"><template v-if="auth.can('surgery.approve')"><button type="button" class="btn btn-soft btn-sm" @click="open(row)">ویرایش</button><button type="button" class="btn btn-soft-danger btn-sm ms-2" @click="remove(row)">حذف</button></template></td>
    </tr><tr v-if="!shown.length"><td colspan="5">عملیاتی تعریف نشده است.</td></tr></tbody>
  </table></div>
  <UiModal v-if="form.open" :title="form.id ? 'ویرایش عملیات' : 'تعریف عملیات'" size="lg" @close="form.open = false">
    <form @submit.prevent="save">
      <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
      <div class="row g-3">
        <div class="col-md-8"><label for="op-name" class="form-label">نام عملیات</label><input id="op-name" v-model="form.name" class="form-control" required maxlength="150" /></div>
        <div class="col-md-4"><label for="op-price" class="form-label">قیمت عملیات (افغانی)</label><input id="op-price" v-model.number="form.price" class="form-control" type="number" min="0.01" step="0.01" required /></div>
      </div>
      <h3 class="h6 mt-4">پکیج‌های مصرفی</h3>
      <div v-for="(p, index) in form.packages" :key="index" class="row g-2 mb-2">
        <div class="col-7"><select v-model="p.packageId" class="form-select" required :aria-label="`پکیج ${index + 1}`"><option :value="null" disabled>انتخاب پکیج</option><option v-for="pkg in packages" :key="pkg.id" :value="pkg.id">{{ pkg.name }} — موجودی {{ quantity(pkg.onHand) }}</option></select></div>
        <div class="col-3"><input v-model.number="p.quantity" class="form-control" type="number" min="0.001" max="100000" step="0.001" required :aria-label="`تعداد پکیج ${index + 1}`" /></div>
        <div class="col-2"><button type="button" class="btn btn-soft-danger" @click="form.packages.splice(index, 1)">حذف</button></div>
      </div>
      <button type="button" class="btn btn-soft btn-sm mt-2" @click="form.packages.push({ packageId: null, quantity: 1 })">افزودن پکیج</button>
      <div class="form-check mt-3"><input id="op-active" v-model="form.isActive" type="checkbox" class="form-check-input" /><label for="op-active" class="form-check-label">فعال</label></div>
      <div class="xr-form-actions"><button type="submit" class="btn btn-primary" :disabled="saving">ذخیره</button><button type="button" class="btn btn-soft" @click="form.open = false">انصراف</button></div>
    </form>
  </UiModal>
</template>
