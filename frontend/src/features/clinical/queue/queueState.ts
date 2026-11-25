import type { CallDeskHandle } from '@/features/calls/useCallDesk'

/** Where a visit stands at one desk, as the Backend's call queue sees it. null: not callable here (or no call permission). */
export type CallState = 'called' | 'next' | 'absent' | 'waiting' | null

export function callStateOf(desk: CallDeskHandle, visitId: number | null | undefined, tokenId?: number | null): CallState {
  if (!desk.enabled || (!visitId && !tokenId)) return null
  const entry = desk.entry(visitId, tokenId)
  if (!entry) return null
  if (entry.isServing) return 'called'
  if (entry.present === false) return 'absent'
  return desk.rows.value.find((r) => !r.isServing && r.present !== false) === entry ? 'next' : 'waiting'
}

export const callStateLabels: Record<Exclude<CallState, null>, string> = { called: 'صدا زده شد', next: 'نفر بعدی', absent: 'غایب', waiting: 'منتظر' }

/** Server order kept; within it: in the room, next, waiting, then those not here. */
export const kindRank: Record<string, number> = { with: 0, called: 0, next: 1, waiting: 2, blocked: 3, away: 3, absent: 4, open: 0, done: 0 }

export function sortByKind<T, K extends string>(items: { row: T; kind: K }[]) {
  return items.map((item, index) => ({ ...item, index })).sort((a, b) => (kindRank[a.kind] ?? 2) - (kindRank[b.kind] ?? 2) || a.index - b.index)
}
