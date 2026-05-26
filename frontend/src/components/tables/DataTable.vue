<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="data-table-shell">
    <table>
      <slot />
    </table>
  </div>
</template>

<style scoped>
.data-table-shell {
  overflow-x: auto;
}
</style>
