<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from 'vue'
import { api } from '@/api/http'
import { formatDateTime } from '@/utils/date'

interface Command {
  id: string
  path: string
  statusCode: number
  createdAt: string
  responseBody: string
}
const commands = ref<Command[]>([])
let timer: ReturnType<typeof setInterval> | undefined
async function refresh() {
  try { commands.value = await api<Command[]>('/sync/commands/') } catch { /* Retain the last known status. */ }
}
function detail(command: Command) {
  if (command.statusCode < 400) return ''
  try {
    const body = JSON.parse(command.responseBody) as { title?: string }
    return body.title ?? 'درخواست تأیید نشد؛ اطلاعات تازه را بررسی کنید.'
  } catch { return 'درخواست تأیید نشد.' }
}
onMounted(() => { void refresh(); timer = setInterval(refresh, 15_000) })
onBeforeUnmount(() => clearInterval(timer))
</script>

<template>
  <details class="card mb-3" @toggle="refresh">
    <summary class="p-3">درخواست‌های آنلاین — {{ commands.filter(c => c.statusCode === 0).length }} در انتظار تأیید محلی</summary>
    <div class="p-3" aria-live="polite">
      <p v-if="!commands.length">درخواستی ثبت نشده است.</p>
      <div v-for="command in commands" :key="command.id" class="border-bottom py-2">
        <span>{{ formatDateTime(command.createdAt) }}</span>
        <code class="mx-2" dir="ltr">{{ command.path }}</code>
        <strong>{{ command.statusCode === 0 ? 'در صف؛ قطعی نشده' : command.statusCode < 400 ? 'تأیید و اجرا شد' : 'رد شد؛ اجرا نشد' }}</strong>
        <p v-if="detail(command)" class="text-danger mb-0">{{ detail(command) }}</p>
        <small dir="ltr">{{ command.id }}</small>
      </div>
      <button class="btn btn-sm btn-outline-primary mt-2" type="button" @click="refresh">تازه‌سازی نتیجه</button>
    </div>
  </details>
</template>
