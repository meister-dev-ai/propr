<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * The payload the next snapshot would carry.
 *
 * The backend builds it from the same type the sender serializes, so the preview and the sent payload cannot
 * drift. Requesting the preview sends nothing, so it can be opened on an installation that has usage
 * statistics switched off.
 */
import { onMounted, ref } from 'vue'
import {
  formatPayloadForDisplay,
  getUsageStatisticsPreview,
  type UsageStatisticsPreview,
} from '@/services/usageStatisticsService'

const preview = ref<UsageStatisticsPreview | null>(null)
const loading = ref(false)
const errorMessage = ref('')

onMounted(load)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    preview.value = await getUsageStatisticsPreview()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The payload preview could not be built.'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <section class="section-card" data-testid="usage-statistics-preview">
    <div class="section-card-header">
      <div>
        <h2>Payload preview</h2>
        <p class="section-subtitle">
          The request body the next snapshot would carry, built by the same code that sends it. Requesting the
          preview sends nothing.
        </p>
      </div>

      <button class="btn-secondary btn-sm" type="button" :disabled="loading" data-testid="usage-statistics-preview-refresh" @click="load">
        {{ loading ? 'Building...' : 'Refresh' }}
      </button>
    </div>

    <div class="section-card-body">
      <p v-if="errorMessage" class="error" data-testid="usage-statistics-preview-error">{{ errorMessage }}</p>

      <pre v-else-if="preview" class="usage-payload" data-testid="usage-statistics-preview-payload">{{ formatPayloadForDisplay(preview.payload) }}</pre>

      <p v-if="preview" class="usage-preview-note">
        Posted as <code>{{ preview.contentType }}</code> to <code>{{ preview.endpoint }}</code>. Every field is
        described in the
        <a :href="preview.payloadDocumentationUrl" target="_blank" rel="noopener noreferrer">payload documentation</a>.
      </p>
    </div>
  </section>
</template>

<style scoped>
.usage-payload {
  margin: 0;
  padding: 0.9rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background: var(--surface-subtle);
  font-family: monospace;
  font-size: 0.82rem;
  line-height: 1.5;
  overflow-x: auto;
  white-space: pre;
}

.usage-preview-note {
  margin: 0.75rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}
</style>
