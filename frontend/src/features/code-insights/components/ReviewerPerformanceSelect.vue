<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div ref="controlHost" class="performance-field">
    <label :for="controlId">{{ label }}</label>
    <v-select
      :id="controlId"
      class="performance-control"
      :model-value="modelValue"
      :items="items"
      :disabled="disabled"
      :aria-label="label"
      :menu-props="{ maxHeight: 280, attach: menuTarget }"
      item-title="label"
      item-value="value"
      variant="outlined"
      density="compact"
      hide-details
      @update:model-value="$emit('update:modelValue', $event)"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import '../performance-controls.css'

const props = defineProps<{
  modelValue: string
  label: string
  items: { value: string; label: string }[]
  disabled?: boolean
  containedMenu?: boolean
}>()
defineEmits<{ 'update:modelValue': [value: string] }>()
const controlId = `performance-select-${useId()}`
const controlHost = ref<HTMLElement>()
const menuTarget = computed(() => {
  if (!props.containedMenu) {
    return undefined
  }

  // Keep the menu in the dialog's top layer without bubbling pointer events into its activator.
  return controlHost.value?.closest<HTMLDialogElement>('dialog') ?? undefined
})
</script>
