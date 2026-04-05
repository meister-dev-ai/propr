<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <Transition name="notification">
    <div
      v-if="notification"
      :class="['app-notification', `app-notification--${notification.type}`]"
      role="status"
    >
      {{ notification.message }}
      <button class="notification-dismiss" @click="dismiss" aria-label="Dismiss">×</button>
    </div>
  </Transition>
</template>

<script setup lang="ts">
import { useNotification } from '@/composables/useNotification'

const { notification, dismiss } = useNotification()
</script>

<style scoped>
.app-notification {
  position: fixed;
  top: 1rem;
  right: 1rem;
  z-index: 200;
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 0.75rem 1rem;
  border-radius: 0.375rem;
  font-size: 0.875rem;
  font-weight: 500;
  box-shadow: 0 4px 6px rgba(0,0,0,0.1);
}
.app-notification--success { background: #d1fae5; color: #065f46; }
.app-notification--error { background: #fee2e2; color: #991b1b; }
.notification-dismiss {
  background: transparent;
  color: inherit;
  padding: 0;
  font-size: 1.25rem;
  line-height: 1;
}
.notification-enter-active, .notification-leave-active { transition: opacity 0.3s, transform 0.3s; }
.notification-enter-from, .notification-leave-to { opacity: 0; transform: translateY(-0.5rem); }
</style>
