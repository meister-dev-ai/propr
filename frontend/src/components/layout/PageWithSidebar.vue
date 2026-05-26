<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-with-sidebar page-with-sidebar-layout">
    <aside class="page-sidebar page-with-sidebar-layout__sidebar">
      <slot name="sidebar" />
    </aside>
    <main class="page-main-content page-with-sidebar-layout__content">
      <slot />
    </main>
  </div>
</template>
