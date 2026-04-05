<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="access-denied-view">
    <h2>403 — Access Denied</h2>
    <p>You do not have permission to access this page.</p>
    <RouterLink to="/" class="btn-secondary">Back to Home</RouterLink>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
</script>

<style scoped>
.access-denied-view {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1rem;
  padding: 4rem 2rem;
  text-align: center;
}
.access-denied-view h2 {
  font-size: 1.75rem;
  margin: 0;
}
.access-denied-view p {
  color: var(--color-text-mute, #6b7280);
  margin: 0;
}
</style>
