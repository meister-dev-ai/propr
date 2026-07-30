<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="client-code-quality">
    <header>
      <h3>Code Quality</h3>
      <p>
        Which problems this client's codebases keep producing, and where they concentrate. The same views as the
        top-level Code Quality area, pinned to this client. Nothing here judges the reviewer.
      </p>
    </header>

    <CodeQualityWorkspace :client-id="clientId" />
  </section>
</template>

<script setup lang="ts">
import CodeQualityWorkspace from '@/features/code-insights/components/CodeQualityWorkspace.vue'

defineProps<{ clientId: string }>()
</script>

<style scoped>
.client-code-quality {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.client-code-quality h3 {
  margin: 0;
  font-size: 1.15rem;
  color: var(--color-text);
}

.client-code-quality header p {
  margin: 0.35rem 0 0;
  max-width: 70ch;
  font-size: 0.85rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}
</style>
