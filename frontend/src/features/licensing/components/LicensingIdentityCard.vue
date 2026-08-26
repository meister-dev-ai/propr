<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The identifier this installation reports itself under.
 *
 * It is shown for support and reporting: it tells one installation's reports apart from another's and it is
 * what a support conversation quotes. It binds nothing — no license is issued against it and no license check
 * reads it — so the copy does not present it as something to obtain a license with.
 */
import { computed, ref } from 'vue'
import { useLicensing } from '@/composables/useLicensing'

const { summary } = useLicensing()

const identity = computed(() => summary.value?.licensingIdentity ?? null)

const copied = ref(false)
const copyFailed = ref(false)

async function copy(): Promise<void> {
  if (identity.value === null) {
    return
  }

  copied.value = false
  copyFailed.value = false

  try {
    await navigator.clipboard.writeText(identity.value)
    copied.value = true
  } catch {
    // Clipboard access can be refused by the browser. The value stays selectable, so the operator can copy it
    // by hand; saying so is more useful than a silent no-op.
    copyFailed.value = true
  }
}
</script>

<template>
  <section v-if="identity" class="section-card licensing-identity-card" data-testid="licensing-identity">
    <div class="section-card-header">
      <div>
        <h3>Installation identity</h3>
        <p class="licensing-subtitle">
          Quote this identifier in usage reports and support requests. No license is issued against it, and no
          license check reads it.
        </p>
      </div>
    </div>

    <div class="section-card-body licensing-identity-body">
      <code class="licensing-identity-value" data-testid="licensing-identity-value">{{ identity }}</code>
      <button
        class="btn-secondary btn-sm"
        type="button"
        aria-label="Copy the installation identity"
        data-testid="licensing-identity-copy"
        @click="copy"
      >
        Copy
      </button>

      <!-- The outcome replaces nothing on the page, so it is announced rather than only shown. -->
      <span class="licensing-identity-hint" role="status" aria-live="polite">
        <span v-if="copied" data-testid="licensing-identity-copied">Copied</span>
        <span v-else-if="copyFailed" data-testid="licensing-identity-copy-failed">
          The browser refused clipboard access. Select the identifier and copy it.
        </span>
      </span>
    </div>
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-identity-body {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
  padding: 1.25rem;
}

.licensing-identity-value {
  font-family: monospace;
  word-break: break-all;
}

.licensing-identity-hint {
  color: var(--color-text-muted);
  font-size: 0.78rem;
}
</style>
