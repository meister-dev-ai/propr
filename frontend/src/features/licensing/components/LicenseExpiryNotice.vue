<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented. -->

<script setup lang="ts">
/** Administrators can open the actionable license notice from the application header. */
import { computed, onMounted, onUnmounted, ref, useId, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { useLicensing } from '@/composables/useLicensing'

const { isAdmin, isAuthenticated } = useSession()
const { summary, noticeStage, load, reset } = useLicensing()

const visible = computed(() => isAuthenticated.value && isAdmin.value && noticeStage.value !== null)
const open = ref(false)
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const noticeId = useId()
const noticeLabel = computed(() => noticeStage.value === 'warning' ? 'License expires soon' : 'License expired')

const daysRemaining = computed(() => summary.value?.daysRemaining ?? null)

const expiresAt = computed(() => formatDate(summary.value?.expiresAt ?? null))

const graceEndsAt = computed(() => formatDate(summary.value?.graceEndsAt ?? null))

/** The stage names the tone: an expired term is marked more strongly than one still running. */
const toneClass = computed(() => (noticeStage.value === null ? '' : `licensing-notice-${noticeStage.value}`))

onMounted(() => {
  void ensureLoaded()
  document.addEventListener('click', onDocumentClick)
  document.addEventListener('keydown', onKeydown)
})
watch(() => isAuthenticated.value && isAdmin.value, ensureLoaded)
watch(visible, () => { open.value = false })

// The notice renders only while a session is active, so this unmounts on sign-out. Clearing here stops the
// next session from rendering the previous session's licensing state.
onUnmounted(() => {
  document.removeEventListener('click', onDocumentClick)
  document.removeEventListener('keydown', onKeydown)
  reset()
})

function onDocumentClick(event: MouseEvent): void {
  if (event.target instanceof Node && !root.value?.contains(event.target)) open.value = false
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    open.value = false
    trigger.value?.focus()
  }
}

async function ensureLoaded(): Promise<void> {
  if (!isAuthenticated.value || !isAdmin.value) {
    return
  }

  await load()
}

function formatDate(value: string | null): string {
  return value === null ? '' : new Date(value).toLocaleDateString(undefined, { timeZone: 'UTC' })
}
</script>

<template>
  <div v-if="visible" ref="root" class="licensing-disclosure">
    <button
      ref="trigger"
      type="button"
      class="licensing-trigger"
      :aria-label="noticeLabel"
      :title="noticeLabel"
      :aria-expanded="open"
      :aria-controls="noticeId"
      data-testid="license-expiry-trigger"
      @click="open = !open"
    >
      <i class="fi fi-rr-exclamation" aria-hidden="true"></i>
    </button>
    <aside v-if="open" :id="noticeId" :class="['licensing-notice', toneClass]" :aria-label="noticeLabel" role="status" data-testid="license-expiry-notice">
      <i class="fi fi-rr-time-past licensing-notice-icon" aria-hidden="true"></i>

      <p v-if="noticeStage === 'warning'" class="licensing-notice-text" data-testid="license-expiry-notice-warning">
        <!--
          The date and the day count are each rendered only when the summary carries them, so a payload missing
          one never produces a sentence with a gap where it should have been.
        -->
        <template v-if="expiresAt !== ''">
          This installation's license expires on {{ expiresAt }}<template v-if="daysRemaining !== null">, in
          {{ daysRemaining }} day(s)</template>.
        </template>
        <template v-else-if="daysRemaining !== null">
          This installation's license expires in {{ daysRemaining }} day(s).
        </template>
        <template v-else>This installation's license is close to expiring.</template>
        Activate a renewed license to keep commercial capabilities available.
        <RouterLink :to="{ name: 'licensing' }" @click="open = false">Open licensing</RouterLink>.
      </p>

      <p v-else-if="noticeStage === 'grace'" class="licensing-notice-text" data-testid="license-expiry-notice-grace">
        <template v-if="expiresAt !== ''">This installation's license expired on {{ expiresAt }}.</template>
        <template v-else>This installation's license has expired.</template>
        <template v-if="graceEndsAt !== ''">
          Commercial capabilities stay available until {{ graceEndsAt }}, after which the installation runs as
          Community. Activate a renewed license before that date.
        </template>
        <template v-else>
          Commercial capabilities stay available while its grace window remains open. Activate a renewed license
          before the installation returns to Community.
        </template>
        <RouterLink :to="{ name: 'licensing' }" @click="open = false">Open licensing</RouterLink>.
      </p>

      <p v-else class="licensing-notice-text" data-testid="license-expiry-notice-reverted">
        This installation's license expired and its grace window ended, so it runs as Community and its
        commercial capabilities are not available. Activate a renewed license to make them available again.
        <RouterLink :to="{ name: 'licensing' }" @click="open = false">Open licensing</RouterLink>.
      </p>
    </aside>
  </div>
</template>

<style scoped>
.licensing-disclosure {
    position: relative;
    display: inline-flex;
}
.licensing-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2.5rem;
  height: 2.5rem;
  border: 1px solid var(--color-warning);
  border-radius: var(--radius-md);
  color: var(--color-warning);
  background: var(--color-surface);
  cursor: pointer;
}
.licensing-trigger:focus-visible {
    outline: 2px solid var(--color-accent);
    outline-offset: 2px;
}
.licensing-notice {
  position: absolute;
  right: 0;
  top: calc(100% + 0.5rem);
  z-index: 100;
  width: min(32rem, calc(100vw - 2rem));
  box-shadow: 0 6px 24px rgb(0 0 0 / 25%);
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.8rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-surface);
}

.licensing-notice-grace,
.licensing-notice-reverted {
  border-color: var(--color-warning);
}

.licensing-notice-icon {
  color: var(--color-accent);
  margin-top: 0.15rem;
}

.licensing-notice-grace .licensing-notice-icon,
.licensing-notice-reverted .licensing-notice-icon {
  color: var(--color-warning);
}

.licensing-notice-text {
  flex: 1;
  margin: 0;
  font-size: 0.85rem;
  line-height: 1.55;
  color: var(--color-text-muted);
}

@media (max-width: 700px) {
  .licensing-notice {
    position: fixed;
    right: 1rem;
    top: 5rem;
    max-height: calc(100dvh - 6rem);
    overflow: auto;
  }
}
</style>
