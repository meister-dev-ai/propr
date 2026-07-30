<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <!-- Next to the number, never in a footnote. Outcomes and misses are judged by a model whose own accuracy
       has not been measured yet, and a precise-looking figure with no such note invites a decision the data
       cannot support. -->
  <p class="estimate-notice">
    <i class="fi fi-rr-info" aria-hidden="true"></i>
    <span>{{ text }}</span>
  </p>
</template>

<script setup lang="ts">
withDefaults(
  defineProps<{ text?: string }>(),
  {
    text:
      'AI-estimated. Outcomes and misses are judged by a model, not confirmed by a person, '
      + 'and the accuracy of that judgement has not been measured yet.',
  },
)
</script>

<style scoped>
.estimate-notice {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin: 0;
  padding: 0.5rem 0.7rem;
  border: 1px dashed var(--color-border);
  border-radius: 0.6rem;
  background: rgba(148, 163, 184, 0.08);
  color: var(--color-text-muted);
  font-size: 0.8rem;
  line-height: 1.35;
}

.estimate-notice i {
  color: var(--color-accent);
  margin-top: 0.1rem;
}
</style>
