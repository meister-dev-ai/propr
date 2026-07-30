<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="code-quality">
    <header class="page-header">
      <div>
        <h1>Code Quality</h1>
        <p class="page-copy">
          Which problems a codebase keeps producing, and where they concentrate. Built from the findings reviews
          have already produced on your pull requests, with no extra analysis. Nothing here judges the reviewer.
        </p>
      </div>
    </header>

    <!-- The same workspace serves the per-client tab, pinned to that client. One implementation, so a fix to
         either lands on both. -->
    <CodeQualityWorkspace />
  </div>
</template>

<script setup lang="ts">
import CodeQualityWorkspace from '@/features/code-insights/components/CodeQualityWorkspace.vue'
</script>

<style scoped>
.code-quality {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1.5rem;
  max-width: 1400px;
  margin: 0 auto;
}

.page-header h1 {
  margin: 0;
  font-size: 1.6rem;
  letter-spacing: -0.02em;
  color: var(--color-text);
}

.page-copy {
  margin: 0.4rem 0 0;
  max-width: 70ch;
  font-size: 0.9rem;
  line-height: 1.45;
  color: var(--color-text-muted);
}
</style>
