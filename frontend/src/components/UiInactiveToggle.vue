<script lang="ts">
import { computed, ref, type Ref } from 'vue'

/** Rows of a list without its inactive ones, unless the user asks to see them. */
export function useInactiveFilter<T extends { isActive: boolean }>(rows: Ref<T[]>) {
  const showInactive = ref(false)
  const shown = computed(() => (showInactive.value ? rows.value : rows.value.filter((row) => row.isActive)))
  const inactiveCount = computed(() => rows.value.filter((row) => !row.isActive).length)
  return { showInactive, shown, inactiveCount }
}
</script>

<script setup lang="ts">
/**
 * Inactive and deactivated records stay in the database for history, but out of everyday lists.
 * A list shows them only when someone asks, through this one checkbox.
 */
defineProps<{ count: number }>()
const model = defineModel<boolean>({ required: true })
</script>

<template>
  <label v-if="count > 0" class="form-check mb-0 text-nowrap inactive-toggle">
    <input v-model="model" type="checkbox" class="form-check-input" />
    <span class="form-check-label">نمایش غیرفعال‌ها ({{ count }})</span>
  </label>
</template>

<style scoped>
.inactive-toggle {
  font-size: 0.875rem;
}
</style>
