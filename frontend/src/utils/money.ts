// Numbers are shown in English digits throughout the system, as the hospital asked.
const format = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })
// Stock may be counted to the thousandth (0.001), so an amount keeps three decimals.
const amountFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 })

/** Currency label stays fixed until the finance lead confirms currencies and rounding (WORKFLOWS.md F-1). */
export function money(value: number | null | undefined) {
  if (value === null || value === undefined) return '—'
  // Display only: a zero (or a remainder that rounds to zero) never shows as "-0".
  return `${format.format(Math.abs(value) < 0.005 ? 0 : value)} افغانی`
}

export function quantity(value: number | null | undefined) {
  return value === null || value === undefined ? '—' : amountFormat.format(value)
}
