<script setup lang="ts">
import { askConfirm } from '@/components/confirm'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { api, ApiError, errorMessage, newIdempotencyKey } from '@/api/http'
import { useLiveRefresh } from '@/api/realtime'
import { testOptions } from '@/api/lookups'
import AttachmentPanel from '@/components/AttachmentPanel.vue'
import ShamsiDateInput from '@/components/ShamsiDateInput.vue'
import UiCard from '@/components/UiCard.vue'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import UiRowMenu from '@/components/UiRowMenu.vue'
import TransferDoctorModal from '@/features/visits/TransferDoctorModal.vue'
import type { Patient } from '@/features/patients/types'
import type { VisitRow } from '@/features/visits/types'
import { useAuthStore } from '@/stores/auth'
import { useNotifications } from '@/stores/notifications'
import { formatDate, formatDateTime, todayIso } from '@/utils/date'
import { money } from '@/utils/money'
import DossierForm, { type RxLine } from './DossierForm.vue'
import { allergyMatch, prescriptionFormOf, recentDrugs, rememberDrugs, type DrugOption } from './doctorShortcuts'
import RefractionPrescription from './RefractionPrescription.vue'
import { prescriptionForm } from './refractionPrescription'
import { shamsiPaper } from '@/features/print/paper'
import {
  parseFindings,
  parseMeasurements,
  parsePlan,
  serializeFindings,
  serializePlan,
  type EyeFindings,
} from './examRecord'
import {
  diagnosticStatusLabels,
  diagnosticStatusTones,
  eyeLabels,
  rxStatusLabels,
  surgeryStatusLabels,
  type Chart,
  type Consultation,
  type DiagnosticOrder,
  type HistoryItem,
  type Prescription,
  type Refraction,
  type SurgeryRow,
} from './types'
import { useUnsavedGuard } from './useUnsavedGuard'

const props = defineProps<{ visitId: string }>()
const auth = useAuthStore()

const chart = ref<Chart | null>(null)
const refraction = ref<Refraction | null>(null)
const error = ref('')
const notice = ref('')
const { notify } = useNotifications()
watch(notice, (value) => { if (value) notify.success(value) })
watch(error, (value) => { if (value) notify.danger(value) })
const needsEmergency = ref(false)

/**
 * The page is the patient's paper file: the doctor fills it in place and saves once.
 * The few things that are not on the paper open in a window from the buttons above it.
 */
type PanelKey = 'lab' | 'surgery' | 'history' | 'amend'
const panel = ref<PanelKey | null>(null)
const panels = computed(() => [
  { key: 'lab' as const, label: 'درخواست تشخیصی', icon: 'ri-microscope-line', count: chart.value?.diagnosticOrders.filter((o) => o.status !== 'Cancelled').length ?? 0, ready: readyResults.value.length },
  { key: 'surgery' as const, label: 'عملیات', icon: 'ri-hospital-line', count: 0, ready: 0 },
])

/** Opens an order with its form already showing, so the doctor fills it at once instead of first pressing "new". */
function openPanel(key: PanelKey) {
  if (key === 'lab') orderFormOpen.value = auth.can('diagnostics.create')
  if (key === 'surgery') surgeryFormOpen.value = auth.can('surgery.create')
  panel.value = key
}

/** The rarer actions of the file, kept out of the way in one menu. */
const moreActions = computed(() => [
  { label: 'سابقه مراجعات', icon: 'ri-history-line', onSelect: () => (panel.value = 'history') },
  // Read only: every later change to the file is recorded on save, with no form to fill.
  ...(chart.value?.amendments.length ? [{ label: 'تاریخچه تغییرات', icon: 'ri-file-history-line', onSelect: () => (panel.value = 'amend') }] : []),
  ...(canEdit.value && visitOpen.value ? [{ label: 'انتقال به داکتر دیگر', icon: 'ri-user-shared-line', onSelect: () => (transferring.value = true) }] : []),
])

const canEdit = computed(() => auth.can('consultation.edit'))
const finalized = computed(() => !!chart.value?.consultation?.finalizedAt)
/** The doctor writes straight onto a seen patient's file too, final or not; the server keeps what it said before. */
const examEditable = computed(() => canEdit.value)

const blankFindings = (): EyeFindings => ({ values: {}, note: '' })

/** What triage recorded of the patient's health, read once here for the banner and the prescription. */
const health = computed(() => parseMeasurements(chart.value?.preExam?.measurements))
/** Null when no allergy was recorded; an empty text when one was, without saying to what. */
const allergy = computed(() => (health.value.allergy === 'Y' ? health.value.allergicTo.trim() : null))

/** The patient's own details for the head of the dossier. Optional: the file stays usable without them. */
const patient = ref<Patient | null>(null)

async function loadPatient(patientId: number) {
  patient.value = auth.can('patients.view') ? await api<Patient>(`/patients/${patientId}`).catch(() => null) : null
}

/** Results that came back for this visit: read at the top of the file, not behind a button. */
const readyResults = computed(() => chart.value?.diagnosticOrders.filter((o) => o.status === 'ResultReady') ?? [])

/** What was sent out from this visit and what came back, one tab per department, so the doctor reads one kind at a time. */
type ResultTab = 'refraction' | 'imaging' | 'lab' | 'optical'
const ordersOf = (kind: string) => chart.value?.diagnosticOrders.filter((o) => o.kind === kind && o.status !== 'Cancelled') ?? []
const imagingOrders = computed(() => ordersOf('Imaging'))
const labOrders = computed(() => ordersOf('Lab'))
const opticalRxs = computed(() => chart.value?.opticalPrescriptions.filter((o) => !o.isCancelled) ?? [])
const refractionDone = computed(() => !!refraction.value?.completedAt)
const resultTabs = computed(() =>
  [
    { key: 'refraction' as const, label: 'رفرکشن', show: !!refraction.value || ordersOf('Refraction').length > 0, waiting: !refractionDone.value },
    { key: 'imaging' as const, label: 'عکس‌برداری', show: imagingOrders.value.length > 0, waiting: imagingOrders.value.some((o) => o.status !== 'ResultReady') },
    { key: 'lab' as const, label: 'آزمایش', show: labOrders.value.length > 0, waiting: labOrders.value.some((o) => o.status !== 'ResultReady') },
    { key: 'optical' as const, label: 'نسخه عینک', show: opticalRxs.value.length > 0, waiting: false },
  ].filter((t) => t.show),
)
const chosenResultTab = ref<ResultTab | null>(null)
/** The doctor's choice while it still has something; otherwise the first kind with a result, so what came back shows first. */
const resultTab = computed<ResultTab | null>(() =>
  resultTabs.value.find((t) => t.key === chosenResultTab.value)?.key ?? resultTabs.value.find((t) => !t.waiting)?.key ?? resultTabs.value[0]?.key ?? null,
)
const tabOrders = computed(() => (resultTab.value === 'imaging' ? imagingOrders.value : resultTab.value === 'lab' ? labOrders.value : []))
const orderStatusLabels: Record<string, string> = { Requested: 'در انتظار', InProgress: 'در حال انجام', ResultReady: 'آماده' }



/** The patient's last visit before this one, to compare today's vision and pressure with. */
const previousVisit = computed(() => history.value.find((h) => h.visitId !== Number(props.visitId) && h.visitDate <= (chart.value?.visit.visitDate ?? '')) ?? null)

/** The paper form has three diagnosis lines per eye; they are always there to type into. */
const DX_LINES = 3
const padLines = (lines: string[]) => [...lines, ...Array<string>(Math.max(0, DX_LINES - lines.length)).fill('')]

const exam = reactive({
  od: blankFindings(),
  os: blankFindings(),
  history: '',
  treatmentPlan: '',
  advice: '',
  note: '',
  confidentialNote: '',
  dxOd: padLines([]),
  dxOs: padLines([]),
  /** Diagnoses for both eyes or for neither, which the paper had no line for. */
  dxOther: [] as { eye: string; text: string }[],
})

/** The prescription being written on this page: a new one, or the visit's own that the pharmacy has not started. */
const rxForm = reactive({ items: [] as RxLine[], notes: '', validUntil: null as string | null })

const { dirty, markSaved, applying } = useUnsavedGuard(reactive({ exam, rxForm }))
const version = ref<number | null>(null)
const saving = ref(false)


