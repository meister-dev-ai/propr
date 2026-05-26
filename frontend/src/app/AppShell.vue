<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <v-app>
    <slot name="header" />
    <v-main>
      <slot />
    </v-main>
    <slot name="footer" />
  </v-app>
</template>

<script setup lang="ts">
// Vuetify-backed application shell. Header, drawer, and footer wiring is supplied by
// route views in T013. This component is intentionally dumb: it owns no session state,
// no navigation logic, and no service calls.
</script>
