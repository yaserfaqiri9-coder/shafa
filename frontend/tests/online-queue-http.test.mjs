import { readFile } from 'node:fs/promises'
import assert from 'node:assert/strict'
import test from 'node:test'
import ts from 'typescript'

// Exercise the actual HTTP client without starting a browser or touching a server.
const source = await readFile(new URL('../src/api/http.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
}).outputText
const { api, ApiError } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)

test('202 queued intent never resolves as a successful business save', async () => {
  const originalFetch = globalThis.fetch
  const originalDocument = globalThis.document
  globalThis.document = { cookie: 'XSRF-TOKEN=test-token' }
  globalThis.fetch = async (_url, options) => {
    assert.equal(options.headers['Idempotency-Key'], 'stable-form-key')
    return new Response(JSON.stringify({ commandId: 'queued-id' }), {
      status: 202, headers: { Location: '/api/sync/commands/queued-id', 'Content-Type': 'application/json' },
    })
  }
  try {
    await assert.rejects(api('/payments', { method: 'POST', body: { amount: 60 }, idempotencyKey: 'stable-form-key' }),
      error => error instanceof ApiError && error.code === 'sync_pending'
        && error.outcomeUnknown && error.problem.commandId === 'queued-id')
  } finally { globalThis.fetch = originalFetch; globalThis.document = originalDocument }
})

test('locally confirmed replay returns the actual original business result', async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => new Response('{"id":42,"amount":60}', {
    status: 201, headers: { 'Content-Type': 'application/json' },
  })
  try { assert.deepEqual(await api('/sync/confirmed-result'), { id: 42, amount: 60 }) }
  finally { globalThis.fetch = originalFetch }
})
