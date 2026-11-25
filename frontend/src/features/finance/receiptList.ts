import { computed, ref } from 'vue'
import { api, ApiError } from '@/api/http'
import { fetchAll } from '@/api/paged'
import type { InvoiceDetail, InvoiceRow, PaymentRow } from './types'

/** Search covers every receipt in the selected period; names use the existing invoice permission. */
export function createReceiptList(url: () => string, canViewInvoices: () => boolean, pageSize = 25) {
  const rows = ref<PaymentRow[]>([])
  const total = ref(0)
  const page = ref(1)
  const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))
  const search = ref('')
  const loading = ref(false)
  let appliedSearch = ''
  let revision = 0
  let controller: AbortController | null = null
  let cachedSearch: { key: string; rows: PaymentRow[] } | null = null

  async function addNames(items: PaymentRow[], signal: AbortSignal) {
    const names = new Map<number, string | null>()
    const ids = [...new Set(items.filter((row) => row.patientName === undefined).map((row) => row.invoiceId))]
    if (canViewInvoices()) {
      for (let offset = 0; offset < ids.length; offset += 4) {
        await Promise.all(ids.slice(offset, offset + 4).map(async (id) => {
          try {
            const detail = await api<InvoiceDetail>(`/invoices/${id}`, { signal })
            names.set(id, detail.invoice.patientName)
          } catch (cause) {
            if (cause instanceof ApiError && (cause.status === 403 || cause.status === 404)) names.set(id, '—')
            else throw cause
          }
        }))
      }
    }
    return items.map((row) => ({ ...row, patientName: row.patientName !== undefined ? row.patientName : canViewInvoices() ? names.get(row.invoiceId) ?? null : '—' }))
  }

  async function load(useCache = false) {
    const current = ++revision
    controller?.abort()
    controller = new AbortController()
    const { signal } = controller
    const base = url()
    loading.value = true
    try {
      let items: PaymentRow[]
      let count: number
      if (appliedSearch) {
        const key = JSON.stringify([base, appliedSearch])
        if (!useCache || cachedSearch?.key !== key) {
          const [payments, invoices] = await Promise.all([
            fetchAll<PaymentRow>(base, signal),
            canViewInvoices() ? fetchAll<InvoiceRow>(`/invoices?q=${encodeURIComponent(appliedSearch)}`, signal) : Promise.resolve([]),
          ])
          const ids = new Set(invoices.map((invoice) => invoice.id))
          const needle = appliedSearch.toLocaleLowerCase()
          const matching = payments.filter((row) => ids.has(row.invoiceId)
            || row.receiptNumber.toLocaleLowerCase().includes(needle)
            || row.invoiceNumber.toLocaleLowerCase().includes(needle)
            || row.patientName?.toLocaleLowerCase().includes(needle))
          if (current !== revision) return
          cachedSearch = { key, rows: matching }
        }
        const matching = cachedSearch!.rows
        count = matching.length
        const selectedPage = Math.min(page.value, Math.max(1, Math.ceil(count / pageSize)))
        items = matching.slice((selectedPage - 1) * pageSize, selectedPage * pageSize)
        if (current === revision) page.value = selectedPage
      } else {
        cachedSearch = null
        const result = await api<{ items: PaymentRow[]; total: number }>(`${base}${base.includes('?') ? '&' : '?'}page=${page.value}&pageSize=${pageSize}`, { signal })
        items = result.items
        count = result.total
        if (items.length === 0 && page.value > 1 && count > 0) {
          page.value = Math.max(1, Math.ceil(count / pageSize))
          return load()
        }
      }
      const named = await addNames(items, signal)
      if (current !== revision) return
      rows.value = named
      total.value = count
    } catch (cause) {
      if (current !== revision || signal.aborted) return
      throw cause
    } finally {
      if (current === revision) loading.value = false
    }
  }

  function first() { page.value = 1; return load() }
  function goTo(value: number) { page.value = value; return load(true) }
  function searchNow() { appliedSearch = search.value.trim(); return first() }
  function dispose() { revision++; controller?.abort() }
  return { rows, total, page, pageCount, search, loading, load, first, goTo, searchNow, dispose }
}
