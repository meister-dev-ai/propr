<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- A field the family computes rather than asks for. It is shown at the height of the inputs beside it, with
     the refusal in place of the value when the host could not compute one. -->
<template>
  <div class="form-field ai-form-grid-full" :data-testid="testId">
    <span>{{ field.label }}</span>
    <output class="declared-computed-value" :data-testid="`${testId}-value`">{{ value }}</output>
    <small v-if="refusal" class="error" :data-testid="`${testId}-refusal`">{{ refusal }}</small>
    <small v-else-if="field.hint" class="field-hint-inline">{{ field.hint }}</small>
  </div>
</template>

<script setup lang="ts">
import type { AiDeclaredFieldDto } from '@/services/aiConnectionsService'

withDefaults(
  defineProps<{
    field: AiDeclaredFieldDto
    testId: string
    value?: string
    refusal?: string | null
  }>(),
  { value: '', refusal: null },
)
</script>

<style scoped>
.form-field span {
  font-weight: 600;
}

/* A computed value is shown, not entered. It reads as text at the height of the inputs beside it. */
.declared-computed-value {
  display: block;
  padding: 0.5rem 0;
  font-family: var(--font-mono, monospace);
  word-break: break-all;
}

.field-hint-inline {
  font-weight: normal;
  font-size: 0.78rem;
  color: var(--color-text-muted);
  text-transform: none;
  letter-spacing: 0;
}
</style>