function fillChart(c: Chart) {
  const consultation = c.consultation
  const parsedPlan = parsePlan(consultation?.treatmentPlan)
  const dx = consultation?.diagnoses ?? []
  const current = editableRx(c)
  applying(() => {
    Object.assign(exam, {
      od: parseFindings(consultation?.findingsOd),
      os: parseFindings(consultation?.findingsOs),
      history: parsedPlan.history,
      treatmentPlan: parsedPlan.plan,
      advice: parsedPlan.advice,
      note: parsedPlan.note,
      confidentialNote: consultation?.confidentialNote ?? '',
      dxOd: padLines(dx.filter((d) => d.eye === 'OD').map((d) => d.text)),
      dxOs: padLines(dx.filter((d) => d.eye === 'OS').map((d) => d.text)),
      dxOther: dx.filter((d) => d.eye !== 'OD' && d.eye !== 'OS').map((d) => ({ ...d })),
    })
    setRx(current)
  })
  examSaved.value = examSnapshot()
  version.value = consultation?.version ?? null
}

const diagnosesToSave = () =>
  [
    ...exam.dxOd.map((text) => ({ eye: 'OD', text })),
    ...exam.dxOs.map((text) => ({ eye: 'OS', text })),
    ...exam.dxOther,
  ].filter((d) => d.text.trim())


/** The exam is sent only when it changed: a prescription alone must not wait on the exam's own rules. */
const examSnapshot = () => JSON.stringify(exam)
const examSaved = ref('')
const examChanged = computed(() => examSnapshot() !== examSaved.value)


async function loadRefraction() {
  if (!auth.can('preexam.view')) return
  // The summary is optional: the chart stays usable if this one read fails.
  refraction.value = (await api<Refraction | undefined>(`/refractions/${props.visitId}`).catch(() => undefined)) ?? null
}

/** A result recorded for this visit while the file is open shows at once; what the doctor is typing is untouched. */
async function refreshResults() {
  if (!chart.value) return
  const fresh = await api<Chart>(`/consultations/${props.visitId}/chart`)
  if (!chart.value || fresh.visit.id !== chart.value.visit.id) return
  chart.value.diagnosticOrders = fresh.diagnosticOrders
  chart.value.opticalPrescriptions = fresh.opticalPrescriptions
  await loadRefraction()
}
// Only this visit's events, and no timed refresh: each read of the file is recorded as a view.
useLiveRefresh(['DiagnosticResultReady', 'OpticalPrescriptionCreated'], refreshResults, 0, (_, ids) => Number(ids.visitId) === Number(props.visitId))

async function load() {
  error.value = ''
  needsEmergency.value = false
  try {
    const sameVisit = chart.value?.visit.id === Number(props.visitId)
    // The patient this doctor called is now with the doctor. The server ignores any other chart; nothing is announced.
    if (!sameVisit && auth.can('consultation.edit')) await api(`/consultations/${props.visitId}/start`, { method: 'POST' }).catch(() => undefined)
    chart.value = await api<Chart>(`/consultations/${props.visitId}/chart`)
    // Reloading after an order or a prescription must never overwrite what the doctor is still typing.
    if (!sameVisit || !dirty.value) fillChart(chart.value)
    // The exam can start now; the refraction summary and the past visits fill in when they arrive.
    loadRefraction()
    if (!sameVisit) {
      loadHistory(true)
      loadPatient(chart.value.visit.patientId)
    }
    // Once the exam is saved the patient is on the seen list, so the next one waiting is offered.
    if (chart.value.consultation) loadNext()
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'clinical_access_required') needsEmergency.value = true
    error.value = errorMessage(cause)
  }
}

const transferring = ref(false)

async function onTransferred() {
  transferring.value = false
  notice.value = 'مسئولیت این مریض به داکتر جدید انتقال یافت.'
  await load()
}

const emergency = reactive({ reason: '', patientId: '' , saving: false })

async function requestEmergency() {
  emergency.saving = true
  try {
    const visit = await api<{ patientId: number }>(`/visits/${props.visitId}`)
    await api(`/clinical/patients/${visit.patientId}/emergency-access`, { method: 'POST', body: { reason: emergency.reason } })
    emergency.reason = ''
    await load()
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    emergency.saving = false
  }
}

function explain(cause: unknown) {
  error.value = errorMessage(cause)
  notice.value = ''
}

async function persistExam() {
  const saved = await api<Consultation>(`/consultations/${props.visitId}`, {
    method: 'PUT',
    body: {
      findingsOd: serializeFindings(exam.od),
      findingsOs: serializeFindings(exam.os),
      treatmentPlan: serializePlan({ history: exam.history, plan: exam.treatmentPlan, advice: exam.advice, note: exam.note }),
      confidentialNote: exam.confidentialNote,
      diagnoses: diagnosesToSave(),
      version: version.value,
    },
  })
  version.value = saved.version
  examSaved.value = examSnapshot()
}

/** Problems the doctor can fix before anything is sent, so nothing is half saved. */
function draftProblem() {
  if (rxEditable.value && rxChanged.value && rx.editing && rxLines().length === 0) return 'نسخه بدون دوا ذخیره نمی‌شود. برای حذف کامل نسخه، دکمه «لغو نسخه» را بزنید.'
  return ''
}

/**
 * Saves everything typed on the page: the exam with its diagnoses, the prescription and the next visit.
 * Returns what was sent to the pharmacy, if anything, for the message.
 */
async function saveDraft() {
  if (examEditable.value && examChanged.value) await persistExam()
  const rxSent = rxEditable.value && rxChanged.value ? await persistRx() : false
  markSaved()
  return rxSent
}

async function saveAll() {
  const problem = draftProblem()
  if (problem) {
    error.value = problem
    notice.value = ''
    return
  }
  saving.value = true
  error.value = ''
  try {
    const rxSent = await saveDraft()
    notice.value = rxSent ? 'ذخیره شد و نسخه دوا برای دواخانه ارسال شد.' : 'ذخیره شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    saving.value = false
  }
}

/** Ticked when the patient will come back today, for example after an ordered test; the visit then stays open. */
const keepVisitOpen = ref(false)

/** The next patient waiting in this doctor's queue, offered once the exam is saved. */
const nextVisit = ref<VisitRow | null>(null)

/** The same patient the queue page shows as next: the Backend's order, skipping absent patients and those away at a test. Opening it does not call. */
async function loadNext() {
  const current = Number(props.visitId)
  const [rows, desk] = await Promise.all([
    api<VisitRow[]>('/clinical/worklist?kind=doctor').catch(() => [] as VisitRow[]),
    api<{ rows: { visitId: number | null; present?: boolean }[] }>('/calls/desk/Doctor').catch(() => null),
  ])
  const ready = (r: VisitRow) => r.id !== current && (r.status === 'ReadyForDoctor' || r.status === 'InConsultation')
  const entry = desk?.rows.find((e) => e.visitId !== current && e.present !== false && rows.some((r) => r.id === e.visitId && ready(r)))
  nextVisit.value = entry ? rows.find((r) => r.id === entry.visitId) ?? null : desk ? null : rows.find(ready) ?? null
}

/** Ctrl+S saves the page, as in any editor. */
function onKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    if (chart.value && (canEdit.value || rxEditable.value) && !saving.value) saveAll()
  }
}
onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))

async function finalize(closeVisit: boolean) {
  const problem = draftProblem()
  if (problem) {
    error.value = problem
    notice.value = ''
    return
  }
  saving.value = true
  error.value = ''
  try {
    // Unsaved typing goes in first, so finalising can never drop what is on screen.
    await saveDraft()
    await api(`/consultations/${props.visitId}/finalize`, { method: 'POST', body: { version: version.value, closeVisit } })
    notice.value = closeVisit ? 'معاینه نهایی و مراجعه تکمیل شد.' : 'معاینه نهایی شد.'
    await load()
  } catch (cause) {
    explain(cause)
  } finally {
    saving.value = false
  }
}

const visitOpen = computed(() => !['Completed', 'Cancelled'].includes(chart.value?.visit.status ?? ''))
/** A stored plan shows as its three labelled parts, never with its storage tags. */
function shownValue(field: string, value: string | null) {
  if (value == null) return '—'
  if (field !== 'TreatmentPlan') return value
  const parts = parsePlan(value)
  return [parts.history && `شرح حال: ${parts.history}`, parts.plan && `پلان: ${parts.plan}`, parts.advice && `توصیه: ${parts.advice}`, parts.note && `یادداشت: ${parts.note}`].filter(Boolean).join('\n') || '—'
}

