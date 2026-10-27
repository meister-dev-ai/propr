<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="login-container">
    <div class="login-view">
      <h1>Completing sign-in</h1>

      <p v-if="vm.loading.value" class="login-callback-copy">Signing you in…</p>
      <template v-else-if="vm.signedIn.value">
        <p class="login-callback-copy" data-testid="login-callback-signed-in">
          You are signed in, but the start page could not be loaded.
        </p>
        <RouterLink class="login-callback-recovery" :to="{ name: 'home' }">Open the start page</RouterLink>
      </template>
      <template v-else>
        <p v-if="vm.errorMessage.value" class="error" data-testid="login-callback-error">{{ vm.errorMessage.value }}</p>
        <RouterLink class="login-callback-recovery" :to="{ name: 'login' }">Back to sign-in</RouterLink>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { useLoginCallbackViewModel } from '@/features/auth/view-models/useLoginCallbackViewModel'

const vm = useLoginCallbackViewModel()
</script>

<style scoped>
.login-callback-copy {
  margin: 0;
  color: var(--color-text-muted);
}

.login-callback-recovery {
  display: inline-block;
  margin-top: 1rem;
}
</style>
