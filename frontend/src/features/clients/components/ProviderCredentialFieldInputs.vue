<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- The credential the selected authentication mode collects. A mode declaring one field renders one box; a
     mode declaring none says so, because an empty area reads as a form that has not loaded. -->
<template>
  <label v-for="field in fields" :key="field.name" class="form-field">
    <span>{{ field.label }}<template v-if="!field.isRequired"> (optional)</template></span>
    <input
      :value="values[field.name] ?? ''"
      :data-testid="`ai-credential-${field.name}`"
      :type="field.isSecret ? 'password' : 'text'"
      :placeholder="field.isSecret ? 'Paste the provider secret' : ''"
      @input="emit('update', field.name, ($event.target as HTMLInputElement).value)"
    />
    <small v-if="field.hint" class="field-hint-inline" :data-testid="`ai-credential-${field.name}-hint`">
      {{ field.hint }}
    </small>
  </label>
  <small
    v-if="fields.length === 0"
    class="field-hint-inline ai-form-grid-full"
    data-testid="ai-credential-none"
  >
    This authentication mode stores no credential.
  </small>
</template>

<script setup lang="ts">
import type { ProviderCredentialField } from '@/services/aiConnectionsService'

defineProps<{
  fields: readonly ProviderCredentialField[]
  values: Record<string, string>
}>()

const emit = defineEmits<{ update: [name: string, value: string] }>()
</script>

<style scoped>
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
</style>
