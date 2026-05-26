<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="state-card error-state" role="alert">
    <slot>Something went wrong.</slot>
  </div>
</template>

<style scoped>
.error-state {
  color: var(--color-danger);
}
</style>
