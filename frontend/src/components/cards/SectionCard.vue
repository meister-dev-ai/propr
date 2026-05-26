<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <section class="section-card section-card-primitive">
    <header v-if="title || $slots.header" class="section-card-header">
      <slot name="header">
        <h3>{{ title }}</h3>
      </slot>
    </header>
    <div class="section-card-body">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
defineProps<{ title?: string }>()
</script>
