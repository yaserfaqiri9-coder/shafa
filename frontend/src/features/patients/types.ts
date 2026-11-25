export interface PatientListItem {
  id: number
  fileNumber: string
  firstName: string
  lastName: string | null
  fatherName: string | null
  gender: string
  ageYears: number | null
  ageMonths?: number | null
  phone: string | null
}

export interface Patient extends PatientListItem {
  birthDate: string | null
  estimatedBirthYear: number | null
  address: string | null
  occupation: string | null
  createdAt: string
  version: number
}

export const genderLabels: Record<string, string> = { Male: 'مرد', Female: 'زن', Other: 'دیگر' }

export function patientName(p: { firstName: string; lastName: string | null }) {
  return [p.firstName, p.lastName].filter(Boolean).join(' ')
}

export function patientAge(p: { ageYears?: number | null; ageMonths?: number | null }) {
  return p.ageMonths != null && p.ageMonths < 12
    ? `${p.ageMonths.toLocaleString('fa-AF')} ماهه`
    : p.ageYears != null ? `${p.ageYears.toLocaleString('fa-AF')} ساله` : ''
}
