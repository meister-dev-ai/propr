<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * Administration page for anonymous usage statistics.
 *
 * Shows the control, the payload, the endpoint, the last send attempt and the privacy contact on one screen.
 */
import { computed, onMounted, ref } from 'vue'
import UsageStatisticsPayloadPreview from '@/features/usage-statistics/components/UsageStatisticsPayloadPreview.vue'
import { useNotification } from '@/composables/useNotification'
import { useUsageStatistics } from '@/composables/useUsageStatistics'

const { notify } = useNotification()
const { settings, loading, load, setEnabled, sendNow } = useUsageStatistics()

const saving = ref(false)
const sending = ref(false)
const errorMessage = ref('')
const toggleInput = ref<HTMLInputElement | null>(null)

const enabled = computed(() => settings.value?.enabled === true)
const managedByLicense = computed(() => settings.value?.managedByLicense === true)
const advisories = computed(() => settings.value?.update.advisories ?? [])

/**
 * True once a load has finished and produced nothing.
 *
 * The page reports that the state is unknown rather than rendering its defaults. Rendering the defaults would
 * show an interactive, off-looking toggle on an installation where a license keeps sending on.
 */
const unavailable = computed(() => !loading.value && settings.value === null)

const lastAttempt = computed(() => {
  const current = settings.value
  if (!current?.lastAttemptAt) {
    return 'No snapshot has been sent yet.'
  }

  const when = formatTimestamp(current.lastAttemptAt)
  if (current.lastAttemptSucceeded) {
    return `Last snapshot delivered ${when}.`
  }

  // The detail is only shown on a failure, where it says how the attempt went wrong.
  return `Last attempt ${when} did not reach the receiver. ${current.lastAttemptDetail ?? ''}`.trim()
})

onMounted(() => load(true))

async function toggle(): Promise<void> {
  if (managedByLicense.value || saving.value) {
    return
  }

  saving.value = true
  errorMessage.value = ''

  try {
    await setEnabled(!enabled.value)
    notify(enabled.value ? 'Anonymous usage statistics enabled.' : 'Anonymous usage statistics disabled.')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The setting could not be changed.'

    // The browser already flipped the box when it was clicked. The bound value has not changed, so Vue sees
    // nothing to patch and the box would sit checked next to the word "Off".
    if (toggleInput.value) {
      toggleInput.value.checked = enabled.value
    }
  } finally {
    saving.value = false
  }
}

/**
 * Runs a send cycle now instead of waiting for the daily one.
 *
 * Each outcome in which nothing was sent is reported separately, so the reason is visible.
 */
async function send(): Promise<void> {
  if (sending.value) {
    return
  }

  sending.value = true
  errorMessage.value = ''

  try {
    notify(describeSendDecision(await sendNow()))
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'The snapshot could not be sent.'
  } finally {
    sending.value = false
  }
}

function describeSendDecision(decision: string): string {
  switch (decision) {
    case 'sent':
      return 'Snapshot sent.'
    case 'disabled':
      return 'Nothing was sent: anonymous usage statistics are switched off.'
    case 'awaitingConsent':
      return 'Nothing was sent: the notice has not been shown to an administrator yet.'
    default:
      return 'Nothing was sent: a snapshot already went out today.'
  }
}

function formatTimestamp(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString()
}

/**
 * Whether an advisory's link is safe to put in an href.
 *
 * The value arrives from the receiver and is stored as it was sent, so without this check a compromised or
 * misconfigured receiver could supply a `javascript:` URL.
 */
function isSafeLink(link: string | null | undefined): boolean {
  if (!link) {
    return false
  }

  try {
    const { protocol } = new URL(link)
    return protocol === 'https:' || protocol === 'http:'
  } catch {
    return false
  }
}
</script>

