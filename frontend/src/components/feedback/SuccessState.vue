<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="state-card success-state" role="status">
    <slot>Saved successfully.</slot>
  </div>
</template>

<style scoped>
.success-state {
  color: var(--color-success);
}
</style>
