<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * Refreshing the global model catalog. The application never fetches a snapshot itself — an operator uploads
 * one — which is what keeps it free of any outbound request to a catalog host.
 *
 * Platform-admin only, because an import writes the global entries every tenant reads. A tenant that needs a
 * different price records an override instead, which cannot affect anybody else.
 */

import { computed, ref } from 'vue'

import FilePicker from '@/components/forms/FilePicker.vue'
import { importSnapshot } from '@/services/modelCatalogService'

const file = ref<File | null>(null)
const importing = ref(false)
const errorMessage = ref('')
const entriesWritten = ref<number | null>(null)

const fileName = computed(() => file.value?.name ?? null)

function chooseFile(event: Event): void {
  const input = event.target as HTMLInputElement
  file.value = input.files?.[0] ?? null
  errorMessage.value = ''
  entriesWritten.value = null
  // Cleared so picking the same file again raises another change event. Without this, an operator whose import
  // was refused cannot re-select the file they just chose, which is the way to retry after correcting it. The
  // file itself is already held above, so the input no longer has to carry it.
  input.value = ''
}

async function submit(): Promise<void> {
  if (!file.value) {
    errorMessage.value = 'Choose a snapshot file first.'
    return
  }

  importing.value = true
  errorMessage.value = ''
  entriesWritten.value = null
  try {
    const result = await importSnapshot(file.value)
    entriesWritten.value = result.entriesWritten ?? 0
  } catch (error) {
    // A malformed snapshot is operator error, so the cause is surfaced rather than left in the server log.
    errorMessage.value = error instanceof Error ? error.message : 'The snapshot could not be imported.'
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <section class="section-card" data-testid="model-catalog-snapshot">
    <header>
      <h3>Model catalog</h3>
      <p class="muted">
        The catalog ships with the application and is refreshed by uploading a newer snapshot. Nothing is fetched
        automatically, so this installation makes no outbound request to a catalog host.
      </p>
      <p class="muted">
        Importing updates the shared entries every tenant reads. A tenant's own pricing overrides are left
        untouched, and a model that has disappeared from a newer snapshot is kept rather than removed from under a
        configuration that still uses it.
      </p>
    </header>

    <div class="snapshot-form">
      <div class="snapshot-field">
        <label for="snapshot-file">Snapshot file</label>
        <!-- Picking is closed while an import runs. A new choice clears the messages, and the ones on screen
             belong to the request still in flight. The error is pointed at only while it is on screen, so the
             input never names an element that is not rendered. -->
        <FilePicker
          input-id="snapshot-file"
          accept="application/json,.json"
          :file-name="fileName"
          :described-by="errorMessage ? 'snapshot-error' : undefined"
          :disabled="importing"
          test-id="snapshot-file"
          @change="chooseFile"
        />
      </div>

      <button
        class="btn-primary btn-sm"
        type="button"
        :disabled="importing || !file"
        data-testid="snapshot-import"
        @click="submit"
      >
        {{ importing ? 'Importing…' : 'Import snapshot' }}
      </button>
    </div>

    <p v-if="errorMessage" id="snapshot-error" class="form-error" data-testid="snapshot-error">{{ errorMessage }}</p>
    <p v-else-if="entriesWritten !== null" class="form-success" data-testid="snapshot-result">
      Imported {{ entriesWritten.toLocaleString() }} catalog entries.
    </p>
  </section>
</template>

<style scoped>
.snapshot-form {
  display: flex;
  gap: 0.75rem;
  align-items: flex-end;
  flex-wrap: wrap;
  margin-block-start: 0.75rem;
}

/* The label sits above its control, so the import button beside it lines up with the control rather than
 * with the label. */
.snapshot-field {
  display: flex;
  flex-direction: column;
}
</style>
