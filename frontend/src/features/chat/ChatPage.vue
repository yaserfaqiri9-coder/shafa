<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useLiveRefresh } from '@/api/realtime'
import { api, ApiError, newIdempotencyKey } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import UiCard from '@/components/UiCard.vue'

interface ChatUser {
  id: number
  fullName: string
  userName: string
}

interface ChatMessage {
  id: number
  senderUserId: number
  recipientUserId: number | null
  senderName: string
  isPublic: boolean
  content: string
  createdAt: string
}

const users = ref<ChatUser[]>([])
const auth = useAuthStore()
const publicRoom: ChatUser = { id: 0, fullName: 'گفتگوی عمومی', userName: 'همه کاربران' }
const selected = ref<ChatUser | null>(null)
const messages = ref<ChatMessage[]>([])
const search = ref('')
const draft = ref('')
const loadingUsers = ref(false)
const loadingMessages = ref(false)
const sending = ref(false)
const error = ref('')
const pendingSync = ref(false)
const pendingContent = ref('')
const conversation = ref<HTMLElement | null>(null)
const canSend = computed(() => !!selected.value && draft.value.trim().length > 0 && draft.value.length <= 2000 && !sending.value)
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchVersion = 0

async function loadUsers() {
  const version = ++searchVersion
  loadingUsers.value = true
  try {
    const query = search.value.trim()
    const result = await api<ChatUser[]>(`/chat/users${query ? `?search=${encodeURIComponent(query)}` : ''}`)
    if (version === searchVersion) users.value = result
  } catch {
    if (version === searchVersion) error.value = 'دریافت فهرست کاربران انجام نشد.'
  } finally {
    if (version === searchVersion) loadingUsers.value = false
  }
}

async function loadMessages() {
  if (!selected.value) return
  const user = selected.value
  const isPublic = user.id === 0
  loadingMessages.value = true
  try {
    const result = await api<ChatMessage[]>(isPublic ? '/chat/public/messages' : `/chat/${user.id}/messages`)
    if (selected.value?.id !== user.id) return
    messages.value = result
    if (pendingContent.value && messages.value.some(message => message.content === pendingContent.value
      && message.senderUserId === auth.user?.id && message.isPublic === isPublic)) {
      pendingContent.value = ''
      pendingSync.value = false
    }
    await nextTick()
    conversation.value?.scrollTo({ top: conversation.value.scrollHeight })
  } catch {
    if (selected.value?.id === user.id) error.value = 'دریافت پیام‌ها انجام نشد. دوباره تلاش کنید.'
  } finally {
    if (selected.value?.id === user.id) loadingMessages.value = false
  }
}

watch(search, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(() => void loadUsers(), 250)
})

watch(() => selected.value?.id, () => {
  messages.value = []
  error.value = ''
  pendingSync.value = false
  pendingContent.value = ''
  void loadMessages()
})

useLiveRefresh(['chatMessage'], () => loadMessages(), 5000, (_type, ids) =>
  !!selected.value && (selected.value.id === 0
    ? ids.isPublic === true
    : ids.isPublic !== true && (Number(ids.senderUserId) === selected.value.id || Number(ids.recipientUserId) === selected.value.id)),
)

async function selectUser(user: ChatUser) {
  selected.value = user
}

async function sendMessage() {
  if (!canSend.value || !selected.value) return
  sending.value = true
  error.value = ''
  const content = draft.value.trim()
  const selectedUser = selected.value
  const isPublic = selectedUser.id === 0
  const clientMessageId = activeMessageId.value ?? newIdempotencyKey()
  activeMessageId.value = clientMessageId
  try {
    const result = await api<ChatMessage>(isPublic ? '/chat/public/messages' : '/chat/messages', {
      method: 'POST',
      idempotencyKey: clientMessageId,
      body: isPublic
        ? { clientMessageId, content }
        : { recipientUserId: selectedUser.id, clientMessageId, content },
    })
    draft.value = ''
    activeMessageId.value = null
    if (result?.id) {
      messages.value = [...messages.value, result]
      await nextTick()
      conversation.value?.scrollTo({ top: conversation.value.scrollHeight, behavior: 'smooth' })
    } else {
      // Online-copy messages appear after the hospital server validates and syncs them.
      pendingContent.value = content
      pendingSync.value = true
    }
    await loadMessages()
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'sync_pending') {
      draft.value = ''
      pendingContent.value = content
      activeMessageId.value = null
      pendingSync.value = true
    } else if (cause instanceof ApiError && cause.outcomeUnknown) {
      error.value = 'نتیجه ارسال روشن نیست. برای جلوگیری از پیام تکراری، با همین متن دوباره ارسال کنید.'
    } else {
      activeMessageId.value = null
      error.value = 'ارسال پیام انجام نشد. اتصال شبکه را بررسی کنید.'
    }
  } finally {
    sending.value = false
  }
}

