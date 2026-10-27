<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<script setup lang="ts">
/**
 * Starts an elevation of the signed-in platform administrator. The scope is the tenant or one of its clients; a
 * reason and a duration up to the maximum of the installation are required. The dialog is modal: the focus moves to
 * the reason, Tab stays inside the dialog, Escape closes it, and the focus returns to the control that opened it.
 */
import { computed, ref, useId, watch } from 'vue'
import { useModalDialog } from '@/composables/useModalDialog'
import { usePlatformAdminElevations } from '@/composables/usePlatformAdminElevations'
import type { PlatformAdminElevation } from '@/services/elevationsService'

interface ClientChoice {
  id: string
  displayName: string
}

const props = defineProps<{
  isOpen: boolean
  tenantId: string
  tenantName?: string | null
  /** A client to preselect as the scope, for example the client of the page. */
  clientId?: string | null
  clientName?: string | null
  /** The clients of the tenant to choose from; without them the scope is the preselected client or the tenant. */
  clients?: ClientChoice[]
}>()

const emit = defineEmits<{
  (e: 'update:isOpen', value: boolean): void
  (e: 'started', elevation: PlatformAdminElevation): void
}>()

const { maxDurationMinutes, start } = usePlatformAdminElevations()

const scopeClientId = ref<string>('')
const reason = ref('')
const durationMinutes = ref(60)
const submitting = ref(false)
const error = ref('')

const PRESETS = [15, 30, 60, 120, 240, 480, 720, 1440]

const durationOptions = computed(() => {
  const options = PRESETS.filter((minutes) => minutes <= maxDurationMinutes.value)
  if (!options.includes(maxDurationMinutes.value)) {
    options.push(maxDurationMinutes.value)
  }

  return options.sort((left, right) => left - right)
})

const clientChoices = computed<ClientChoice[]>(() => {
  if (props.clients && props.clients.length > 0) {
    return props.clients
  }

  return props.clientId ? [{ id: props.clientId, displayName: props.clientName ?? props.clientId }] : []
})

const reasonValid = computed(() => reason.value.trim().length > 0 && reason.value.length <= 500)

const id = useId()
const reasonField = ref<HTMLTextAreaElement | null>(null)
const { dialog, onKeydown, onCancel } = useModalDialog(() => props.isOpen, close, reasonField)

// Every opening of the dialog takes the next number. A start request shows its refusal and closes the dialog only
// while the dialog is in the opening that sent the request. If the administrator closes the dialog and opens it again
// before the request finishes, the request leaves the new form alone. A started elevation is reported in every case,
// and the shared state of the active elevations holds it.
let opening = 0

watch(() => props.isOpen, (open) => {
  if (!open) {
    return
  }

  opening++
  scopeClientId.value = props.clientId ?? ''
  reason.value = ''
  error.value = ''
  submitting.value = false
  durationMinutes.value = Math.min(60, maxDurationMinutes.value)
}, { immediate: true })

function formatDuration(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} minutes`
  }

  const hours = minutes / 60
  return Number.isInteger(hours) ? `${hours} ${hours === 1 ? 'hour' : 'hours'}` : `${minutes} minutes`
}

function close(): void {
  emit('update:isOpen', false)
}

async function submit(): Promise<void> {
  if (!reasonValid.value || submitting.value) {
    return
  }

  const submittedIn = opening
  submitting.value = true
  error.value = ''
  try {
    const elevation = await start({
      tenantId: props.tenantId,
      clientId: scopeClientId.value || null,
      reason: reason.value.trim(),
      durationMinutes: durationMinutes.value,
    })
    emit('started', elevation)
    if (submittedIn === opening && props.isOpen) {
      close()
    }
  } catch (failure) {
    if (submittedIn === opening) {
      error.value = failure instanceof Error ? failure.message : 'The elevation could not be started.'
    }
  } finally {
    if (submittedIn === opening) {
      submitting.value = false
    }
  }
}
</script>

<template>
  <Teleport to="body">
    <dialog
      v-if="isOpen"
      ref="dialog"
      class="elevation-dialog-frame"
      :aria-labelledby="`${id}-title`"
      :aria-describedby="`${id}-hint`"
      tabindex="-1"
      @keydown="onKeydown"
      @cancel="onCancel"
      @click.self="close"
    >
      <form class="confirm-dialog elevation-dialog" data-testid="start-elevation-dialog" @submit.prevent="submit">
        <div class="elevation-dialog-header">
          <h3 :id="`${id}-title`">Start an elevation</h3>
          <button type="button" class="elevation-dialog-close" aria-label="Close" @click="close">
            <i class="fi fi-rr-cross-small" aria-hidden="true"></i>
          </button>
        </div>

        <p :id="`${id}-hint`" class="elevation-dialog-hint">
          During the elevation you can read the reviews, findings, protocols and other content of the scope you choose.
          The elevation grants no write access. ProPR records the start, the end and every content read with your
          reason in the audit history of {{ tenantName ?? 'the tenant' }}, which its tenant administrators can read.
        </p>

        <label class="form-field">
          <span>Scope</span>
          <select v-model="scopeClientId" data-testid="elevation-scope">
            <option value="">Every client of {{ tenantName ?? 'the tenant' }}</option>
            <option v-for="client in clientChoices" :key="client.id" :value="client.id">Client {{ client.displayName }}</option>
          </select>
        </label>

        <label class="form-field">
          <span>Reason</span>
          <textarea
            ref="reasonField"
            v-model="reason"
            rows="2"
            maxlength="500"
            placeholder="Ticket number or the request of the customer"
            data-testid="elevation-reason"
          ></textarea>
        </label>

        <label class="form-field">
          <span>Duration</span>
          <select v-model.number="durationMinutes" data-testid="elevation-duration">
            <option v-for="minutes in durationOptions" :key="minutes" :value="minutes">{{ formatDuration(minutes) }}</option>
          </select>
        </label>

        <p v-if="error" class="error" data-testid="elevation-error">{{ error }}</p>

        <div class="confirm-dialog-actions elevation-dialog-actions">
          <button type="button" class="btn-secondary" @click="close">Cancel</button>
          <button type="submit" class="btn-primary" :disabled="!reasonValid || submitting" data-testid="elevation-submit">
            {{ submitting ? 'Starting…' : 'Start elevation' }}
          </button>
        </div>
      </form>
    </dialog>
  </Teleport>
</template>

<style scoped>
.elevation-dialog-frame {
  max-width: none;
  margin: auto;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--color-text);
}

.elevation-dialog-frame::backdrop {
  background: rgba(0, 0, 0, 0.7);
  backdrop-filter: blur(4px);
}

.elevation-dialog {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  max-width: 34rem;
}

.elevation-dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.elevation-dialog-header h3 {
  margin: 0;
}

.elevation-dialog-close {
  border: none;
  background: transparent;
  color: var(--color-text-muted);
  cursor: pointer;
  font-size: 1.2rem;
}

.elevation-dialog-hint {
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.5;
  color: var(--color-text-muted);
}

.form-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  margin-bottom: 0;
  font-size: 0.85rem;
}

.elevation-dialog-actions {
  margin-top: 0.5rem;
}

.form-field textarea,
.form-field select {
  width: 100%;
}
</style>
