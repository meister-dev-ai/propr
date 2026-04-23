<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div id="app">
    <AppHeader v-if="isAuthenticated" />
    <RouterView />
    <AppNotification />
    <footer v-if="isAuthenticated" class="app-footer">
      Powered by <a href="https://meister-dev.ai" target="_blank" rel="noopener noreferrer" class="footer-link">meister-dev.ai</a>
    </footer>
  </div>
</template>

<script setup lang="ts">
import { RouterView } from 'vue-router'
import AppHeader from '@/components/AppHeader.vue'
import AppNotification from '@/components/AppNotification.vue'
import { useSession } from '@/composables/useSession'

const { isAuthenticated } = useSession()
</script>

<style scoped>
.app-footer {
  text-align: center;
  padding: 1rem 2rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
  opacity: 0.6;
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  margin-top: auto;
}

.footer-link {
  color: var(--color-accent);
  text-decoration: none;
  opacity: 0.8;
  transition: opacity 0.2s;
}

.footer-link:hover {
  opacity: 1;
  text-decoration: underline;
}
</style>