<template>
  <div class="page-view usage-statistics-view">
    <section class="section-card" data-testid="usage-statistics-control">
      <div class="section-card-header">
        <div>
          <h2>Anonymous usage statistics</h2>
          <p class="section-subtitle">
            Once a day this installation sends an anonymous snapshot of itself and receives the latest version
            and any security advisories in return. The snapshot contains no code, no repository or
            organization names, no personal data and no raw counts.
          </p>
        </div>

        <label
          v-if="!unavailable"
          class="usage-toggle"
          :class="{ active: enabled, locked: managedByLicense }"
          data-testid="usage-statistics-toggle"
        >
          <input
            ref="toggleInput"
            type="checkbox"
            aria-label="Send anonymous usage statistics"
            :checked="enabled"
            :disabled="managedByLicense || saving || loading"
            @change="toggle"
          />
          <span>{{ enabled ? 'On' : 'Off' }}</span>
        </label>
      </div>

      <div class="section-card-body usage-statistics-body">
        <p v-if="unavailable" class="error" data-testid="usage-statistics-unavailable">
          The current setting could not be read. Reload the page, and if it keeps failing check that the
          installation has a database configured.
        </p>

        <p v-if="managedByLicense" class="usage-note" data-testid="usage-statistics-locked-note">
          <i class="fi fi-rr-lock" aria-hidden="true"></i>
          Managed by your commercial license. The control stays visible so administrators can see the current
          state.
        </p>

        <div v-if="!unavailable" class="usage-attempt-row">
          <p class="usage-note" data-testid="usage-statistics-last-attempt">{{ lastAttempt }}</p>
          <button
            class="btn-secondary btn-sm"
            type="button"
            :disabled="sending || loading"
            data-testid="usage-statistics-send-now"
            @click="send"
          >
            {{ sending ? 'Sending...' : 'Send now' }}
          </button>
        </div>

        <p v-if="settings && !settings.consentGateSatisfied" class="usage-note">
          Nothing has been sent yet. The first snapshot goes out after an administrator has seen the notice
          describing its contents.
        </p>

        <p v-if="errorMessage" class="error" data-testid="usage-statistics-error">{{ errorMessage }}</p>

        <dl v-if="settings" class="usage-facts">
          <div>
            <dt>Endpoint</dt>
            <dd><code>{{ settings.pingEndpoint }}</code></dd>
          </div>
          <div>
            <dt>Field-by-field documentation</dt>
            <dd>
              <a :href="settings.payloadDocumentationUrl" target="_blank" rel="noopener noreferrer">
                Payload documentation
              </a>
            </dd>
          </div>
          <div>
            <dt>Privacy contact</dt>
            <dd><a :href="`mailto:${settings.privacyContact}`">{{ settings.privacyContact }}</a></dd>
          </div>
        </dl>
      </div>
    </section>

    <section v-if="settings" class="section-card" data-testid="usage-statistics-updates">
      <div class="section-card-header">
        <div>
          <h2>Updates and advisories</h2>
          <p class="section-subtitle">
            The newest release and any security advisories the receiver last reported for the version you are
            running.
          </p>
        </div>
      </div>

      <div class="section-card-body usage-statistics-body">
        <div class="usage-version-row">
          <span class="chip chip-muted chip-sm">Running {{ settings.update.currentVersion }}</span>
          <span v-if="settings.update.latestVersion" class="chip chip-sm" :class="settings.update.updateAvailable ? 'chip-warning' : 'chip-success'">
            Latest {{ settings.update.latestVersion }}
          </span>
        </div>

        <p v-if="!settings.update.receivedAt" class="usage-note">
          Nothing reported yet. This fills in after the first snapshot reaches the receiver.
        </p>

        <ul v-if="advisories.length" class="usage-advisories" data-testid="usage-statistics-advisories">
          <li v-for="advisory in advisories" :key="advisory.id">
            <span class="chip chip-danger chip-sm">{{ advisory.severity }}</span>
            <a v-if="isSafeLink(advisory.link)" :href="advisory.link!" target="_blank" rel="noopener noreferrer">
              {{ advisory.title ?? advisory.id }}
            </a>
            <span v-else>{{ advisory.title ?? advisory.id }}</span>
            <span v-if="advisory.affectedVersions" class="usage-note">{{ advisory.affectedVersions }}</span>
          </li>
        </ul>
      </div>
    </section>

    <UsageStatisticsPayloadPreview />
  </div>
</template>

<style scoped>
.usage-statistics-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.usage-statistics-body {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.usage-toggle {
  display: inline-flex;
  align-items: center;
  gap: 0.55rem;
  padding: 0.45rem 0.8rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-pill);
  font-size: 0.85rem;
  font-weight: 600;
}

.usage-toggle.active {
  border-color: rgba(34, 197, 94, 0.35);
  background: rgba(34, 197, 94, 0.08);
}

.usage-toggle.locked {
  opacity: 0.65;
}

.usage-note {
  margin: 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.usage-attempt-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.usage-facts {
  display: grid;
  gap: 0.6rem;
  margin: 0;
}

.usage-facts dt {
  font-size: 0.78rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-text-muted);
}

.usage-facts dd {
  margin: 0.15rem 0 0;
  word-break: break-all;
}

.usage-version-row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.usage-advisories {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.usage-advisories li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
}
</style>