const activeMessageId = ref<string | null>(null)

function timeOf(value: string) {
  return new Intl.DateTimeFormat('fa-AF', { hour: '2-digit', minute: '2-digit', hour12: true }).format(new Date(value))
}

function selectPublicRoom() {
  selected.value = publicRoom
}

onMounted(() => void loadUsers())
onBeforeUnmount(() => clearTimeout(searchTimer))
</script>

<template>
  <div class="chat-page">
    <header class="page-heading">
      <div>
        <h1>گفتگو با همکاران</h1>
        <p>پیام خصوصی برای کاربران فعال سیستم بفرستید.</p>
      </div>
    </header>

    <div v-if="error" class="alert" role="status">{{ error }}</div>
    <div v-if="pendingSync" class="alert alert-info" role="status">پیام در صف تأیید سرور محلی است و پس از همگام‌سازی نمایش داده می‌شود.</div>

    <div class="chat-layout">
      <UiCard title="کاربران">
        <button class="public-room" :class="{ 'public-room--active': selected?.id === 0 }" type="button" :disabled="activeMessageId !== null" @click="selectPublicRoom">
          <i class="ri-earth-line" aria-hidden="true"></i><span><strong>گفتگوی عمومی</strong><small>پیام برای همهٔ کاربران</small></span>
        </button>
        <label class="search-label" for="chat-search">جستجو</label>
        <input id="chat-search" v-model="search" class="form-control" type="search" placeholder="نام یا نام کاربری" autocomplete="off" />
        <p v-if="loadingUsers" class="muted">در حال دریافت فهرست…</p>
        <p v-else-if="users.length === 0" class="muted">کاربری پیدا نشد.</p>
        <ul v-else class="user-list">
          <li v-for="user in users" :key="user.id">
            <button class="user-option" :class="{ 'user-option--active': selected?.id === user.id }" type="button" :disabled="activeMessageId !== null" @click="selectUser(user)">
              <span class="avatar" aria-hidden="true">{{ user.fullName.slice(0, 1) }}</span>
              <span class="user-copy"><strong>{{ user.fullName }}</strong><small>{{ user.userName }}</small></span>
              <i class="ri-arrow-left-s-line" aria-hidden="true"></i>
            </button>
          </li>
        </ul>
      </UiCard>

      <UiCard :title="selected?.fullName ?? 'یک همکار را انتخاب کنید'">
        <template v-if="selected">
          <div ref="conversation" class="conversation" aria-live="polite" aria-label="پیام‌های گفتگو">
            <p v-if="loadingMessages && messages.length === 0" class="muted conversation-hint">در حال دریافت پیام‌ها…</p>
            <p v-else-if="messages.length === 0" class="muted conversation-hint">هنوز پیامی در این گفتگو نیست. پیام نخست را بفرستید.</p>
            <article v-for="message in messages" :key="message.id" class="message" :class="{ 'message--mine': message.senderUserId === auth.user?.id }">
              <strong v-if="message.isPublic && message.senderUserId !== auth.user?.id" class="message__sender">{{ message.senderName }}</strong>
              <p>{{ message.content }}</p>
              <time :datetime="message.createdAt">{{ timeOf(message.createdAt) }}</time>
            </article>
          </div>
          <form class="composer" @submit.prevent="sendMessage">
            <textarea v-model="draft" class="form-control" rows="2" maxlength="2000" :disabled="activeMessageId !== null" aria-label="متن پیام" placeholder="پیام خود را بنویسید…" @keydown.enter.exact.prevent="sendMessage"></textarea>
            <div class="composer__footer">
              <small>{{ draft.length }} / ۲۰۰۰</small>
              <button class="btn btn-primary" type="submit" :disabled="!canSend">
                <i class="ri-send-plane-fill" aria-hidden="true"></i>{{ sending ? 'در حال ارسال…' : 'ارسال پیام' }}
              </button>
            </div>
          </form>
        </template>
        <div v-else class="empty-conversation">
          <i class="ri-chat-smile-3-line" aria-hidden="true"></i>
          <p>برای شروع گفتگو، یک کاربر را از فهرست انتخاب کنید.</p>
        </div>
      </UiCard>
    </div>
  </div>
