<script setup lang="ts">
import { ref, useId, watch } from 'vue'

const model = defineModel<number | null>({ required: true })
const id = useId()
const draft = ref(model.value?.toString() ?? '')
watch(model, value => {
  if (value !== null) draft.value = String(value)
  else if (/^\d+$/.test(draft.value) && Number(draft.value) >= 1 && Number(draft.value) <= 100) draft.value = ''
})
function update(event: Event) {
  const input = event.target as HTMLInputElement
  draft.value = input.value
  const normalized = input.value.replace(/[۰-۹]/g, d => String(d.charCodeAt(0) - 1776)).replace(/[٠-٩]/g, d => String(d.charCodeAt(0) - 1632)).trim()
  const number = /^\d+$/.test(normalized) ? Number(normalized) : NaN
  const valid = Number.isInteger(number) && number >= 1 && number <= 100
  model.value = valid ? number : null
  input.setCustomValidity(valid || !normalized ? '' : 'شماره کارت باید عدد صحیح از ۱ تا ۱۰۰ باشد.')
}
</script>

<template>
  <div>
    <label :for="id" class="form-label">شماره نوبت <span aria-hidden="true">*</span></label>
    <input :id="id" :value="draft" name="queueNumber" class="form-control" type="text" inputmode="numeric" maxlength="3" required autocomplete="off" :aria-describedby="`${id}-hint`" @input="update" />
    <p :id="`${id}-hint`" class="form-hint">شماره روی کارت فیزیکی مریض (۱ تا ۱۰۰). صبح و چاشت (از ساعت ۳) هر کدام از ۱ شروع می‌شود. این شماره در تمام بخش‌ها ثابت می‌ماند.</p>
  </div>
</template>
