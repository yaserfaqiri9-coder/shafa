<script setup lang="ts">
import UiModal from './UiModal.vue'
import { pendingConfirm } from './confirm'
</script>

<template>
  <UiModal v-if="pendingConfirm" :title="pendingConfirm.title" @close="pendingConfirm.resolve(false)">
    <p class="ui-confirm__message">{{ pendingConfirm.message }}</p>
    <div class="xr-form-actions">
      <button type="button" class="btn" :class="pendingConfirm.danger ? 'btn-danger' : 'btn-primary'" autofocus @click="pendingConfirm.resolve(true)">{{ pendingConfirm.confirmLabel }}</button>
      <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
    </div>
  </UiModal>
</template>

<style scoped>
.ui-confirm__message { white-space: pre-line; }
</style>
