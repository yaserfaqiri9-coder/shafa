<script setup lang="ts">
import { prescriptionRows, type PrescriptionForm } from './refractionPrescription'
import { lensOptions } from './examRecord'

const props = defineProps<{ modelValue: PrescriptionForm; patientName: string; date: string; readonly?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: PrescriptionForm] }>()
const columns = [
  { key: 'sphere', label: 'SPHERE' }, { key: 'cylinder', label: 'CYLENDAR' },
  { key: 'axis', label: 'AXIS' }, { key: 'va', label: 'VISION' },
] as const
function field(key: 'visionRe' | 'visionLe' | 'pd' | 'add', event: Event) {
  emit('update:modelValue', { ...props.modelValue, [key]: (event.target as HTMLInputElement).value })
}
function cell(index: number, key: typeof columns[number]['key'], event: Event) {
  const rows = props.modelValue.rows.map((row, i) => i === index ? { ...row, [key]: (event.target as HTMLInputElement).value } : row)
  emit('update:modelValue', { ...props.modelValue, rows })
}
function toggle(option: string, event: Event) {
  const lens = props.modelValue.lens.filter((value) => value !== option)
  if ((event.target as HTMLInputElement).checked) lens.push(option)
  emit('update:modelValue', { ...props.modelValue, lens })
}
const positions = [
  [37.48, 74.70], [55.04, 74.70], [69.29, 74.70],
  [37.48, 80.10], [55.04, 80.10], [69.29, 80.10],
  [37.48, 85.50], [55.04, 85.50], [69.29, 85.50],
]
</script>

<template>
  <article class="refraction-prescription" dir="ltr" aria-label="نسخه عینک الشفا">
    <!-- Original stationery supplies only fixed artwork and labels. All patient
         values and selections below are real, accessible controls/text. -->
    <img class="prescription-stationery" src="/print/alshafa-refraction-reference.png" alt="" aria-hidden="true" />
    <span class="paper-value patient-name" dir="auto" :title="patientName" :style="{ fontSize: `${Math.min(1.5, 33 / Math.max(patientName.length, 1))}cqw` }">{{ patientName }}</span>
    <span class="paper-value visit-date">{{ date }}</span>
    <template v-for="(key, i) in (['visionRe', 'visionLe'] as const)" :key="key">
      <span v-if="readonly" class="paper-value acuity-value" :class="{ 'filled-acuity': modelValue[key] }" :style="{ top: `${19.2 + i * 3.85}%` }">{{ modelValue[key] }}</span>
      <input v-else class="paper-value acuity-value" :class="{ 'filled-acuity': modelValue[key] }" :style="{ top: `${19.2 + i * 3.85}%` }" :value="modelValue[key]" :aria-label="`Vision ${i ? 'LE' : 'RE'}`" maxlength="30" autocomplete="off" @input="field(key, $event)" />
    </template>
    <table class="prescription-grid" aria-label="EYE SPHERE CYLENDAR AXIS VISION">
      <colgroup><col style="width: 13%" /><col style="width: 21.8%" /><col style="width: 30.1%" /><col style="width: 15.4%" /><col style="width: 19.7%" /></colgroup>
      <thead><tr><th><span class="visually-hidden">EYE</span></th><th v-for="column in columns" :key="column.key"><span class="visually-hidden">{{ column.label }}</span></th></tr></thead>
      <tbody>
        <tr v-for="(row, index) in modelValue.rows" :key="`${row.stage}-${row.eye}`">
          <th scope="row"><span class="visually-hidden">{{ prescriptionRows[index].label }}</span></th>
          <td v-for="column in columns" :key="column.key">
            <span v-if="readonly" class="cell-value">{{ row[column.key] }}</span>
            <input v-else :value="row[column.key]" :aria-label="`${column.label} ${prescriptionRows[index].label} ${index + 1}`" :maxlength="column.key === 'va' ? 30 : 10" autocomplete="off" @input="cell(index, column.key, $event)" />
          </td>
        </tr>
      </tbody>
    </table>
    <template v-for="key in (['add', 'pd'] as const)" :key="key">
      <span v-if="readonly" class="paper-value" :class="`${key}-value`">{{ modelValue[key] }}</span>
      <input v-else class="paper-value" :class="`${key}-value`" :value="modelValue[key]" :aria-label="key === 'add' ? 'Add' : 'PD'" maxlength="10" autocomplete="off" @input="field(key, $event)" />
    </template>
    <template v-for="(option, index) in lensOptions" :key="option">
      <span v-if="readonly" class="paper-check" :style="{ left: `${positions[index][0]}%`, top: `${positions[index][1]}%` }" role="checkbox" :aria-label="option" :aria-checked="modelValue.lens.includes(option)">{{ modelValue.lens.includes(option) ? '✓' : '' }}</span>
      <input v-else class="paper-check" type="checkbox" :aria-label="option" :checked="modelValue.lens.includes(option)" :style="{ left: `${positions[index][0]}%`, top: `${positions[index][1]}%` }" @change="toggle(option, $event)" />
    </template>
  </article>