const amendFields: Record<string, string> = {
  Diagnoses: 'تشخیص',
  FindingsOd: 'یافته‌های چشم راست',
  FindingsOs: 'یافته‌های چشم چپ',
  TreatmentPlan: 'پلان درمان',
  ConfidentialNote: 'یادداشت محرمانه',
}

/** Everything else the history can show: exam, refraction, prescriptions, lab results and surgery. */
const historyFields: Record<string, string> = {
  ...amendFields,
  ChiefComplaint: 'شکایت اصلی',
  History: 'سوابق',
  VisualAcuityOd: 'دید چشم راست',
  VisualAcuityOs: 'دید چشم چپ',
  IopOd: 'فشار چشم راست',
  IopOs: 'فشار چشم چپ',
  Measurements: 'اندازه‌گیری‌ها',
  Notes: 'یادداشت',
  UnaidedVaOd: 'دید بدون اصلاح راست',
  UnaidedVaOs: 'دید بدون اصلاح چپ',
  PinholeVaOd: 'Pinhole راست',
  PinholeVaOs: 'Pinhole چپ',
  NearVaOd: 'دید نزدیک راست',
  NearVaOs: 'دید نزدیک چپ',
  Pd: 'PD',
  Lines: 'ردیف‌های بینایی‌سنجی',
  Items: 'دواهای نسخه',
  SphereOd: 'SPH راست',
  CylinderOd: 'CYL راست',
  AxisOd: 'AXIS راست',
  AddOd: 'ADD راست',
  SphereOs: 'SPH چپ',
  CylinderOs: 'CYL چپ',
  AxisOs: 'AXIS چپ',
  AddOs: 'ADD چپ',
  ResultText: 'نتیجه آزمایش',
  SurgeryType: 'نوع عملیات',
  Eye: 'چشم عملیات',
  PreOpChecks: 'بررسی‌های پیش از عملیات',
  OperationReport: 'گزارش عملیات',
  PostOpStatus: 'وضعیت پس از عملیات',
}

/** The prescription on the page and the server state it started from. */
const rx = reactive({ editing: null as Prescription | null, saved: '', key: newIdempotencyKey() })

/** The visit's newest prescription that may still change: issued, and nothing of it handed out yet. */
function editableRx(c: Chart) {
  if (!auth.can('prescriptions.edit')) return null
  return [...c.prescriptions].reverse().find((p) => p.status === 'Issued' && p.items.every((i) => !i.dispensedQuantity)) ?? null
}

const rxLines = () => rxForm.items.filter((i) => i.drugName.trim())
const rxSnapshot = () => JSON.stringify({ items: rxLines(), notes: rxForm.notes.trim(), validUntil: rxForm.validUntil })
const rxChanged = computed(() => rxSnapshot() !== rx.saved)

/** Loads a prescription into the page, or an empty one when `p` is null. */
function setRx(p: Prescription | null) {
  rx.editing = p
  Object.assign(rxForm, {
    notes: p?.notes ?? '',
    validUntil: p?.validUntil ?? null,
    items: p?.items.map((i) => ({ drugName: i.drugName, dose: i.dose ?? '', route: i.route ?? '', duration: i.duration ?? '', instructions: i.instructions ?? '', quantity: i.quantity })) ?? [],
  })
  rx.saved = rxSnapshot()
  loadDrugForms(rxForm.items.map((i) => i.drugName))
}

/** Form of each drug on the page by name (drop, tablet…), shown beside it; a picked drug brings its own. */
const drugInfo = reactive<Record<string, DrugOption>>({})

async function loadDrugForms(names: string[]) {
  const missing = [...new Set(names.map((n) => n.trim()).filter((n) => n && !(n in drugInfo)))]
  if (!missing.length) return
  const params = new URLSearchParams()
  missing.forEach((n) => params.append('names', n))
  try {
    Object.assign(drugInfo, await api<Record<string, DrugOption>>(`/prescriptions/drug-details?${params}`))
  } catch {
    // Only a label beside the drug; the prescription works without it.
  }
}

/** A new prescription can always be started; an issued one can change until the pharmacy starts on it. */
const rxEditable = computed(() => (rx.editing ? auth.can('prescriptions.edit') : auth.can('prescriptions.create')))

const recent = ref(recentDrugs(auth.user?.id))

async function addDrug(drug: DrugOption) {
  // A drug the pharmacy does not stock is written whole, as the old system wrote it: "Eye drop Ocuzol (Antazoline + Naphazoline)".
  // The pharmacy has no record of it, so the form and generic would otherwise be lost once the prescription is saved.
  const name = drug.outside
    ? [prescriptionFormOf(drug.form), drug.name, drug.generic ? `(${drug.generic})` : ''].filter(Boolean).join(' ')
    : drug.name
  if (drug.form || drug.generic) drugInfo[name] = drug.outside ? { ...drug, name, generic: null } : drug
  const index = rxForm.items.findIndex((i) => i.drugName.trim().toLowerCase() === name.toLowerCase())
  const clash = index < 0 ? allergyMatch(allergy.value, name) : []
  if (clash.length && !(await askConfirm(`هشدار حساسیت: مریض به «${allergy.value}» حساسیت دارد و نام این دوا «${clash.join('، ')}» را دارد.\nبا این حال دوا اضافه شود؟`, { title: 'هشدار حساسیت', confirmLabel: 'اضافه شود', danger: true }))) return
  if (index < 0) {
    const known = recent.value.find((d) => d.name.toLowerCase() === name.toLowerCase())
    // Every new line starts with the hospital's editable paper-form default; the doctor may replace it.
    rxForm.items.push({ drugName: name, dose: '1x3', route: '', duration: '', instructions: '', quantity: known?.quantity || 1 })
  }
  // The cursor goes to the dose, the one thing left to write for the drug.
  const at = index < 0 ? rxForm.items.length : index + 1
  nextTick(() => document.querySelector<HTMLInputElement>(`[aria-label="طریقه مصرف ${at}"]`)?.focus())
}

/** Sends the prescription; true when the pharmacy received something new. */
async function persistRx() {
  const items = rxLines().map((i) => ({ ...i, drugName: i.drugName.trim(), quantity: i.quantity || 1 }))
  try {
    if (rx.editing) {
      await api(`/prescriptions/${rx.editing.id}`, { method: 'PUT', body: { notes: rxForm.notes, items, version: rx.editing.version, validUntil: rxForm.validUntil } })
    } else {
      if (items.length === 0) return false
      await api('/prescriptions', { method: 'POST', idempotencyKey: rx.key, body: { visitId: Number(props.visitId), notes: rxForm.notes, items, validUntil: rxForm.validUntil } })
      rx.key = newIdempotencyKey()
    }
  } catch (cause) {
    if (cause instanceof ApiError && cause.status !== 0 && !rx.editing) rx.key = newIdempotencyKey()
    throw cause
  }
  rx.saved = rxSnapshot()
  rememberDrugs(auth.user?.id, items)
  recent.value = recentDrugs(auth.user?.id)
  return true
}

/** Moves the page to another prescription of this visit, or to a blank new one. */
async function openRx(p: Prescription | null) {
  if (rxChanged.value && !(await askConfirm('تغییرات نسخه فعلی ذخیره نشده‌اند. از آن صرف نظر شود؟', { confirmLabel: 'صرف نظر', danger: true }))) return
  setRx(p)
  rx.key = newIdempotencyKey()
}

const otherPrescriptions = computed(() => chart.value?.prescriptions.filter((p) => p.id !== rx.editing?.id) ?? [])

/** The doctor answers a pharmacy's request for review, or renews a prescription whose validity ran out. */
const reapprove = reactive({ rx: null as Prescription | null, validUntil: null as string | null, note: '', saving: false, error: '' })

async function submitReapprove() {
  reapprove.saving = true
  reapprove.error = ''
  try {
    await api(`/prescriptions/${reapprove.rx!.id}/reapprove`, { method: 'POST', body: { validUntil: reapprove.validUntil, note: reapprove.note, version: reapprove.rx!.version } })
    reapprove.rx = null
    notice.value = 'نسخه دوباره تأیید شد و دواخانه می‌تواند آن را تحویل دهد.'
    await load()
  } catch (cause) {
    reapprove.error = errorMessage(cause)
  } finally {
    reapprove.saving = false
  }
}

