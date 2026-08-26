<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/**
 * Activation, replacement and removal of the installation's license document.
 *
 * A license file is read in the browser and sent as the same text a pasted token is sent as, so there is one
 * request shape rather than a file upload beside a paste box. Replacing and removing both ask for
 * confirmation, because both change what the whole installation is entitled to.
 */
import { computed, ref } from 'vue'
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import FilePicker from '@/components/forms/FilePicker.vue'
import { useLicensing } from '@/composables/useLicensing'
import { LicenseActivationRefusedError } from '@/services/licensingService'
import { activationFailureMessage } from '@/features/licensing/licensingCopy'

const { summary, summaryStale, activate, remove, reload } = useLicensing()

const token = ref('')
const busy = ref(false)
const failureMessage = ref('')
const successMessage = ref('')
/**
 * Set when the change was written but the state read back after it failed. It is separate from the failure
 * message because the change did take effect: what the card reports is that the entitlements shown below it
 * are the ones from before the change.
 */
const staleSummary = ref(false)
const refreshing = ref(false)
const fileError = ref('')
// Held here because the input is cleared after every read, so the input itself no longer names the file the
// operator picked.
const fileName = ref<string | null>(null)
const replaceConfirmOpen = ref(false)
const removeConfirmOpen = ref(false)
// Incremented on every file pick and on every manual edit of the text, so a file read that completes late
// can tell it no longer holds the latest input and leaves the newer state alone.
let readSequence = 0

const hasLicense = computed(() => summary.value !== null && summary.value.stage !== 'none')

const canSubmit = computed(() => summary.value !== null && token.value.trim().length > 0 && !busy.value)

const submitLabel = computed(() => (hasLicense.value ? 'Replace license' : 'Activate license'))

/**
 * Reads a chosen file in the browser. The endpoint takes the document as text, so nothing about the file
 * itself is sent: what an operator picks and what an operator pastes reach the backend identically.
 */
async function readFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  const read = ++readSequence
  token.value = ''
  fileError.value = ''
  failureMessage.value = ''
  successMessage.value = ''
  fileName.value = file.name

  try {
    const text = (await file.text()).trim()
    // A newer pick or a manual edit may have superseded this read while it was in flight; committing its
    // result now would stage content the displayed file name does not identify.
    if (read === readSequence) {
      token.value = text
    }
  } catch {
    if (read === readSequence) {
      fileError.value = 'The file could not be read. Open it and paste its contents instead.'
      // Nothing was read, so nothing is staged. Naming the file next to a message that asks for its contents
      // to be pasted would state otherwise.
      fileName.value = null
    }
  } finally {
    // Cleared so picking the same file again raises another change event. Without this, an operator whose
    // activation was refused cannot re-select the file they just chose, which is the obvious way to retry
    // after correcting it.
    input.value = ''
  }
}

/** A manual edit supersedes any file read still in flight, and the staged text no longer is the file's. */
function noteTokenEdited(): void {
  readSequence += 1
  fileName.value = null
  // A failed read asks for the contents to be pasted instead, which is what this edit does, so the message
  // stops applying to what is now staged.
  fileError.value = ''
}

function submit(): void {
  if (!canSubmit.value) {
    return
  }

  if (hasLicense.value) {
    replaceConfirmOpen.value = true
    return
  }

  void send()
}

function confirmReplace(): void {
  replaceConfirmOpen.value = false
  void send()
}

async function send(): Promise<void> {
  const replacing = hasLicense.value
  busy.value = true
  failureMessage.value = ''
  successMessage.value = ''
  staleSummary.value = false

  try {
    // The activation and the state read that follows it are reported separately. Only a rejection means the
    // license was not accepted; a read that failed after it is carried on the outcome, so an operator is not
    // told to submit a document the installation already runs on.
    const outcome = await activate(token.value.trim())
    // Both are cleared together. A file name left beside an empty box would describe a document that is no
    // longer staged for activation.
    token.value = ''
    fileName.value = null
    successMessage.value = replacing
      ? 'The license was replaced. The entitlements below are the ones the new license grants.'
      : 'The license was activated. The entitlements below are the ones it grants.'
    staleSummary.value = outcome.isSummaryStale
  } catch (error) {
    // The typed reason decides the message, so an operator reads what to do about this document rather than
    // a generic refusal. A reason this build does not know falls back to what the backend sent.
    failureMessage.value = error instanceof LicenseActivationRefusedError
      ? activationFailureMessage(error.reason, error.message)
      : error instanceof Error
        ? error.message
        : 'The license file was not accepted.'
  } finally {
    busy.value = false
  }
}