</template>

<style scoped>
.refraction-prescription { position: relative; width: 100%; aspect-ratio: 1448 / 1086; container-type: inline-size; color: #111; background: white; font-family: Arial, sans-serif; isolation: isolate; }
.prescription-stationery { display: block; width: 100%; height: 100%; position: absolute; inset: 0; z-index: -1; }
.paper-value { position: absolute; display: flex; align-items: center; margin: 0; padding: 0 .3%; border: 0; border-radius: 0; background: transparent; font: 500 1.8cqw/1.1 Arial, sans-serif; color: #111; min-width: 0; }
.patient-name { left: 56%; top: 15.6%; width: 21.5%; height: 4%; font-family: Vazirmatn, sans-serif; white-space: nowrap; }
.visit-date { left: 56%; top: 20.5%; width: 21%; height: 4%; }
.acuity-value { left: 29.5%; width: 18.5%; height: 3.5%; }
/* A complete entered value supplies its own slash (or any other free text).
   Cover the stationery's empty fraction mark only while a value is present. */
.filled-acuity { background: #fff; }
.add-value { left: 30%; top: 67.8%; width: 15%; height: 4%; }
.pd-value { left: 50.6%; top: 67.8%; width: 17.5%; height: 4%; }
.prescription-grid { position: absolute; left: 25.55%; top: 28.85%; width: 47.57%; height: 36.1%; border-collapse: collapse; table-layout: fixed; margin: 0; border: 0; background: transparent; }
.prescription-grid thead tr, .prescription-grid tbody tr { height: 5.4cqw; }
.prescription-grid th, .prescription-grid td { border: 0; padding: 0; background: transparent; vertical-align: middle; }
.prescription-grid input, .cell-value { display: block; box-sizing: border-box; width: 100%; min-width: 0; padding: .3cqw; border: 0; border-radius: 0; background: transparent; color: #111; text-align: center; font: 500 1.65cqw/1.2 Arial, sans-serif; overflow-wrap: anywhere; }
.prescription-grid input { height: 4.8cqw; }
.paper-check { position: absolute; display: grid; place-items: center; width: 2.25%; height: 2.85%; margin: 0; padding: 0; appearance: none; border: 0; background: transparent; color: #111; font: bold 2.3cqw/1 Arial, sans-serif; cursor: pointer; }
input.paper-check:checked::after { content: '✓'; }
input:focus-visible { outline: 2px solid #007bc5; outline-offset: -2px; background: #e8f7ffb0; }
input:hover { background-color: #e8f7ff66; }
@media print {
  .refraction-prescription { print-color-adjust: exact; -webkit-print-color-adjust: exact; break-inside: avoid; }
}
</style>
