import { ref } from 'vue'
import { usePagedList } from '@/api/paged'
import type { PaymentRow } from './types'

/** Search and pagination run on the server, including older receipts. */
export function createReceiptList(url: () => string, _canViewInvoices: () => boolean, pageSize = 25) {
  const search = ref('')
  let appliedSearch = ''
  const list = usePagedList<PaymentRow>(() => {
    const base = url()
    return `${base}${base.includes('?') ? '&' : '?'}q=${encodeURIComponent(appliedSearch)}`
  }, pageSize)
  function searchNow() { appliedSearch = search.value.trim(); return list.first() }
  return { ...list, search, searchNow, dispose() {} }
}
