<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <section class="form-section">
    <header v-if="title || $slots.header" class="form-section__header">
      <slot name="header">
        <h3>{{ title }}</h3>
      </slot>
    </header>
    <div class="form-section__body">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{ title?: string }>()
</script>

<style scoped>
.form-section {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.form-section__header h3 {
  margin: 0;
}
</style>