async function confirmRemove(): Promise<void> {
  removeConfirmOpen.value = false
  busy.value = true
  failureMessage.value = ''
  successMessage.value = ''
  staleSummary.value = false

  try {
    const outcome = await remove()
    successMessage.value = 'The license was removed. This installation runs as Community.'
    staleSummary.value = outcome.isSummaryStale
  } catch (error) {
    failureMessage.value = error instanceof Error ? error.message : 'The license could not be removed.'
  } finally {
    busy.value = false
  }
}

/**
 * Reads the licensing state again after a change whose read back failed. The change itself is not repeated:
 * it was written, and only what the panel shows about it is missing.
 */
async function refreshSummary(): Promise<void> {
  refreshing.value = true

  try {
    await reload()
    staleSummary.value = summaryStale.value
  } finally {
    refreshing.value = false
  }
}
</script>

<template>
  <section class="section-card licensing-activation-card">
    <div class="section-card-header">
      <div>
        <h3>License</h3>
        <p class="licensing-subtitle">
          The commercial edition follows from a signed license file issued by Meister DEV. The file is read in
          your browser and sent as text.
        </p>
      </div>
    </div>

    <div class="section-card-body">
      <p v-if="!hasLicense" class="licensing-activation-note" data-testid="license-activation-instruction">
        No license is active on this installation, so it runs as Community. Contact Meister DEV to obtain a
        license file, then upload it or paste its contents below. COMMERCIAL-LICENSE-POLICY.md describes
        which installations one license covers.
      </p>

      <div class="form-field">
        <label class="licensing-field-label" for="license-file">License file</label>
        <!-- The error is pointed at only while it is on screen, so the input never names an element that is
             not rendered. -->
        <FilePicker
          input-id="license-file"
          accept=".lic,.txt,.json,text/plain"
          :file-name="fileName"
          :described-by="fileError ? 'license-file-error' : undefined"
          :disabled="busy"
          test-id="license-file-input"
          @change="readFile"
        />
        <span v-if="fileError" id="license-file-error" class="field-error" data-testid="license-file-error">{{ fileError }}</span>
      </div>

      <div class="form-field">
        <label class="licensing-field-label" for="license-token">Or paste the license text</label>
        <textarea
          id="license-token"
          v-model="token"
          rows="4"
          spellcheck="false"
          data-testid="license-token-input"
          placeholder="Paste the contents of the license file"
          :disabled="busy"
          @input="noteTokenEdited"
        ></textarea>
      </div>

      <p v-if="failureMessage" class="error" data-testid="license-activation-error">{{ failureMessage }}</p>
      <p v-if="successMessage" class="licensing-success" data-testid="license-activation-success">{{ successMessage }}</p>

      <!-- Beside the success message rather than in place of it: the change was written, and what could not
           be read is the state the rest of the panel shows. -->
      <div v-if="staleSummary" class="licensing-summary-stale" data-testid="license-summary-stale">
        <span>
          The licensing state could not be read after the change, so what is shown below still describes the
          installation as it was before it.
        </span>
        <button
          class="btn-secondary btn-sm"
          type="button"
          :disabled="refreshing"
          data-testid="license-summary-refresh"
          @click="refreshSummary"
        >
          Refresh
        </button>
      </div>

      <div class="form-actions">
        <button
          class="btn-primary"
          type="button"
          :disabled="!canSubmit"
          data-testid="license-activate-button"
          @click="submit"
        >
          {{ submitLabel }}
        </button>
        <button
          v-if="hasLicense"
          class="btn-danger"
          type="button"
          :disabled="busy"
          data-testid="license-remove-button"
          @click="removeConfirmOpen = true"
        >
          Remove license
        </button>
      </div>
    </div>

    <ConfirmDialog
      :open="replaceConfirmOpen"
      message="Replace the license on this installation? The license in force is discarded, and the new one decides what the installation is entitled to."
      data-testid="license-replace-confirm"
      @confirm="confirmReplace"
      @cancel="replaceConfirmOpen = false"
    />

    <ConfirmDialog
      :open="removeConfirmOpen"
      message="Remove the license from this installation? It returns to the Community edition, and every commercial capability stops being available."
      data-testid="license-remove-confirm"
      @confirm="confirmRemove"
      @cancel="removeConfirmOpen = false"
    />
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-activation-card .section-card-body {
  padding: 1.25rem;
}

.licensing-activation-note {
  color: var(--color-text-muted);
  margin: 0 0 1.25rem;
}

.licensing-field-label {
  display: block;
  font-weight: 600;
  margin-bottom: 0.35rem;
}

.licensing-success {
  color: var(--color-success);
  margin: 0;
}

.licensing-summary-stale {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  color: var(--color-warning);
  font-size: 0.85rem;
  margin: 0.75rem 0 0;
  padding: 0.75rem;
  background: var(--color-warning-soft);
  border-radius: 0.25rem;
}

.form-actions {
  margin-top: 1rem;
}
</style>
