<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <output class="state-card success-state">
    <slot>Saved successfully.</slot>
  </output>
</template>

<style scoped>
.success-state {
  display: block;
  color: var(--color-success);
}
</style>
