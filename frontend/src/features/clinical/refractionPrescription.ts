import { lensOptions, parseRefractionExtras } from './examRecord'
import type { Refraction, RefractionLine } from './types'

// The upper pair is the examiner's measurement; the lower pair remains the
// final prescription used by the existing doctor/optical workflow.
export const prescriptionRows = [
  { stage: 'Subjective', eye: 'OD', label: 'RE' },
  { stage: 'Subjective', eye: 'OS', label: 'LE' },
  { stage: 'Final', eye: 'OD', label: 'RE' },
  { stage: 'Final', eye: 'OS', label: 'LE' },
] as const

export interface PrescriptionForm {
  visionRe: string
  visionLe: string
  pd: string
  add: string
  lens: string[]
  rows: RefractionLine[]
}

export function prescriptionForm(record?: Refraction | null): PrescriptionForm {
  const rows = prescriptionRows.map(({ stage, eye }) => ({
    stage, eye, sphere: '', cylinder: '', axis: '', va: '', add: '',
    ...record?.lines.find((line) => line.stage === stage && line.eye === eye),
  }))
  return {
    visionRe: record?.unaidedVaOd ?? '', visionLe: record?.unaidedVaOs ?? '',
    pd: record?.pd ?? '', add: record?.lines.find((line) => line.stage === 'Final' && line.eye === 'OD')?.add
      ?? record?.lines.find((line) => line.stage === 'Final' && line.eye === 'OS')?.add ?? '',
    lens: parseRefractionExtras(record?.measurements).lens,
    rows,
  }
}

/** Keep hidden historic stages, investigations, notes and per-eye ADD intact.
 * Only an explicit edit of the paper's shared ADD replaces both final ADDs. */
export function prescriptionRequest(form: PrescriptionForm, record: Refraction | null, amendReason?: string) {
  const original = prescriptionForm(record)
  const rows = form.rows.map((row) => ({ ...row,
    add: row.stage === 'Final' && form.add !== original.add ? form.add : row.add,
  }))
  // Replace only a recognized Lens line, preserving every other historic byte.
  const remaining = (record?.measurements ?? '').split('\n').filter((line) => {
    const match = line.match(/^Lens:\s*(.*)$/)
    return !match || !match[1].split(',').every((value) => lensOptions.includes(value.trim()))
  }).join('\n')
  const measurements = [remaining, form.lens.length ? `Lens: ${form.lens.join(', ')}` : ''].filter(Boolean).join('\n')
  return {
    unaidedVaOd: form.visionRe, unaidedVaOs: form.visionLe,
    pinholeVaOd: record?.pinholeVaOd ?? null, pinholeVaOs: record?.pinholeVaOs ?? null,
    nearVaOd: record?.nearVaOd ?? null, nearVaOs: record?.nearVaOs ?? null,
    pd: form.pd, measurements, notes: record?.notes ?? null,
    lines: [...(record?.lines.filter((line) => line.stage !== 'Subjective' && line.stage !== 'Final') ?? []), ...rows],
    version: record?.version ?? null, amendReason,
  }
}
