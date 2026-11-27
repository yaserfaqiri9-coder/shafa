export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly problem: Record<string, unknown> | null = null,
  ) {
    super(message)
  }

  /** True when the request may or may not have been applied on the server. */
  get outcomeUnknown() {
    return this.code === 'outcome_unknown' || this.code === 'sync_pending'
  }
}

export function errorMessage(cause: unknown) {
  return cause instanceof ApiError ? cause.message : 'خطایی رخ داد. دوباره تلاش کنید.'
}

/** One key per form submission attempt; reuse it when retrying so the server applies the action once. */
export function newIdempotencyKey() {
  return crypto.randomUUID().replaceAll('-', '')
}

function readCookie(name: string) {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
  return match ? decodeURIComponent(match[1]) : null
}

async function ensureCsrfToken(signal: AbortSignal) {
  const existing = readCookie('XSRF-TOKEN')
  if (existing) return existing
  await fetch('/api/auth/csrf', { credentials: 'same-origin', signal })
  return readCookie('XSRF-TOKEN') ?? ''
}

let onUnauthorized: () => void = () => {}

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

interface RequestOptions {
  method?: string
  body?: unknown
  signal?: AbortSignal
  idempotencyKey?: string
}

export async function uploadFile<T>(path: string, form: FormData): Promise<T> {
  return withRequestTimeout(undefined, async signal => {
    try {
      const token = await ensureCsrfToken(signal)
      const response = await fetch(`/api${path}`, { method: 'POST', headers: { 'X-XSRF-TOKEN': token }, credentials: 'same-origin', body: form, signal })
      if (!response.ok) throw await toApiError(response)
      return (await response.json()) as T
    } catch (cause) {
      if (cause instanceof ApiError) throw cause
      throw new ApiError(0, 'outcome_unknown', 'ارتباط قطع شد و معلوم نیست فایل ذخیره شده یا نه. پیش از تکرار، فهرست فایل‌ها را بررسی کنید.')
    }
  })
}

async function send(path: string, options: RequestOptions, signal: AbortSignal) {
  const method = options.method ?? 'GET'
  const headers: Record<string, string> = {}
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET') headers['X-XSRF-TOKEN'] = await ensureCsrfToken(signal)
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey

  return fetch(`/api${path}`, {
    method,
    headers,
    credentials: 'same-origin',
    signal,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  })
}

// Covers CSRF acquisition, response headers AND the complete JSON/blob body.
// Clearing a timer as soon as headers arrive leaves stalled bodies waiting forever.
async function withRequestTimeout<T>(source: AbortSignal | undefined, action: (signal: AbortSignal) => Promise<T>) {
  const controller = new AbortController()
  const abort = () => controller.abort()
  if (source?.aborted) controller.abort()
  source?.addEventListener('abort', abort)
  const timer = setTimeout(abort, REQUEST_TIMEOUT_MS)
  try {
    return await action(controller.signal)
  } finally {
    clearTimeout(timer)
    source?.removeEventListener('abort', abort)
  }
}

const REQUEST_TIMEOUT_MS = 120_000

async function toApiError(response: Response) {
  const problem = await response.json().catch(() => null)
  if (response.status === 403) {
    // A refusal from a rule says what to do next, such as handing a patient over or asking for emergency access.
    if (problem?.code && problem.code !== 'forbidden') return new ApiError(403, problem.code, problem.title, problem)
    return new ApiError(403, 'forbidden', 'شما اجازه این عملیات را ندارید.', problem)
  }
  if (response.status === 429) {
    return new ApiError(429, 'rate_limited', 'تعداد تلاش‌ها زیاد است. کمی بعد دوباره تلاش کنید.', problem)
  }
  // Field validation failures have no code and an English framework title.
  if (problem?.errors) {
    return new ApiError(response.status, 'validation', 'اطلاعات فورم کامل یا درست نیست. لطفاً بررسی کنید.', problem)
  }
  return new ApiError(
    response.status,
    problem?.code ?? 'error',
    problem?.code ? problem.title : 'خطایی رخ داد. دوباره تلاش کنید.',
    problem,
  )
}

/** Saves a file the server makes, under the name the server gives it. A refusal reads like any other API error. */
export async function downloadFile(path: string, fallbackName: string) {
  const { response, blob } = await withRequestTimeout(undefined, async signal => {
    try {
      const response = await send(path, {}, signal)
      if (response.status === 401) onUnauthorized()
      if (!response.ok) throw await toApiError(response)
      return { response, blob: await response.blob() }
    } catch (cause) {
      if (cause instanceof ApiError) throw cause
      throw new ApiError(0, 'network', 'اتصال با سرور برقرار نشد. اتصال شبکه را بررسی کنید.')
    }
  })

  const disposition = response.headers.get('Content-Disposition') ?? ''
  const name = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1] ?? /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? fallbackName
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = decodeURIComponent(name)
  document.body.append(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export async function api<T = void>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method ?? 'GET'
  if (method !== 'GET' && !options.idempotencyKey) options = { ...options, idempotencyKey: newIdempotencyKey() }
  return withRequestTimeout(options.signal, async signal => {
    try {
      let response = await send(path, options, signal)

      // An invalid CSRF token is rejected before any work happens, so one retry with a fresh token is safe.
      if (response.status === 400 && method !== 'GET') {
        const error = await toApiError(response.clone())
        if (error.code === 'csrf_invalid') {
          document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/'
          response = await send(path, options, signal)
        }
      }
      if (response.status === 401 && path !== '/auth/login' && path !== '/auth/me') {
        onUnauthorized()
      }

      if (!response.ok) throw await toApiError(response)
      if (response.status === 202 && response.headers.get('Location')?.startsWith('/api/sync/commands/')) {
        const queued = await response.json() as { commandId: string }
        // A queued online intent must never look like a confirmed business save.
        throw new ApiError(202, 'sync_pending',
          'درخواست در صف آنلاین محفوظ است؛ هنوز در شفاخانه تأیید نشده است. نتیجه را در فهرست درخواست‌های آنلاین پیگیری کنید و درخواست را تکرار نکنید.',
          { commandId: queued.commandId })
      }
      if (response.status === 204) return undefined as T
      return (await response.json()) as T
    } catch (cause) {
      if (cause instanceof ApiError) throw cause
      if (options.signal?.aborted && cause instanceof DOMException && cause.name === 'AbortError') throw cause
      if (method === 'GET') {
        throw new ApiError(0, 'network', 'اتصال با سرور برقرار نشد. اتصال شبکه را بررسی کنید.')
      }
      throw new ApiError(
        0,
        'outcome_unknown',
        'ارتباط قطع شد و معلوم نیست این عملیات ثبت شده یا نه. پیش از تکرار، صفحه را تازه کنید و بررسی کنید.',
      )
    }
  })
}