const rxNeedsDoctor = (p: Prescription) =>
  ['Issued', 'PartiallyDispensed'].includes(p.status) && (p.pharmacyStatus === 'NeedsDoctorReview' || (!!p.validUntil && p.validUntil < todayIso()))

const tests = ref<(ComboOption & { kind: string; serviceId: number | null; price: number | null })[]>([])
const testsLoading = ref(false)
const orderFormOpen = ref(false)
const order = reactive({ testId: null as number | null, note: '', returnToDoctor: true, discount: 0, discountMode: 'Amount' as 'Amount' | 'Percent', key: newIdempotencyKey(), saving: false })
const selectedTest = computed(() => tests.value.find((t) => t.value === order.testId) ?? null)
const orderKind = computed(() => selectedTest.value?.kind ?? 'Lab')
const orderPrice = computed(() => selectedTest.value?.price ?? 0)
const orderDiscountAmount = computed(() => order.discount <= 0 ? 0 : order.discountMode === 'Percent' ? Math.round(orderPrice.value * order.discount) / 100 : order.discount)
const orderFinal = computed(() => Math.max(0, orderPrice.value - orderDiscountAmount.value))

async function submitOrder() {
  order.saving = true
  error.value = ''
  try {
    // The patient may be away for a while, so what is on screen goes onto the record first.
    // The prescription waits for the doctor's own save: it would reach the pharmacy at once.
    if (examChanged.value && examEditable.value) {
      await persistExam()
      if (!rxChanged.value) markSaved()
    }
    const kind = orderKind.value
    await api('/diagnostic-orders', {
      method: 'POST',
      idempotencyKey: order.key,
      body: { patientId: chart.value!.visit.patientId, visitId: Number(props.visitId), testId: order.testId! > 0 ? order.testId : 0, serviceId: order.testId! < 0 ? -order.testId! : null, note: order.note, returnToDoctor: order.returnToDoctor, discount: order.discount > 0 ? { amount: order.discount, mode: order.discountMode } : null },
    })
    // The form stays open and empty, so a second test is ordered without another click.
    Object.assign(order, { testId: null, note: '', returnToDoctor: true, discount: 0, discountMode: 'Amount', key: newIdempotencyKey() })
    notice.value = kind === 'Lab' ? 'درخواست برای آزمایشگاه ارسال شد.' : 'درخواست ثبت شد. پس از پرداخت کامل فیس شروع می‌شود.'
    await load()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) order.key = newIdempotencyKey()
  } finally {
    order.saving = false
  }
}

const attachmentsFor = ref<number | null>(null)

const noteEdit = reactive({ order: null as DiagnosticOrder | null, note: '', saving: false, error: '' })

async function saveOrderNote() {
  noteEdit.saving = true
  noteEdit.error = ''
  try {
    await api(`/diagnostic-orders/${noteEdit.order!.id}/note`, { method: 'PUT', body: { note: noteEdit.note, version: noteEdit.order!.version } })
    noteEdit.order = null
    notice.value = 'توضیح درخواست ویرایش شد.'
    await load()
  } catch (cause) {
    noteEdit.error = errorMessage(cause)
  } finally {
    noteEdit.saving = false
  }
}

async function cancelItem(path: string, body?: unknown) {
  error.value = ''
  try {
    await api(path, { method: 'POST', body })
    notice.value = 'لغو شد.'
    await load()
  } catch (cause) {
    explain(cause)
  }
}

const cancelRx = (p: Prescription) => cancelItem(`/prescriptions/${p.id}/cancel?version=${p.version}`)
const cancelOrder = (o: DiagnosticOrder) => cancelItem(`/diagnostic-orders/${o.id}/cancel`, { version: o.version })

const surgeries = ref<SurgeryRow[]>([])
const operations = ref<{ id: number; name: string; price: number | null; packages: { name: string; quantity: number }[] }[]>([])
const operationOptions = computed<ComboOption[]>(() => operations.value.map((o) => ({ value: o.id, label: o.name, hint: money(o.price) })))
const surgery = reactive({ surgeryType: '', eye: 'OD', operationDefinitionId: null as number | null, amount: null as number | null, discount: 0, discountMode: 'Amount' as 'Amount' | 'Percent', key: newIdempotencyKey(), saving: false })
const selectedOperation = computed(() => operations.value.find((o) => o.id === surgery.operationDefinitionId) ?? null)
const surgeryBasePrice = computed(() => surgery.amount ?? selectedOperation.value?.price ?? 0)
const surgeryDiscountAmount = computed(() => surgery.discount <= 0 ? 0 : surgery.discountMode === 'Percent' ? Math.round(surgeryBasePrice.value * surgery.discount) / 100 : surgery.discount)
const surgeryFinal = computed(() => Math.max(0, surgeryBasePrice.value - surgeryDiscountAmount.value))
const surgeryFormOpen = ref(false)
async function loadSurgeries() {
  if (!auth.can('surgery.view')) return
  const page = await api<{ items: SurgeryRow[] }>('/surgeries?pageSize=100')
  surgeries.value = page.items.filter((s) => s.visitId === Number(props.visitId))
}

async function submitSurgery() {
  surgery.saving = true
  error.value = ''
  try {
    await api('/surgeries', {
      method: 'POST',
      idempotencyKey: surgery.key,
      body: { visitId: Number(props.visitId), surgeryType: selectedOperation.value?.name, eye: surgery.eye, operationDefinitionId: surgery.operationDefinitionId, amount: surgery.amount ?? undefined, discount: surgery.discount > 0 ? { amount: surgery.discount, mode: surgery.discountMode } : null },
    })
    Object.assign(surgery, { surgeryType: '', operationDefinitionId: null, amount: null, discount: 0, discountMode: 'Amount', key: newIdempotencyKey() })
    surgeryFormOpen.value = false
    notice.value = 'پرونده عملیات ایجاد شد.'
    await loadSurgeries()
  } catch (cause) {
    explain(cause)
    if (cause instanceof ApiError && cause.status !== 0) surgery.key = newIdempotencyKey()
  } finally {
    surgery.saving = false
  }
}

const history = ref<HistoryItem[]>([])

/** `quiet` for the read on opening the file: the comparison with the last visit is a help, not a reason for an error. */
async function loadHistory(quiet = false) {
  if (!chart.value) return
  try {
    history.value = await api<HistoryItem[]>(`/clinical/patients/${chart.value.visit.patientId}/history`)
  } catch (cause) {
    if (!quiet) explain(cause)
  }
}

watch(panel, (value) => {
  notice.value = ''
  if (value === 'history' && chart.value) loadHistory()
  if (value === 'surgery') {
    loadSurgeries().catch(explain)
    if (auth.can('surgery.create')) {
      api<typeof operations.value>('/operations').then((rows) => (operations.value = rows)).catch(explain)
    }
  }
  if (value === 'lab' && auth.can('diagnostics.create')) {
    testsLoading.value = true
    Promise.all([
      testOptions(),
      api<{ id: number; name: string; code: string; isActive: boolean; isSurgical: boolean; hasDiagnosticTest: boolean; currentPrice: number | null }[]>('/services'),
    ])
      .then(([rows, serviceRows]) => {
        const linked = new Set(rows.map((t) => t.serviceId))
        const priceFor = (serviceId: number | null) => serviceRows.find((s) => s.id === serviceId)?.currentPrice ?? null
        tests.value = [
          ...rows.map((t) => ({ ...t, price: priceFor(t.serviceId) })),
          ...serviceRows.filter((s) => s.isActive && !s.isSurgical && !s.hasDiagnosticTest && !['VISIT-FEE', 'REFRACTION', 'IMAGING'].includes(s.code) && !linked.has(s.id))
            .map((s) => ({ value: -s.id, label: s.name, kind: 'Lab', serviceId: s.id, price: s.currentPrice })),
        ]
      })
      .catch(explain)
      .finally(() => (testsLoading.value = false))
  }
})

onMounted(load)
watch(() => props.visitId, () => {
  load()
})
</script>

