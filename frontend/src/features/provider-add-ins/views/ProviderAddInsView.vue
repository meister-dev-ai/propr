<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-view provider-add-ins-view">
    <div class="page-toolbar">
      <h2 class="view-title">AI provider add-ins</h2>
    </div>

    <section class="section-card">
      <div class="section-card-body">
        <p class="section-subtitle">
          An add-in you mount is found here and does not run until you activate it. What this page shows for one
          was read out of the file with none of it executed, so the decision is about a binary rather than about
          a family that is already running. Activating is bound to the file's content hash: replacing the file
          brings it back here for a fresh decision.
        </p>
        <p class="section-subtitle">
          The directories are read once, while this host starts. An add-in added since is not listed until the
          host restarts, and one deleted from a mounted volume still appears and still serves.
        </p>
        <p v-if="error" class="error" data-testid="add-ins-error">{{ error }}</p>
      </div>
    </section>

    <p v-if="loading" class="loading">Loading…</p>

    <!-- First, because it is the only thing on this page an administrator can act on. -->
    <section v-else-if="awaiting.length" class="section-card" data-testid="add-ins-awaiting">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Waiting for activation</h3>
          <span class="chip chip-warning">{{ awaiting.length }}</span>
        </div>
      </div>
      <div class="add-in-table-scroll">
        <table>
          <thead>
            <tr>
              <th>Identity</th>
              <th>Version</th>
              <th>Contacts</th>
              <th>Capability</th>
              <th>File</th>
              <th>Content hash</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="found in awaiting" :key="found.contentHash ?? found.filePath">
              <td>
                {{ found.key ?? found.assemblyName }}
                <div v-if="found.label" class="add-in-secondary">{{ found.label }}</div>
              </td>
              <td>{{ found.version ?? found.assemblyVersion }}</td>
              <td>{{ found.reachedHosts.length ? found.reachedHosts.join(', ') : '—' }}</td>
              <td>{{ found.requiredCapability ?? '—' }}</td>
              <td class="add-in-path" :title="found.filePath">{{ found.filePath }}</td>
              <td class="add-in-hash" :title="found.contentHash ?? undefined">{{ found.contentHash ?? '—' }}</td>
              <td>
                <button
                  v-if="found.canBeActivated"
                  type="button"
                  class="btn-primary btn-sm"
                  :disabled="activating === found.contentHash"
                  :data-testid="`add-in-activate-${found.contentHash}`"
                  @click="activate(found.contentHash!)"
                >
                  {{ activating === found.contentHash ? 'Activating…' : 'Activate' }}
                </button>
                <span v-else class="error add-in-refusal">{{ found.refusal }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <!-- Rejected add-ins come first. An operator opens this page when a family is missing. -->
    <section v-else-if="rejected.length" class="section-card">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Skipped</h3>
          <span class="chip chip-danger">{{ rejected.length }}</span>
        </div>
      </div>
      <div class="add-in-table-scroll">
      <table>
        <thead>
          <tr>
            <th>Category</th>
            <th>Reason</th>
            <th>Identity</th>
            <th>File</th>
            <th>Directory</th>
            <th>Content hash</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(skipped, index) in rejected" :key="`${skipped.filePath}-${index}`">
            <td><span class="chip chip-danger chip-sm">{{ skipped.category }}</span></td>
            <td>{{ skipped.reason }}</td>
            <td>{{ skipped.key ?? '—' }}</td>
            <td class="add-in-path" :title="skipped.filePath ?? undefined">{{ skipped.filePath }}</td>
            <td>{{ skipped.origin }}</td>
            <td class="add-in-hash" :title="skipped.contentHash ?? undefined">{{ skipped.contentHash ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      </div>
    </section>

    <section v-if="!loading" class="section-card">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Loaded</h3>
          <span class="chip chip-muted">{{ loaded.length }}</span>
        </div>
      </div>

      <div v-if="loaded.length" class="add-in-table-scroll">
        <table>
        <thead>
          <tr>
            <th>Identity</th>
            <th>Name</th>
            <th>Version</th>
            <th>Contract</th>
            <th>Reaches</th>
            <th>Requires</th>
            <th>File</th>
            <th>Directory</th>
            <th>Content hash</th>
          </tr>
        </thead>
        <tbody>
          <!-- The key is shown beside the name because two add-ins may declare the same name. -->
          <tr v-for="family in loaded" :key="family.key">
            <td>{{ family.key }}</td>
            <td>{{ family.label }}</td>
            <td>{{ family.version }}</td>
            <td>{{ family.contractVersion }}</td>
            <td>{{ family.reachedHostPatterns.length ? family.reachedHostPatterns.join(', ') : '—' }}</td>
            <td>{{ family.requiredCapabilityKey ?? '—' }}</td>
            <td class="add-in-path" :title="family.filePath ?? undefined">{{ family.filePath }}</td>
            <td>{{ family.origin }}</td>
            <td class="add-in-hash" :title="family.contentHash ?? undefined">{{ family.contentHash ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      </div>

      <div v-else class="section-card-body">
        <p>No provider family was loaded from an add-in directory. The families this build compiles in are unaffected.</p>
      </div>
    </section>

    <section v-if="!loading && activations.length" class="section-card" data-testid="add-ins-activations">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Activated</h3>
          <span class="chip">{{ activations.length }}</span>
        </div>
      </div>
      <div class="section-card-body">
        <p class="section-subtitle">
          What each administrator decided, and what the add-in said about itself at the time. Withdrawing an
          activation stops the next start taking that binary. It does not stop the family serving now: an
          assembly cannot be unloaded from under the connections using it.
        </p>
      </div>
      <div class="add-in-table-scroll">
        <table>
          <thead>
            <tr>
              <th>Identity</th>
              <th>Version</th>
              <th>Activated by</th>
              <th>When</th>
              <th>Serving</th>
              <th>Content hash</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="decision in activations" :key="decision.contentHash">
              <td>
                {{ decision.key }}
                <div class="add-in-secondary">{{ decision.label }}</div>
              </td>
              <td>{{ decision.version }}</td>
              <td>{{ decision.activatedByDisplayName }}</td>
              <td>{{ new Date(decision.activatedAt).toLocaleString() }}</td>
              <td>
                <span v-if="decision.isServing" class="chip chip-sm">yes</span>
                <span v-else class="chip chip-danger chip-sm">no</span>
              </td>
              <td class="add-in-hash" :title="decision.contentHash">{{ decision.contentHash }}</td>
              <td>
                <button
                  type="button"
                  class="btn-secondary btn-sm"
                  :disabled="revoking === decision.contentHash"
                  :data-testid="`add-in-revoke-${decision.contentHash}`"
                  @click="revoke(decision.contentHash)"
                >
                  {{ revoking === decision.contentHash ? 'Withdrawing…' : 'Withdraw' }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import {
  activateProviderAddIn,
  getProviderAddInInventory,
  listProviderAddInActivations,
  revokeProviderAddIn,
  type AwaitingProviderAddIn,
  type LoadedProviderAddIn,
  type ProviderAddInActivation,
  type RejectedProviderAddIn,
} from '@/services/providerAddInsService'

const loaded = ref<LoadedProviderAddIn[]>([])
const rejected = ref<RejectedProviderAddIn[]>([])
const awaiting = ref<AwaitingProviderAddIn[]>([])
const activations = ref<ProviderAddInActivation[]>([])
const loading = ref(false)
const error = ref('')
const activating = ref<string | null>(null)
const revoking = ref<string | null>(null)

onMounted(() => {
  void loadInventory()
})

async function loadInventory() {
  loading.value = true
  error.value = ''

  try {
    // Both together: the activations name what the inventory is showing, and reading them apart leaves the
    // page saying an add-in is activated and not listing it, or the other way round.
    const [inventory, decisions] = await Promise.all([
      getProviderAddInInventory(),
      listProviderAddInActivations(),
    ])

    loaded.value = inventory.loaded
    rejected.value = inventory.rejected
    awaiting.value = inventory.awaiting
    activations.value = decisions
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Failed to load the provider add-in inventory.'
    loaded.value = []
    rejected.value = []
    awaiting.value = []
    activations.value = []
  } finally {
    loading.value = false
  }
}

// Activating loads the add-in, so what the host is running has changed and the whole page is read again rather
// than the one row being moved. The refusal, where there is one, is what the host said and not a restatement.
async function activate(contentHash: string) {
  activating.value = contentHash
  error.value = ''

  try {
    await activateProviderAddIn(contentHash)
    await loadInventory()
  } catch (activationError) {
    error.value = activationError instanceof Error ? activationError.message : 'The add-in was not activated.'
  } finally {
    activating.value = null
  }
}

async function revoke(contentHash: string) {
  revoking.value = contentHash
  error.value = ''

  try {
    await revokeProviderAddIn(contentHash)
    await loadInventory()
  } catch (revocationError) {
    error.value = revocationError instanceof Error ? revocationError.message : 'The activation was not withdrawn.'
  } finally {
    revoking.value = null
  }
}
</script>

<style scoped>
/* Nine columns do not fit the card, and a table that does not scroll is squeezed instead: the path column
   collapses to a few characters and every row grows to the height of the path broken across them. The table
   keeps its natural width and the card scrolls. */
.add-in-table-scroll {
  overflow-x: auto;
}

.add-in-table-scroll table {
  min-width: 100%;
  width: max-content;
}

/* An absolute path and a content hash are long, and neither is read at a glance. Each is kept on one line and
   cut where it runs out of room; the whole value is on the cell's title and is selectable once the table is
   scrolled to it. */
.add-in-path,
.add-in-hash {
  font-family: var(--font-mono, monospace);
  font-size: 0.85em;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.add-in-path {
  max-width: 22rem;
}

.add-in-hash {
  max-width: 10rem;
}

/* The label under an identity, and the reason an add-in cannot be activated. Both are secondary to the row they
   sit in and neither should widen its column. */
.add-in-secondary {
  font-size: 0.85em;
  color: var(--color-text-muted);
}

.add-in-refusal {
  display: block;
  max-width: 24rem;
  font-size: 0.85em;
}
</style>
