<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * The recorded license activations, replacements and removals, newest first.
 *
 * The records outlive the licenses they describe, so the list is shown whether or not a license is in force:
 * an installation that has none still needs to show when the last one was removed and by whom.
 */
import { onMounted, ref } from 'vue'
import { useLicensing } from '@/composables/useLicensing'
import { activationActionLabel } from '@/features/licensing/licensingCopy'

const { history, historyStale, loadHistory } = useLicensing()

const loading = ref(false)
const errorMessage = ref('')
/**
 * Bumped on every load this component starts, so an older one that finishes after a newer one leaves the
 * loading state and the error to the newer one.
 *
 * Reads started elsewhere, such as the history refresh that follows a license change, are ordered by the
 * composable, which raises only for the read that is still the current one. An error is therefore published
 * only for the latest load across both callers, instead of an older read here reporting a failure over records
 * a refresh has already loaded.
 */
let loadRequest = 0

onMounted(load)

async function load(): Promise<void> {
  const request = ++loadRequest
  loading.value = true
  errorMessage.value = ''
  historyStale.value = false

  try {
    await loadHistory()
  } catch (error) {
    if (request === loadRequest) {
      errorMessage.value = error instanceof Error ? error.message : 'Failed to load the license history.'
    }
  } finally {
    if (request === loadRequest) {
      loading.value = false
    }
  }
}

function formatTimestamp(value: string): string {
  return value === '' ? '' : new Date(value).toLocaleString()
}
</script>

<template>
  <section class="section-card licensing-history-card">
    <div class="section-card-header">
      <div>
        <h3>License history</h3>
        <p class="licensing-subtitle">
          Every activation, replacement and removal recorded on this installation, newest first. The records
          stay after the license they describe is removed.
        </p>
      </div>
      <button class="btn-secondary btn-sm" type="button" :disabled="loading" data-testid="license-history-refresh" @click="load">
        Refresh
      </button>
    </div>

    <div class="section-card-body">
      <!-- Above the branches below, because the warning applies whether or not there are records. On an
           installation whose first activation could not be re-read, the empty state below says that no change
           was recorded, which the failed refresh cannot confirm. -->
      <p v-if="historyStale" class="licensing-history-stale" data-testid="license-history-stale">
        The history could not be refreshed after the last change. Refresh to see the current records.
      </p>
      <div v-if="loading" class="licensing-history-state" data-testid="license-history-loading">
        <span>Loading the license history…</span>
      </div>
      <div v-else-if="errorMessage" class="licensing-history-state">
        <p class="error" data-testid="license-history-error">{{ errorMessage }}</p>
        <button class="btn-secondary btn-sm" type="button" @click="load">Try again</button>
      </div>
      <div v-else-if="history.length === 0" class="licensing-history-state" data-testid="license-history-empty">
        <h4>No license changes recorded</h4>
        <p>Activations, replacements and removals appear here once one is made.</p>
      </div>
      <div v-else class="licensing-history-list">
        <article
          v-for="(entry, index) in history"
          :key="`${entry.occurredAt}-${index}`"
          class="licensing-history-entry"
          :data-testid="`license-history-entry-${index}`"
        >
          <div class="licensing-history-entry-header">
            <strong>{{ activationActionLabel(entry.action) }}</strong>
            <span class="licensing-history-timestamp">{{ formatTimestamp(entry.occurredAt) }}</span>
          </div>
          <p class="licensing-history-detail">
            <span v-if="entry.licensee">{{ entry.licensee }}</span>
            <span v-else>Licensee not recorded</span>
            ·
            <code>{{ entry.licenseId ?? 'License id not recorded' }}</code>
          </p>
          <p class="licensing-history-actor">
            {{ entry.actorUserId ? `By user ${entry.actorUserId}` : 'No signed-in user recorded' }}
          </p>
        </article>
      </div>
    </div>
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-history-state {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 2rem 1.25rem;
}

.licensing-history-state h4,
.licensing-history-state p {
  margin: 0;
}

.licensing-history-list {
  display: flex;
  flex-direction: column;
  padding: 0 1.25rem 1.25rem;
}

.licensing-history-entry {
  padding: 1rem 0;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  border-bottom: 1px solid var(--color-border);
}

.licensing-history-entry:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.licensing-history-entry-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
}

.licensing-history-detail,
.licensing-history-actor,
.licensing-history-timestamp {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0;
}

.licensing-history-detail code {
  font-family: monospace;
  word-break: break-all;
}

.licensing-history-stale {
  color: var(--color-warning, #b45309);
  font-size: 0.85rem;
  margin: 0 1.25rem 1rem;
  padding: 0.75rem;
  background: var(--color-warning-bg, #fef3c7);
  border-radius: 0.25rem;
}
</style>
