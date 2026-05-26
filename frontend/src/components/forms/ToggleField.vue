<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <label class="toggle-field">
    <input :checked="modelValue" type="checkbox" @change="onChange" />
    <span class="toggle-field__content">
      <strong>{{ label }}</strong>
      <small v-if="hint">{{ hint }}</small>
    </span>
  </label>
</template>

<script setup lang="ts">
const props = defineProps<{
  modelValue: boolean
  label: string
  hint?: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

function onChange(event: Event) {
  emit('update:modelValue', (event.target as HTMLInputElement).checked)
}
</script>

<style scoped>
.toggle-field {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
}

.toggle-field__content {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.toggle-field__content small {
  color: var(--color-text-muted);
}
</style>
