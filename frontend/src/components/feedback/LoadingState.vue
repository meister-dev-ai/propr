<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <output class="state-card loading-state">
    <slot>Loading...</slot>
  </output>
</template>

<style scoped>
.loading-state {
  display: block;
}
</style>
