<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- One input for one field a provider family declared. The connection form and an action's form both render
     the family's fields, and both have to render them by the declared shape: a choice is a select and a
     boolean is a checkbox, because a text box would accept a value the family already said it will not take.
     Keeping one renderer keeps the two forms from drifting apart. -->
<template>
  <label class="form-field" :data-testid="testId">
    <span>{{ field.label }}<template v-if="!field.isRequired"> (optional)</template></span>

    <select
      v-if="field.kind === 'choice'"
      :value="modelValue"
      :data-testid="`${testId}-input`"
      @change="emit('update:modelValue', ($event.target as HTMLSelectElement).value)"
    >
      <option v-for="choice in field.choices ?? []" :key="choice" :value="choice">{{ choice }}</option>
    </select>

    <input
      v-else-if="field.kind === 'bool'"
      :checked="modelValue === 'true'"
      type="checkbox"
      :data-testid="`${testId}-input`"
      @change="emit('update:modelValue', ($event.target as HTMLInputElement).checked ? 'true' : 'false')"
    />

    <textarea
      v-else-if="field.kind === 'stringList'"
      :value="modelValue"
      rows="3"
      :data-testid="`${testId}-input`"
      :placeholder="placeholder"
      @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
    ></textarea>

    <input
      v-else
      :value="modelValue"
      :type="inputType"
      :data-testid="`${testId}-input`"
      :placeholder="placeholder"
      @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />

    <small v-if="error" class="error" :data-testid="`${testId}-error`">{{ error }}</small>
    <small v-else-if="note" class="field-hint-inline" :data-testid="`${testId}-set`">{{ note }}</small>
    <small v-else-if="field.hint" class="field-hint-inline">{{ field.hint }}</small>
  </label>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { AiDeclaredFieldDto } from '@/services/aiConnectionsService'

const props = withDefaults(
  defineProps<{
    field: AiDeclaredFieldDto
    modelValue: string
    testId: string
    placeholder?: string
    error?: string | null
    note?: string | null
  }>(),
  { placeholder: '', error: null, note: null },
)

const emit = defineEmits<{ 'update:modelValue': [string] }>()

// Maps a declared field shape onto an input type. The vocabulary is closed, so this stays a lookup and not a
// form engine. A shape the server sends that this list does not name gets a text box, which the operator can
// still use.
const inputType = computed(() => {
  if (props.field.isSecret) {
    return 'password'
  }

  switch (props.field.kind) {
    case 'int':
      return 'number'
    case 'url':
      return 'url'
    default:
      return 'text'
  }
})
</script>

<style scoped>
/* The label is this component's root, so the parent's .form-field rule still reaches it. Everything inside is
   out of the parent's scope and is styled here. */
.form-field span {
  font-weight: 600;
}

.field-hint-inline {
  font-weight: normal;
  font-size: 0.78rem;
  color: var(--color-text-muted);
  text-transform: none;
  letter-spacing: 0;
}

/* Matches the connection form: a number box shows no native spinners beside the text fields next to it. */
input[type='number'] {
  appearance: textfield;
  -moz-appearance: textfield;
}

input[type='number']::-webkit-outer-spin-button,
input[type='number']::-webkit-inner-spin-button {
  appearance: none;
  -webkit-appearance: none;
  margin: 0;
}
</style>