<template>
  <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>

  <UiCard v-if="needsEmergency" title="دسترسی اضطراری">
    <p>این مریض به شما مربوط نیست. در حالت اضطرار می‌توانید با ثبت دلیل، برای 4 ساعت دسترسی بگیرید. این دسترسی ثبت و قابل بررسی است.</p>
    <form @submit.prevent="requestEmergency">
      <label class="form-label" for="em-reason">دلیل</label>
      <input id="em-reason" v-model="emergency.reason" class="form-control mb-3" minlength="5" maxlength="500" required />
      <button type="submit" class="btn btn-primary" :disabled="emergency.saving">ثبت دلیل و باز کردن پرونده</button>
    </form>
  </UiCard>

  <!-- Opens at once in the shape of the page; nothing can be typed until this visit's record has arrived. -->
  <div v-if="!chart && !error && !needsEmergency" class="d-grid gap-4" role="status" aria-label="در حال بارگذاری پرونده">
    <section class="xr-card chart-placeholder">
      <span class="xr-skeleton xr-skeleton--title"></span>
      <span class="xr-skeleton xr-skeleton--wide"></span>
    </section>
    <section class="xr-card chart-placeholder chart-placeholder--sheet">
      <span v-for="n in 12" :key="n" class="xr-skeleton"></span>
    </section>
  </div>

  <div v-if="chart" class="chart-shell">
    <!-- The patient's details sit at the head of the dossier itself, as on the paper; this strip holds only the orders. -->
    <section class="chart-header" aria-label="اقدامات پرونده">
      <!-- Compact report-style actions beside the dossier; each item opens its existing form. -->
      <nav class="chart-orders" aria-label="درخواست‌ها">
        <div class="chart-orders__group" role="group" aria-label="نوع درخواست">
          <button v-for="item in panels" :key="item.key" type="button" class="chart-orders__item" :class="`chart-orders__item--${item.key}`" aria-haspopup="dialog" @click="openPanel(item.key)">
            <span class="chart-orders__icon"><i :class="item.icon" aria-hidden="true"></i></span>
            <span class="chart-orders__copy">
              <strong>{{ item.label }}</strong>
            </span>
            <span class="chart-orders__badges">
              <span v-if="item.count" class="chart-header__count" :aria-label="`${item.count} ثبت‌شده`">{{ item.count }}</span>
              <span v-if="item.ready" class="chart-header__count chart-header__count--ready">{{ item.ready }} نتیجه</span>
            </span>
            <i class="chart-orders__arrow ri-arrow-left-line" aria-hidden="true"></i>
          </button>
        </div>
        <span class="chart-orders__end">
          <RouterLink v-if="auth.can('consultation.print')" :to="`/print/dossier/${visitId}`" target="_blank" class="btn btn-quiet btn-sm" aria-label="چاپ دوسیه" title="چاپ دوسیه">
            <i class="ri-printer-line" aria-hidden="true"></i>
          </RouterLink>
          <UiRowMenu label="گزینه‌های بیشتر پرونده" :items="moreActions" />
        </span>
      </nav>
    </section>
    <TransferDoctorModal v-if="transferring" :visit="chart.visit" @close="transferring = false" @saved="onTransferred" />

    <!-- What the doctor should know before writing: results that came back, and how the patient was last time. -->
    <section v-if="resultTabs.length || previousVisit" class="xr-card chart-brief" aria-label="خلاصه پیش از معاینه">
      <div v-if="resultTabs.length" class="chart-brief__block">
        <div class="results__head">
          <h2 class="chart-brief__title">نتایج این مراجعه</h2>
          <div class="results__tabs" role="tablist" aria-label="نوع نتیجه">
            <button
              v-for="t in resultTabs"
              :key="t.key"
              type="button"
              role="tab"
              class="results__tab"
              :class="{ 'results__tab--active': resultTab === t.key }"
              :aria-selected="resultTab === t.key"
              @click="chosenResultTab = t.key"
            >
              {{ t.label }}
              <span class="results__dot" :class="t.waiting ? 'results__dot--wait' : 'results__dot--ready'" :title="t.waiting ? 'در انتظار نتیجه' : 'آماده'"></span>
            </button>
          </div>
        </div>

        <div role="tabpanel">
          <template v-if="resultTab === 'refraction'">
            <p v-if="!refraction" class="results__empty">بینایی‌سنج هنوز نتیجه را ثبت نکرده است.</p>
            <template v-else>
              <div class="refraction-result-preview">
                <RefractionPrescription :model-value="prescriptionForm(refraction)" :patient-name="chart.visit.patientName" :date="shamsiPaper(chart.visit.visitDate)" readonly />
              </div>
              <RouterLink :to="`/print/refraction/${visitId}`" target="_blank" class="btn btn-soft mt-2">چاپ نسخه</RouterLink>
            </template>
          </template>

          <template v-else-if="resultTab === 'imaging' || resultTab === 'lab'">
            <div v-for="o in tabOrders" :key="o.id" class="results__item">
              <div class="chart-brief__line">
                <strong>{{ o.testName }}</strong>
                <span class="xr-badge" :class="o.status === 'ResultReady' ? 'xr-badge--green' : 'xr-badge--orange'">{{ orderStatusLabels[o.status] ?? o.status }}</span>
                <span v-if="o.performedAt" class="small text-muted">{{ formatDateTime(o.performedAt) }}</span>
                <button v-if="o.status === 'ResultReady'" type="button" class="btn btn-quiet btn-sm ms-auto" @click="attachmentsFor = attachmentsFor === o.id ? null : o.id">
                  <i class="ri-attachment-2" aria-hidden="true"></i> {{ attachmentsFor === o.id ? 'بستن فایل‌ها' : 'فایل‌ها' }}
                </button>
              </div>
              <p v-if="o.status === 'ResultReady'" class="chart-brief__text" dir="auto">{{ o.resultText || 'نتیجه متنی ندارد؛ فایل‌ها را ببینید.' }}</p>
              <AttachmentPanel v-if="attachmentsFor === o.id && panel !== 'lab'" :key="o.id" owner-type="DiagnosticOrder" :owner-id="o.id" :can-upload="false" />
            </div>
          </template>

          <template v-else-if="resultTab === 'optical'">
            <div v-for="o in opticalRxs" :key="o.id" class="results__item">
              <div class="chart-brief__line">
                <strong class="ltr-num">{{ o.rxNumber }}</strong>
                <span class="small text-muted">{{ o.fromRefraction ? 'از بینایی‌سنج' : 'داکتر' }} · {{ formatDateTime(o.createdAt) }}</span>
              </div>
              <table class="chart-brief__compare" dir="ltr">
                <thead><tr><th></th><th>SPH</th><th>CYL</th><th>AXIS</th><th>ADD</th></tr></thead>
                <tbody>
                  <tr><th>OD</th><td>{{ o.sphereOd || '—' }}</td><td>{{ o.cylinderOd || '—' }}</td><td>{{ o.axisOd || '—' }}</td><td>{{ o.addOd || '—' }}</td></tr>
                  <tr><th>OS</th><td>{{ o.sphereOs || '—' }}</td><td>{{ o.cylinderOs || '—' }}</td><td>{{ o.axisOs || '—' }}</td><td>{{ o.addOs || '—' }}</td></tr>
                </tbody>
              </table>
              <p v-if="o.pd" class="results__meta" dir="ltr">PD {{ o.pd }}</p>
            </div>
          </template>
        </div>
      </div>
      <div v-if="previousVisit" class="chart-brief__block">
        <h2 class="chart-brief__title">
          <i class="ri-history-line" aria-hidden="true"></i> مراجعه قبلی:
          <RouterLink :to="`/chart/${previousVisit.visitId}`" title="باز کردن مراجعه قبلی">{{ formatDate(previousVisit.visitDate) }}</RouterLink>
          <span v-if="previousVisit.doctorName" class="small text-muted">— {{ previousVisit.doctorName }}</span>
        </h2>
        <table class="chart-brief__compare" dir="ltr">
          <thead><tr><th></th><th>VA OD</th><th>VA OS</th><th>IOP OD</th><th>IOP OS</th></tr></thead>
          <tbody>
            <tr><th dir="rtl">قبلی</th><td>{{ previousVisit.visualAcuityOd || '—' }}</td><td>{{ previousVisit.visualAcuityOs || '—' }}</td><td>{{ previousVisit.iopOd || '—' }}</td><td>{{ previousVisit.iopOs || '—' }}</td></tr>
            <tr><th dir="rtl">امروز</th><td>{{ chart.preExam?.visualAcuityOd || '—' }}</td><td>{{ chart.preExam?.visualAcuityOs || '—' }}</td><td>{{ chart.preExam?.iopOd || '—' }}</td><td>{{ chart.preExam?.iopOs || '—' }}</td></tr>
          </tbody>
        </table>
        <p v-if="previousVisit.diagnoses.length" class="chart-brief__text" dir="auto"><b dir="rtl">تشخیص:</b> {{ previousVisit.diagnoses.map((d) => (d.eye === 'Both' ? 'OU' : d.eye === 'None' ? '' : d.eye) + ' ' + d.text).join('، ') }}</p>
        <p v-if="parsePlan(previousVisit.treatmentPlan).plan" class="chart-brief__text" dir="auto"><b dir="rtl">پلان:</b> {{ parsePlan(previousVisit.treatmentPlan).plan }}</p>
      </div>
    </section>

    <!-- The patient's file itself: the doctor writes straight onto the paper form, both sides, and saves once. -->
    <form class="chart-dossier" aria-label="دوسیه مریض" @submit.prevent="saveAll">
      <DossierForm
        :chart="chart"
        :refraction="refraction"
        :exam="exam"
        :rx="rxForm"
        :patient="patient"
        :allergy="allergy"
        :drug-info="drugInfo"
        :exam-editable="examEditable"
        :rx-editable="rxEditable"
        @pick-drug="addDrug"
      >
        <template #rx-meta>
          <span v-if="rx.editing" class="rx-meta" dir="rtl">
            <strong class="ltr-num">{{ rx.editing.rxNumber }}</strong>
            <span class="rx-state">{{ rxStatusLabels[rx.editing.status] }}</span>
            <span v-if="rxNeedsDoctor(rx.editing)" class="rx-state rx-state--warn">نیازمند تأیید دوباره داکتر</span>
            <button v-if="rxNeedsDoctor(rx.editing) && auth.can('prescriptions.edit')" type="button" class="rx-act" @click="Object.assign(reapprove, { rx: rx.editing, validUntil: null, note: '', error: '' })">تأیید دوباره</button>
            <button v-if="auth.can('prescriptions.cancel')" type="button" class="rx-act rx-act--danger" @click="cancelRx(rx.editing)">لغو نسخه</button>
          </span>
        </template>
        <template #rx-extra>
          <div v-if="otherPrescriptions.length" class="rx-others">
            <div v-for="p in otherPrescriptions" :key="p.id" class="issued">
              <div class="issued__head">
                <strong class="ltr-num">{{ p.rxNumber }}</strong>
                <span class="rx-state">{{ rxStatusLabels[p.status] }}</span>
                <span v-if="p.validUntil" class="rx-state">معتبر تا {{ formatDate(p.validUntil) }}</span>
                <span v-if="rxNeedsDoctor(p)" class="rx-state rx-state--warn">نیازمند تأیید دوباره داکتر</span>
                <span class="issued__acts">
                  <button v-if="rxNeedsDoctor(p) && auth.can('prescriptions.edit')" type="button" class="rx-act" @click="Object.assign(reapprove, { rx: p, validUntil: null, note: '', error: '' })">تأیید دوباره</button>
                  <RouterLink v-if="auth.can('prescriptions.print')" :to="`/print/rx/${p.id}`" target="_blank" class="rx-act">چاپ</RouterLink>
                  <button v-if="p.status === 'Issued' && p.items.every((i) => !i.dispensedQuantity) && auth.can('prescriptions.edit')" type="button" class="rx-act" @click="openRx(p)">ویرایش</button>
                  <button v-if="p.status === 'Issued' && auth.can('prescriptions.cancel')" type="button" class="rx-act rx-act--danger" @click="cancelRx(p)">لغو نسخه</button>
                </span>
              </div>
              <ul class="mb-0">
                <li v-for="item in p.items" :key="item.id">{{ item.drugName }} — {{ [item.dose, item.route, item.duration, item.instructions].filter(Boolean).join(' ') }} (تعداد: {{ item.quantity }})</li>
              </ul>
            </div>
          </div>
        </template>
      </DossierForm>


      <div v-if="canEdit || rxEditable" class="xr-action-bar chart-actions">
        <button type="submit" class="btn btn-primary" :disabled="saving" title="Ctrl+S"><i class="ri-save-line" aria-hidden="true"></i> ذخیره</button>
        <template v-if="examEditable && !finalized && auth.can('consultation.approve') && (version !== null || dirty)">
          <button type="button" class="btn btn-soft" :disabled="saving" @click="finalize(!keepVisitOpen)">
            <i class="ri-check-double-line" aria-hidden="true"></i> نهایی‌سازی
          </button>
          <label class="form-check chart-actions__keep" title="برای مریضی که امروز پس از آزمایش یا عکس‌برداری دوباره نزد شما می‌آید">
            <input v-model="keepVisitOpen" class="form-check-input" type="checkbox" />
            <span class="form-check-label">مریض امروز برمی‌گردد</span>
          </label>
        </template>
        <span v-if="finalized" class="xr-badge xr-badge--primary">معاینه نهایی شده است</span>
        <span v-if="dirty || rxChanged" class="xr-action-bar__status">تغییرات ذخیره نشده</span>
        <template v-if="chart.consultation">
          <RouterLink v-if="nextVisit" :to="`/chart/${nextVisit.id}`" class="btn btn-primary chart-actions__next">
            مریض بعدی: {{ nextVisit.patientName }} <i class="ri-arrow-left-line" aria-hidden="true"></i>
          </RouterLink>
          <RouterLink to="/doctor" class="btn btn-soft">بازگشت به صف</RouterLink>
        </template>
      </div>
    </form>

    <UiModal v-if="panel === 'amend'" title="تاریخچه تغییرات" size="lg" @close="panel = null">
        <template v-if="chart.amendments.length">
          <div class="xr-table-wrap">
            <table class="xr-table">
              <thead><tr><th>فیلد</th><th>قبلی</th><th>جدید</th><th>دلیل</th><th>کاربر</th><th>زمان</th></tr></thead>
              <tbody>
                <tr v-for="(a, i) in chart.amendments" :key="i">
                  <td>{{ a.field === 'FinalizedAt' ? 'ادامه معاینه' : historyFields[a.field] ?? a.field }}</td>
                  <td class="pre-line" dir="auto">{{ shownValue(a.field, a.oldValue) }}</td>
                  <td class="pre-line" dir="auto">{{ shownValue(a.field, a.newValue) }}</td>
                  <td>{{ a.reason }}</td>
                  <td>{{ a.byUser ?? '—' }}</td>
                  <td>{{ formatDateTime(a.createdAt) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
    </UiModal>

      <UiModal v-if="panel === 'lab'" title="درخواست تشخیصی" size="xl" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <h3 v-if="chart.diagnosticOrders.length" class="module-panel__list-title">درخواست‌های ثبت‌شده</h3>
        <div v-if="chart.diagnosticOrders.length" class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>درخواست</th><th>وضعیت</th><th>پرداخت</th><th>نتیجه</th><th>زمان نتیجه</th><th></th></tr></thead>
            <tbody>
              <tr v-for="o in chart.diagnosticOrders" :key="o.id">
                <td><span class="ltr-num">{{ o.orderNumber }}</span></td>
                <td>{{ o.testName }}</td>
                <td><span class="xr-badge" :class="`xr-badge--${diagnosticStatusTones[o.status]}`">{{ diagnosticStatusLabels[o.status] }}</span></td>
                <td>{{ o.paid === null ? '—' : o.paid ? 'پرداخت‌شده' : 'در انتظار پرداخت' }}</td>
                <td>{{ o.resultText ?? '—' }}</td>
                <td>{{ formatDateTime(o.performedAt) }}</td>
                <td class="text-nowrap">
                  <button type="button" class="btn btn-soft btn-sm" @click="attachmentsFor = attachmentsFor === o.id ? null : o.id">فایل‌ها</button>
                  <button v-if="['Requested', 'InProgress'].includes(o.status) && auth.can('diagnostics.create')" type="button" class="btn btn-soft btn-sm ms-2" @click="Object.assign(noteEdit, { order: o, note: o.note ?? '', saving: false, error: '' })">ویرایش توضیح</button>
                  <button v-if="['Requested', 'InProgress'].includes(o.status) && auth.can('diagnostics.cancel')" type="button" class="btn btn-soft-danger btn-sm ms-2" @click="cancelOrder(o)">لغو</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else-if="!orderFormOpen" class="xr-state">درخواستی ثبت نشده است.</p>
        <AttachmentPanel v-if="attachmentsFor" :key="attachmentsFor" owner-type="DiagnosticOrder" :owner-id="attachmentsFor" :can-upload="false" />
        <button v-if="auth.can('diagnostics.create') && !orderFormOpen" type="button" class="btn btn-primary module-panel__create" @click="orderFormOpen = true"><i class="ri-add-line" aria-hidden="true"></i> درخواست تشخیصی جدید</button>
        <form v-if="orderFormOpen" class="row g-3 align-items-end module-panel__form" @submit.prevent="submitOrder">
          <div class="col-md-4">
            <UiComboBox v-model="order.testId" label="درخواست" :options="tests" :loading="testsLoading" required chip />
          </div>
          <div class="col-md-3">
            <label class="form-label" for="o-note">توضیح</label>
            <input id="o-note" v-model="order.note" class="form-control" maxlength="500" />
          </div>
          <div class="col-md-3">
            <label class="form-label" for="o-discount">تخفیف</label>
            <div class="input-group">
              <input id="o-discount" v-model.number="order.discount" class="form-control" type="number" min="0" step="0.01" :max="order.discountMode === 'Percent' ? 100 : orderPrice" :disabled="!orderPrice" />
              <select v-model="order.discountMode" class="form-select" style="max-width: 82px" :disabled="!orderPrice"><option value="Amount">AFN</option><option value="Percent">%</option></select>
            </div>
            <small v-if="orderPrice" class="form-text">تعرفه {{ money(orderPrice) }} · قابل پرداخت {{ money(orderFinal) }}</small>
          </div>
          <div class="col-md-2"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="order.saving">درخواست</button></div>
          <div v-if="orderKind === 'Imaging'" class="col-12">
            <div class="form-check">
              <input id="o-return" v-model="order.returnToDoctor" class="form-check-input" type="checkbox" />
              <label class="form-check-label" for="o-return">بعد از عکس‌برداری مریض به صف داکتر برگردد</label>
            </div>
          </div>
        </form>
      </div></UiModal>

      <UiModal v-if="panel === 'surgery'" title="ارجاع به عملیات" size="xl" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <h3 v-if="surgeries.length" class="module-panel__list-title">پرونده‌های عملیات این مراجعه</h3>
        <div v-if="surgeries.length" class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>شماره</th><th>نوع عملیات</th><th>چشم</th><th>وضعیت</th></tr></thead>
            <tbody>
              <tr v-for="s in surgeries" :key="s.id">
                <td>
                  <RouterLink v-if="auth.can('surgery.edit')" :to="`/surgery/${s.id}`"><span class="ltr-num">{{ s.caseNumber }}</span></RouterLink>
                  <span v-else class="ltr-num">{{ s.caseNumber }}</span>
                </td>
                <td>{{ s.surgeryType }}</td>
                <td>{{ eyeLabels[s.eye] }}</td>
                <td><span class="xr-badge xr-badge--primary">{{ surgeryStatusLabels[s.status] }}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else-if="!surgeryFormOpen" class="xr-state">پرونده عملیاتی ثبت نشده است.</p>
        <button v-if="auth.can('surgery.create') && !surgeryFormOpen" type="button" class="btn btn-primary module-panel__create" @click="surgeryFormOpen = true"><i class="ri-add-line" aria-hidden="true"></i> ارجاع جدید به عملیات</button>
        <form v-if="surgeryFormOpen" class="row g-3 align-items-end module-panel__form" @submit.prevent="submitSurgery">
          <div class="col-md-3">
            <UiComboBox v-model="surgery.operationDefinitionId" label="عملیات" :options="operationOptions" required chip />
          </div>
          <div class="col-md-2">
            <label class="form-label" for="s-eye">چشم</label>
            <select id="s-eye" v-model="surgery.eye" class="form-select">
              <option v-for="(label, value) in eyeLabels" :key="value" :value="value">{{ label }}</option>
            </select>
          </div>
          <div class="col-md-2">
            <label class="form-label" for="s-amount">مبلغ عملیات</label>
            <input id="s-amount" v-model.number="surgery.amount" class="form-control" type="number" min="0.01" step="0.01" :placeholder="surgeryBasePrice ? String(surgeryBasePrice) : ''" />
          </div>
          <div class="col-md-3">
            <label class="form-label" for="s-discount">تخفیف</label>
            <div class="input-group">
              <input id="s-discount" v-model.number="surgery.discount" class="form-control" type="number" min="0" step="0.01" :max="surgery.discountMode === 'Percent' ? 100 : surgeryBasePrice" />
              <select v-model="surgery.discountMode" class="form-select" style="max-width: 82px"><option value="Amount">AFN</option><option value="Percent">%</option></select>
            </div>
          </div>
          <div class="col-md-2"><button type="submit" class="btn btn-primary w-100 xr-control-button" :disabled="surgery.saving">ارجاع به عملیات</button></div>
          <div class="col-12">
            <p class="form-hint mb-0">تعرفه {{ money(selectedOperation?.price ?? 0) }} · مبلغ نهایی {{ money(surgeryFinal) }}. موجودی پکیج پس از تسویهٔ کامل فیس کسر می‌شود.</p>
          </div>
        </form>
      </div></UiModal>

      <UiModal v-if="panel === 'history'" title="سابقه مراجعات" size="lg" @close="panel = null">
        <div class="module-panel">
          <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
          <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
        <p v-if="history.length === 0" class="xr-state">سابقه‌ای پیدا نشد.</p>
        <div v-else class="xr-table-wrap">
          <table class="xr-table">
            <thead><tr><th>تاریخ</th><th>بخش</th><th>داکتر</th><th>دید OD / OS</th><th>فشار OD / OS</th><th>تشخیص</th><th>پلان</th><th></th></tr></thead>
            <tbody>
              <tr v-for="h in history" :key="h.visitId">
                <td>{{ formatDate(h.visitDate) }}</td>
                <td>{{ h.departmentName }}</td>
                <td>{{ h.doctorName ?? '—' }}</td>
                <td dir="ltr" class="text-nowrap">{{ h.visualAcuityOd || '—' }} / {{ h.visualAcuityOs || '—' }}</td>
                <td dir="ltr" class="text-nowrap">{{ h.iopOd || '—' }} / {{ h.iopOs || '—' }}</td>
                <td>{{ h.diagnoses.map((d) => d.text).join('، ') || '—' }}</td>
                <td>{{ parsePlan(h.treatmentPlan).plan || '—' }}</td>
                <td><RouterLink :to="`/chart/${h.visitId}`" class="btn btn-soft btn-sm text-nowrap">باز کردن</RouterLink></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div></UiModal>
  </div>


  <UiModal v-if="reapprove.rx" :title="`تأیید دوباره نسخه ${reapprove.rx.rxNumber}`" @close="reapprove.rx = null">
    <form @submit.prevent="submitReapprove">
      <div class="mb-3"><ShamsiDateInput v-model="reapprove.validUntil" label="اعتبار تازه تا (اختیاری)" :min="todayIso()" /></div>
      <label class="form-label" for="reapprove-note">توضیح داکتر</label>
      <input id="reapprove-note" v-model="reapprove.note" class="form-control mb-3" minlength="3" maxlength="300" required />
      <div v-if="reapprove.error" class="alert alert-danger" role="alert">{{ reapprove.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="reapprove.saving">تأیید</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>

  <UiModal v-if="noteEdit.order" :title="`توضیح ${noteEdit.order.testName}`" @close="noteEdit.order = null">
    <form @submit.prevent="saveOrderNote">
      <p class="form-hint">خود آزمایش تغییر نمی‌کند، چون به همان نام هزینه و ارجاع شده است. اگر اشتباه است، درخواست را لغو و دوباره ثبت کنید.</p>
      <label class="form-label" for="note-edit">توضیح</label>
      <input id="note-edit" v-model="noteEdit.note" class="form-control mb-3" maxlength="500" />
      <div v-if="noteEdit.error" class="alert alert-danger" role="alert">{{ noteEdit.error }}</div>
      <div class="xr-form-actions">
        <button type="submit" class="btn btn-primary" :disabled="noteEdit.saving">ذخیره</button>
        <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
      </div>
    </form>
  </UiModal>
</template>

<style scoped>
.refraction-result-preview { overflow-x: auto; }
.refraction-result-preview :deep(.refraction-prescription) { min-width: 900px; }
.chart-shell {
  display: grid;
  gap: 12px;
  min-width: 0;
}

/* Orders and the jump links stay in reach for the whole length of the dossier. */
.chart-header {
  position: sticky;
  top: var(--xr-sticky-top);
  z-index: 15;
  display: grid;
  gap: 8px;
  min-width: 0;
  margin: -6px -6px 0;
  padding: 6px;
  border-radius: 10px;
  background: var(--xr-page-bg);
}

@media (max-width: 991.98px) {
  .chart-header {
    position: static;
  }
}

.chart-header :deep(.banner),
.chart-header :deep(.banner__facts) {
  min-width: 0;
}

/* Small report-style action items keep the dossier header calm and space-efficient. */
.chart-orders {
  display: flex;
  align-items: center;
  gap: 8px;
}

.chart-orders__group {
  display: flex;
  flex: 1;
  gap: 6px;
  min-width: 0;
}

.chart-orders__item {
  display: flex;
  align-items: center;
  flex: 1 1 0;
  gap: 7px;
  min-width: 0;
  min-height: 44px;
  padding: 5px 9px;
  border: 1px solid var(--xr-border);
  border-radius: 9px;
  background: var(--xr-surface, #fff);
  color: var(--xr-text);
  text-align: start;
  transition: border-color .18s ease, background .18s ease, box-shadow .18s ease;
}

.chart-orders__item:hover,
.chart-orders__item:focus-visible {
  border-color: color-mix(in srgb, var(--action-accent) 48%, var(--xr-border));
  background: color-mix(in srgb, var(--action-accent) 6%, #fff);
  box-shadow: 0 3px 10px color-mix(in srgb, var(--action-accent) 12%, transparent);
  outline: none;
}

.chart-orders__item--optical { --action-accent: #2676a8; }
.chart-orders__item--lab { --action-accent: #258b79; }
.chart-orders__item--surgery { --action-accent: #8a5aaa; }

.chart-orders__icon {
  display: grid;
  flex: 0 0 29px;
  place-items: center;
  width: 29px;
  height: 29px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--action-accent) 12%, #fff);
  color: var(--action-accent);
  font-size: 16px;
}

.chart-orders__copy {
  flex: 1;
  min-width: 0;
}

.chart-orders__copy strong {
  display: block;
  overflow: hidden;
  font-size: 12.5px;
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chart-orders__badges {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 3px;
}

.chart-orders__arrow {
  flex: 0 0 auto;
  color: var(--xr-text-muted);
  font-size: 13px;
}

.chart-orders__end {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-inline-start: auto;
}

.chart-header__count {
  min-width: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: var(--xr-primary);
  color: #fff;
  font-size: 10.5px;
  line-height: 18px;
  text-align: center;
}

.chart-header__count--ready {
  background: var(--xr-status-orange);
}

.chart-brief {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(320px, 100%), 1fr));
  gap: 8px 20px;
  padding: 10px 14px;
}

.chart-brief__block {
  display: grid;
  gap: 6px;
  align-content: start;
  min-width: 0;
}

.chart-brief__title {
  margin: 0;
  font-size: 14px;
  font-weight: 650;
}

.chart-brief__line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.chart-brief__text {
  margin: 0;
  font-size: 13px;
  white-space: pre-line;
}

.chart-brief__compare {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
  text-align: center;
}

.chart-brief__compare th,
.chart-brief__compare td {
  padding: 3px 6px;
  border: 1px solid var(--xr-border);
}

.chart-brief__compare thead th {
  font-weight: 600;
}

.results__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.results__tabs {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 2px;
  padding: 2px;
  border: 1px solid var(--xr-switch-border);
  border-radius: 8px;
  background: var(--xr-switch-bg);
}

.results__tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 12px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--xr-text-soft);
  font-size: 13px;
}

.results__tab--active {
  background: var(--xr-switch-on);
  box-shadow: var(--xr-switch-on-shadow);
  color: #fff;
  font-weight: 600;
}

.results__dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
}

.results__dot--ready { background: var(--xr-status-green); }
.results__dot--wait { background: var(--xr-status-orange); }
.results__tab--active .results__dot { box-shadow: 0 0 0 2px #fff; }

.results__item {
  display: grid;
  gap: 6px;
  padding: 8px 0;
  border-bottom: 1px solid var(--xr-row-border);
}

.results__item:last-child {
  border-bottom: 0;
}

.results__meta {
  display: flex;
  gap: 16px;
  margin: 6px 0 0;
  color: var(--xr-text-soft);
  font-size: 12.5px;
}

.results__empty {
  margin: 4px 0;
  color: var(--xr-text-muted);
  font-size: 13px;
}

.chart-actions__keep {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  font-size: 13px;
}

.chart-actions__next {
  margin-inline-start: auto;
}

.chart-dossier {
  display: grid;
  gap: 12px;
  min-width: 0;
}

.chart-actions {
  position: sticky;
  bottom: 0;
  max-width: 980px;
  width: 100%;
  margin: 0 auto;
  border: 1px solid var(--xr-border);
  border-radius: var(--xr-radius-card);
  box-shadow: 0 -4px 14px rgb(0 0 0 / 0.06);
}

.rx-meta {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}

.rx-state {
  color: var(--xr-text-muted);
  font-size: 12.5px;
}

.rx-state--warn {
  color: var(--xr-tone-orange-text);
}

/* Quiet text actions, so the prescription reads as one page and not a row of coloured buttons. */
.rx-act {
  padding: 2px 6px;
  border: 0;
  border-radius: 4px;
  background: none;
  color: var(--xr-text-soft);
  font-size: 12.5px;
  text-decoration: none;
}

.rx-act:hover {
  background: var(--xr-row-hover);
  color: var(--xr-text);
}

.rx-act--danger:hover {
  color: var(--xr-red, #c62828);
}

.issued__acts {
  display: inline-flex;
  gap: 2px;
  margin-inline-start: auto;
}

.rx-others {
  margin-top: 10px;
  padding: 8px 10px;
  border: 1px dashed var(--xr-border);
  border-radius: 6px;
  font-size: 13px;
}

/* The form to fill comes first in each window; what is already recorded follows under it. */
.module-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.module-panel > .alert {
  order: -2;
  margin: 0;
}

.module-panel__form,
.module-panel__create {
  order: -1;
}

.module-panel__form {
  padding-bottom: 14px;
  border-bottom: 1px solid var(--xr-border);
}

.module-panel__list-title {
  margin: 0;
  color: var(--xr-text-muted);
  font-size: 13px;
  font-weight: 600;
}

.module-panel__create {
  display: inline-flex;
  align-self: flex-start;
  align-items: center;
  gap: 6px;
}

.chart-placeholder {
  display: grid;
  gap: 14px;
  padding: 22px 20px;
}

.chart-placeholder--sheet {
  max-width: 980px;
  width: 100%;
  min-height: 60vh;
  margin: 0 auto;
}

.summary__document-close {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 12px;
}

.issued {
  margin-bottom: 10px;
  color: var(--xr-text);
}

.issued__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.pre-line {
  white-space: pre-line;
}

/* Keeps an inline action level with the 45px fields beside it. */
.xr-control-button,
.xr-control-icon {
  height: var(--xr-control-height);
}

.xr-control-icon {
  width: var(--xr-control-height);
}

@media (max-width: 767.98px) {
  .chart-shell { gap: 8px; }
  .chart-header :deep(.banner) {
    gap: 8px;
    padding: 10px 12px;
  }
  .chart-header :deep(.banner__name) { font-size: 16px; }
  .chart-header :deep(.banner__facts) {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 6px 12px;
  }
  .chart-orders { flex-wrap: wrap; gap: 6px; align-items: stretch; }
  .chart-orders__group { flex-basis: 100%; flex-wrap: wrap; gap: 4px; }
  .chart-orders__item { flex: 1 1 150px; }
  .chart-orders__item { min-height: 42px; padding-inline: 7px; }
  .chart-orders__icon { flex-basis: 27px; width: 27px; height: 27px; }
  .chart-orders__copy strong { font-size: 11.5px; }
  .chart-orders__arrow { display: none; }
  .chart-orders__end { margin-inline-start: auto; }
}
</style>
