<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <section class="pr-code-quality">
    <header>
      <h3>Code Quality</h3>
      <p>
        What the reviews on this pull request found, and what became of it: the kinds of problem raised, the files
        carrying them, and how much was still being reported at the newest increment. Collected across every review
        of pull request #{{ pullRequestId }} rather than one of them, and nothing here judges the reviewer.
      </p>
    </header>

    <CodeQualityWorkspace
      :client-id="clientId"
      :repository-id="repositoryId"
      :pull-request-id="pullRequestId"
    />
  </section>
</template>

<script setup lang="ts">
import CodeQualityWorkspace from '@/features/code-insights/components/CodeQualityWorkspace.vue'

// The whole scope arrives from the pull request being looked at, so this tab offers no filters of its own.
defineProps<{
  clientId: string
  repositoryId: string
  pullRequestId: number
}>()
</script>

<style scoped>
.pr-code-quality {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.pr-code-quality h3 {
  margin: 0;
  font-size: 1.15rem;
  color: var(--color-text);
}

.pr-code-quality header p {
  margin: 0.35rem 0 0;
  max-width: 70ch;
  font-size: 0.85rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}
</style>
