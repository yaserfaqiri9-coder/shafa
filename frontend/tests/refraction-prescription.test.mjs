import { readFile } from 'node:fs/promises'
import assert from 'node:assert/strict'
import test from 'node:test'
import ts from 'typescript'

const compile = async (path) => ts.transpileModule(await readFile(new URL(path, import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
}).outputText
const url = (text) => `data:text/javascript;base64,${Buffer.from(text).toString('base64')}`
const extras = url(await compile('../src/features/clinical/examRecord.ts'))
const source = (await compile('../src/features/clinical/refractionPrescription.ts')).replace("'./examRecord'", JSON.stringify(extras))
const { prescriptionForm, prescriptionRequest } = await import(url(source))

test('legacy hidden clinical values and stages survive a paper-only edit', () => {
  const legacy = {
    version: 7, measurements: 'K1 OD: 43.25\nLegacy: untouched\nLens: Near, Plastic', notes: 'old note',
    pinholeVaOd: '6/9', nearVaOs: 'N6', lines: [
      { stage: 'Auto', eye: 'OD', sphere: '-3.25' },
      { stage: 'Current', eye: 'OS', sphere: '+1.50' },
      { stage: 'Final', eye: 'OD', sphere: '-2.25', add: '+1.00' },
      { stage: 'Final', eye: 'OS', sphere: '+0.50', add: '+1.50' },
    ],
  }
  const form = prescriptionForm(legacy)
  form.rows[0].sphere = '+1.125'
  form.pd = '62.75'
  form.lens = ['White', 'Distance', 'Bifocal']
  const request = prescriptionRequest(form, legacy)
  assert.equal(request.pinholeVaOd, '6/9')
  assert.equal(request.nearVaOs, 'N6')
  assert.equal(request.notes, legacy.notes)
  assert.equal(request.version, 7)
  assert.equal(request.lines.length, 6)
  assert.deepEqual(request.lines.slice(0, 2), legacy.lines.slice(0, 2))
  assert.equal(request.lines.find(l => l.stage === 'Final' && l.eye === 'OS').add, '+1.50')
  assert.equal(request.measurements, 'K1 OD: 43.25\nLegacy: untouched\nLens: White, Distance, Bifocal')
  form.add = '+2.125'
  assert.ok(prescriptionRequest(form, legacy).lines.filter(l => l.stage === 'Final').every(l => l.add === '+2.125'))
  form.lens = []
  assert.equal(prescriptionRequest(form, legacy).measurements, 'K1 OD: 43.25\nLegacy: untouched')
})

test('all nine checkboxes roundtrip and arbitrary signed text stays unchanged', () => {
  assert.equal(prescriptionForm({ lines: [{ stage: 'Final', eye: 'OS', add: '+1.75' }] }).add, '+1.75')
  const form = prescriptionForm()
  form.visionRe = '-0.125'
  form.visionLe = '6/7.5'
  form.add = '+2.375'
  form.pd = '۶۲٫۵'
  form.lens = ['ph-Brown', 'Ph-Gray', 'White', 'Distance', 'Near', 'Bifocal', 'Progressive', 'Antireflex', 'Plastic']
  form.rows.forEach((r, i) => Object.assign(r, { sphere: `+${i}.125`, cylinder: '-0.375', axis: '-1.5', va: 'N6' }))
  const request = prescriptionRequest(form, null)
  const loaded = prescriptionForm({ ...request, lines: request.lines })
  assert.equal(loaded.visionRe, form.visionRe)
  assert.equal(loaded.visionLe, form.visionLe)
  assert.equal(loaded.pd, form.pd)
  assert.equal(loaded.add, form.add)
  assert.deepEqual(loaded.lens, form.lens)
  assert.equal(loaded.rows.length, 4)
  assert.deepEqual(loaded.rows.map(r => [r.sphere, r.cylinder, r.axis, r.va]), form.rows.map(r => [r.sphere, r.cylinder, r.axis, r.va]))
})