</template>

<style scoped>
.chat-page { display: grid; gap: 20px; padding-bottom: 24px; }
.page-heading { display: flex; align-items: center; justify-content: space-between; }
.page-heading p, .muted { color: var(--xr-text-muted); margin: 4px 0 0; }
.chat-layout { display: grid; grid-template-columns: minmax(250px, 0.8fr) minmax(0, 1.8fr); gap: 18px; align-items: start; }
.search-label { display: block; margin-bottom: 6px; font-weight: 600; }
.public-room { width: 100%; display: flex; align-items: center; gap: 10px; padding: 10px 8px; margin-bottom: 12px; border: 1px solid var(--xr-border, #dce3ec); border-radius: 10px; background: transparent; text-align: right; color: inherit; cursor: pointer; }
.public-room:hover, .public-room--active { background: var(--xr-tone-primary-bg); border-color: var(--xr-primary-light); }
.public-room > i { font-size: 24px; color: var(--xr-tone-primary-text); }
.public-room span { display: grid; gap: 2px; }
.public-room small { color: var(--xr-text-muted); }
.user-list { list-style: none; padding: 0; margin: 14px -4px -4px; max-height: min(62vh, 640px); overflow: auto; }
.user-option { width: 100%; display: flex; align-items: center; gap: 10px; padding: 10px 8px; border: 0; border-radius: 10px; background: transparent; text-align: right; color: inherit; cursor: pointer; }
.user-option:hover, .user-option--active { background: var(--xr-row-hover); }
.avatar { display: grid; place-items: center; flex: 0 0 38px; height: 38px; border-radius: 50%; background: var(--xr-tone-primary-bg); color: var(--xr-tone-primary-text); font-weight: 700; }
.user-copy { display: grid; gap: 2px; min-width: 0; flex: 1; }
.user-copy strong, .user-copy small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.user-copy small { color: var(--xr-text-muted); }
.conversation { display: flex; flex-direction: column; gap: 10px; height: min(55vh, 560px); overflow: auto; padding: 12px; background: var(--xr-page-bg, #f7f8fb); border-radius: 12px; }
.conversation-hint, .empty-conversation { margin: auto; text-align: center; }
.message { align-self: flex-start; max-width: min(82%, 680px); padding: 9px 12px 6px; border-radius: 14px 14px 14px 4px; background: white; box-shadow: var(--xr-shadow-card); overflow-wrap: anywhere; }
.message--mine { align-self: flex-end; background: var(--xr-tone-primary-bg); border-radius: 14px 14px 4px 14px; }
.message p { margin: 0; white-space: pre-wrap; }
.message__sender { display: block; margin-bottom: 3px; color: var(--xr-tone-primary-text); font-size: 12px; }
.message time { display: block; margin-top: 3px; color: var(--xr-text-soft); font-size: 11px; text-align: left; }
.composer { display: grid; gap: 8px; margin-top: 12px; }
.composer__footer { display: flex; align-items: center; justify-content: space-between; gap: 12px; color: var(--xr-text-muted); }
.composer__footer .btn { display: inline-flex; align-items: center; gap: 6px; }
.empty-conversation { display: grid; justify-items: center; gap: 8px; min-height: 300px; color: var(--xr-text-muted); }
.empty-conversation i { font-size: 42px; }
.empty-conversation p { margin: 0; }
@media (max-width: 760px) {
  .chat-layout { grid-template-columns: minmax(0, 1fr); }
  .conversation { height: 48vh; }
}
</style>
