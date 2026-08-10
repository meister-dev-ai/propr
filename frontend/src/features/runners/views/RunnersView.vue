<!--
  Copyright (c) Andreas Rain.
  Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
  This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.
-->
<template>
  <div class="page-view runners-view">
    <div class="page-toolbar">
      <div>
        <h2 class="view-title">Runners</h2>
        <p class="runners-description">
          Hosts that execute reviews outside the control plane.
          <template v-if="spansAllTenants">Every tenant in this installation.</template>
        </p>
      </div>
      <div class="runners-toolbar-actions">
        <button class="btn-secondary" :disabled="loading" @click="load">
          <i class="fi fi-rr-refresh"></i> Refresh
        </button>
        <button class="btn-primary" @click="openIssueDialog">
          <i class="fi fi-rr-add"></i> Issue registration token
        </button>
      </div>
    </div>

    <p v-if="loadError" class="error">{{ loadError }}</p>

    <!--
      The fleet before the hosts. An operator arrives asking whether the work is moving, and a table of
      rows that each look individually fine cannot answer that: four healthy runners and forty reviews
      waiting is the state worth seeing first, and it is invisible one row at a time.
    -->
    <section v-if="!loading && !loadError" class="section-card">
      <div class="section-card-body runners-summary">
        <div class="runners-stat">
          <div class="runners-stat-value">
            {{ activeRunnerCount }}<span class="runners-stat-total"> / {{ runners.length }}</span>
          </div>
          <div class="runners-stat-label">Runners active</div>
        </div>
        <div class="runners-stat">
          <div class="runners-stat-value">{{ executingJobCount }}</div>
          <div class="runners-stat-label">Reviews running</div>
        </div>
        <div class="runners-stat">
          <div class="runners-stat-value" :class="{ 'runners-stat-warning': pendingJobCount > 0 && executingJobCount === 0 }">
            {{ pendingJobCount }}
          </div>
          <div class="runners-stat-label">
            Waiting<span v-if="oldestPendingSince">, oldest {{ formatTime(oldestPendingSince) }}</span>
          </div>
        </div>
        <div class="runners-stat">
          <div class="runners-stat-value">{{ completedJobCount }}</div>
          <div class="runners-stat-label">Finished in the last day</div>
        </div>
        <div class="runners-stat">
          <div class="runners-stat-value">
            <span :class="['chip', executionMode === 'RunnersOnly' ? 'chip-success' : 'chip-muted']">
              {{ executionModeLabel }}
            </span>
          </div>
          <div class="runners-stat-label">Where reviews execute</div>
        </div>
      </div>
    </section>

    <!--
      The stall is shown above the list rather than inside it. A stalled queue is a property of the
      fleet, and an operator whose runners all went quiet needs to see the cause before reading a table
      of rows that each look individually fine.
    -->
    <section v-if="registry?.stall" class="section-card runners-stall">
      <div class="section-card-body">
        <div class="runners-stall-title">
          <i class="fi fi-rr-exclamation"></i> Queue stalled: {{ stallCauseLabel }}
        </div>
        <p class="runners-stall-detail">
          {{ registry.stall.pendingJobCount }} job(s) waiting, oldest since
          {{ formatTime(registry.stall.oldestPendingSince) }}.
          <span v-if="registry.stall.detail">{{ registry.stall.detail }}</span>
        </p>
      </div>
    </section>

    <!--
      Issued tokens, listed because one that leaked before it was used stays valid for its whole lifetime
      and an operator otherwise has no way to see it, let alone withdraw it. The secret is not shown: only
      its hashes were ever stored.
    -->
    <section v-if="pendingTokens.length" class="section-card">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Outstanding registration tokens</h3>
          <span class="chip chip-muted">{{ pendingTokens.length }}</span>
        </div>
      </div>
      <table>
        <thead>
          <tr>
            <th>Issued</th>
            <th>Expires</th>
            <th>Remaining uses</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="token in pendingTokens" :key="token.tokenId">
            <td>{{ formatTime(token.issuedAt) }}</td>
            <td>{{ token.expiresAt ? formatTime(token.expiresAt) : 'Never' }}</td>
            <td>{{ token.remainingUses ?? 'Unlimited' }}</td>
            <td>
              <div class="actions-cell">
                <button class="btn-danger" @click="revokeToken(token)">Revoke</button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </section>

    <section class="section-card">
      <div class="section-card-header">
        <div class="section-card-header-left">
          <h3>Fleet</h3>
          <span v-if="!loading" class="chip chip-muted">
            {{ runners.length }} runner{{ runners.length === 1 ? '' : 's' }}
          </span>
        </div>
      </div>

      <p v-if="loading" class="loading" style="padding: 1rem 1.25rem;">Loading…</p>

      <table v-else-if="runners.length">
        <thead>
          <tr>
            <th>Runner</th>
            <!-- Across tenants a display name alone does not identify a host: two tenants may each run
                 one called "build-01". -->
            <th v-if="spansAllTenants">Tenant</th>
            <th>Health</th>
            <th>Working on</th>
            <th>Client scope</th>
            <th>Tags</th>
            <th>Contract</th>
            <th>Last seen</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="runner in runners" :key="runner.id" :class="{ 'row-inactive': runner.state !== 'Enrolled' }">
            <td>
              <div class="runners-name">{{ runner.displayName }}</div>
              <div class="runners-subtle">{{ runner.id }}</div>
            </td>
            <td v-if="spansAllTenants">
              <RouterLink
                v-if="runner.tenantId"
                :to="{ name: 'runners', params: { tenantId: runner.tenantId } }"
              >
                {{ runner.tenantName || runner.tenantId }}
              </RouterLink>
            </td>
            <td>
              <span :class="['chip', healthChipClass(runner)]">
                <i :class="healthIcon(runner)"></i> {{ healthLabel(runner) }}
              </span>
            </td>
            <!--
              What it is doing, not only how many. A runner sitting on one review for an hour and a runner
              turning over six look identical as a count, and the first is the one worth interrupting.
            -->
            <td>
              <div v-if="runner.executingJobCount">
                <div v-for="job in runner.executing ?? []" :key="job.jobId" class="runners-job">
                  <span class="runners-name">{{ job.repositoryName ?? 'unknown repository' }} #{{ job.pullRequestNumber }}</span>
                  <span class="runners-subtle"> · {{ elapsedSince(job.startedAt) }}</span>
                  <span v-if="job.reclaimCount" class="chip chip-warning chip-sm">
                    reclaimed {{ job.reclaimCount }}x
                  </span>
                </div>
                <div v-if="unnamedJobCount(runner) > 0" class="runners-subtle">
                  and {{ unnamedJobCount(runner) }} more
                </div>
              </div>
              <span v-else class="runners-subtle">Idle</span>
              <div v-if="runner.completedJobCount" class="runners-subtle">
                {{ runner.completedJobCount }} finished in the last day
              </div>
            </td>
            <td>
              <span v-if="!runner.clientScope?.length" class="runners-subtle">
                Every client in the tenant
              </span>
              <span v-else>{{ runner.clientScope.length }} client(s)</span>
            </td>
            <td>
              <span v-if="!runner.tags?.length" class="runners-subtle">None</span>
              <template v-else>
                <span v-for="tag in runner.tags" :key="tag" class="chip chip-muted chip-sm">{{ tag }}</span>
              </template>
            </td>
            <td>v{{ runner.contractVersion }}</td>
            <td>{{ runner.lastSeenAt ? formatTime(runner.lastSeenAt) : 'Never' }}</td>
            <td>
              <div class="actions-cell">
                <button
                  v-if="runner.state === 'Enrolled'"
                  class="btn-danger"
                  @click="confirmRevoke(runner)"
                >
                  Revoke
                </button>
                <!--
                  Offered only when the runner is executing nothing, mirroring the server's rule: a held
                  lease refuses the delete anyway, and a button that always fails teaches operators to
                  ignore errors.
                -->
                <button
                  v-if="!runner.executingJobCount"
                  class="btn-critical"
                  @click="confirmDelete(runner)"
                >
                  Delete
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>

      <p v-else class="empty-state" style="padding: 1rem 1.25rem;">
        No runners are enrolled. Reviews run in the control plane.
      </p>
    </section>

    <!--
      Shown once, at creation, and never retrievable afterwards, so the dialog says so plainly rather
      than letting an operator close it expecting to find the value in the list later.
    -->
    <Teleport to="body">
      <div v-if="tokenDialogOpen" class="confirm-dialog-overlay" @click.self="closeTokenDialog">
        <div class="confirm-dialog runners-dialog">
          <h3>Registration token</h3>
          <template v-if="issuedToken">
            <p class="runners-dialog-warning">
              <i class="fi fi-rr-exclamation"></i>
              Copy this now. It is shown once and cannot be retrieved again.
            </p>
            <div class="form-field">
              <label for="runner-token">RUNNER_REGISTRATION_TOKEN</label>
              <textarea id="runner-token" :value="issuedToken.token" readonly rows="2"></textarea>
            </div>
            <p class="runners-subtle">
              {{ issuedToken.expiresAt ? 'Expires ' + formatTime(issuedToken.expiresAt) + '.' : 'It does not expire.' }}
              Revoke it here when it is no longer wanted.
            </p>
          </template>
          <template v-else>
            <!-- The tenant a host joins is decided by the token and never changes afterwards, so on the
                 installation-wide view it has to be chosen rather than inferred. -->
            <div v-if="spansAllTenants" class="form-field">
              <label for="runner-token-tenant">Tenant the runner joins</label>
              <select id="runner-token-tenant" v-model="tokenTenantId">
                <option :value="null" disabled>Choose a tenant…</option>
                <option v-for="tenant in selectableTenants" :key="tenant.id" :value="tenant.id">
                  {{ tenant.displayName }}
                </option>
              </select>
            </div>
            <div class="form-field">
              <label for="runner-token-hours">Valid for (hours)</label>
              <input id="runner-token-hours" v-model="validForHours" type="number" min="1" placeholder="Never expires" />
              <span class="runners-subtle">Leave empty for a token that does not expire.</span>
            </div>
            <!-- One host enrolled by hand needs a single use. A scaling group needs more, because the
                 replicas it starts have nobody present to issue them a token each. -->
            <div class="form-field">
              <label for="runner-token-uses">Hosts it may enrol</label>
              <input id="runner-token-uses" v-model="maxUses" type="number" min="1" placeholder="No limit" />
              <span class="runners-subtle">
                One for a host you start yourself. More to provision a scaling group, whose replicas each
                spend a use as they start. Leave empty for no limit.
              </span>
            </div>
            <p class="runners-subtle">
              The client scope is stamped onto the enrollment by the server. Leave it empty to allow
              every client in the tenant.
            </p>
          </template>
          <div class="confirm-dialog-actions">
            <button class="btn-secondary" @click="closeTokenDialog">Close</button>
            <button
              v-if="!issuedToken"
              class="btn-primary"
              :disabled="issuing || (spansAllTenants && !tokenTenantId)"
              @click="issueToken"
            >
              {{ issuing ? 'Issuing…' : 'Issue' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>

    <Teleport to="body">
      <div v-if="revokeDialogOpen" class="confirm-dialog-overlay" @click.self="revokeDialogOpen = false">
        <div class="confirm-dialog">
          <h3>Revoke runner</h3>
          <p>
            {{ revokeTarget?.displayName }} stops being able to lease immediately. A review it is
            running now finishes, and its lease is returned when it does.
          </p>
          <div class="confirm-dialog-actions">
            <button class="btn-secondary" @click="revokeDialogOpen = false">Cancel</button>
            <button class="btn-danger" :disabled="revoking" @click="revoke">
              {{ revoking ? 'Revoking…' : 'Revoke' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>

    <Teleport to="body">
      <div v-if="deleteDialogOpen" class="confirm-dialog-overlay" @click.self="deleteDialogOpen = false">
        <div class="confirm-dialog">
          <h3>Delete runner</h3>
          <p>
            {{ deleteTarget?.displayName }} is removed from the registry and stops counting as
            capacity. A host still running under this identity loses it and reports refused until it
            is enrolled again with a new token. This cannot be undone.
          </p>
          <div class="confirm-dialog-actions">
            <button class="btn-secondary" @click="deleteDialogOpen = false">Cancel</button>
            <button class="btn-critical" :disabled="deleting" @click="removeRunner">
              {{ deleting ? 'Deleting…' : 'Delete' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import {
  deleteRunner,
  elapsedSince,
  executionModeLabel as toExecutionModeLabel,
  fleetCompletedCount,
  getAllRunnerRegistries,
  getRunnerRegistry,
  issueRegistrationToken,
  revokeRegistrationToken,
  revokeRunner,
  runnerHealth,
  unnamedJobCount,
  type PendingToken,
  type Runner,
  type RunnerRegistrationToken,
  type RunnerRegistry,
} from '@/services/runnerAdminService'
import { listTenants } from '@/services/tenantAdminService'

/**
 * Absent on the installation-wide route, where a platform administrator reads every tenant at once.
 * The two routes render the same view because they answer the same question; only the breadth differs.
 */
const props = defineProps<{ tenantId?: string }>()

/** True when this is the installation-wide fleet rather than one tenant's. */
const spansAllTenants = computed(() => !props.tenantId)

const registry = ref<RunnerRegistry | null>(null)
const loading = ref(true)
const loadError = ref<string | null>(null)

const tokenDialogOpen = ref(false)
const issuedToken = ref<RunnerRegistrationToken | null>(null)
// Held as typed rather than as numbers: a number input yields a string, and an empty one is the whole
// point here — it is how an operator says "no expiry" and "no limit".
const validForHours = ref('24')
const maxUses = ref('1')

/** A blank box means unbounded, so it becomes an absent field rather than a zero. */
function optionalCount(entered: string): number | undefined {
  const trimmed = entered.trim()
  if (trimmed === '') {
    return undefined
  }

  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : undefined
}
const issuing = ref(false)

/**
 * Which tenant an enrolled host joins. Fixed on the tenant route; chosen on the installation-wide one,
 * where there is no tenant in the URL to take it from. A runner's tenant is decided here and never
 * changes afterwards, so the choice cannot be left implicit.
 */
const tokenTenantId = ref<string | null>(null)

/**
 * Every tenant, not merely those that already have a runner — the first token for a tenant is issued
 * when it has none, which is exactly the case a list built from the runners in view would omit.
 */
const selectableTenants = ref<{ id: string; displayName: string }[]>([])

const revokeDialogOpen = ref(false)
const revokeTarget = ref<Runner | null>(null)
const revoking = ref(false)

const deleteDialogOpen = ref(false)
const deleteTarget = ref<Runner | null>(null)
const deleting = ref(false)

const runners = computed(() => registry.value?.runners ?? [])
const pendingTokens = computed(() => registry.value?.pendingTokens ?? [])
const activeRunnerCount = computed(() => registry.value?.activeRunnerCount ?? 0)
const executingJobCount = computed(() => registry.value?.executingJobCount ?? 0)
const pendingJobCount = computed(() => registry.value?.pendingJobCount ?? 0)
const oldestPendingSince = computed(() => registry.value?.oldestPendingSince ?? null)
const executionMode = computed(() => registry.value?.executionMode ?? 'InProcess')

const completedJobCount = computed(() => fleetCompletedCount(runners.value))
const executionModeLabel = computed(() => toExecutionModeLabel(executionMode.value))

const stallCauseLabel = computed(() => {
  switch (registry.value?.stall?.cause) {
    case 'NoActiveRunner':
      return 'no runner has been heard from'
    case 'NoFreeSlot':
      return 'no runner is taking work'
    case 'NoRunnerMatchesRequiredTags':
      return 'no runner declares the tags this work requires'
    default:
      return registry.value?.stall?.cause ?? 'unknown'
  }
})

function healthLabel(runner: Runner): string {
  switch (runnerHealth(runner)) {
    case 'active':
      return 'Active'
    case 'stale':
      return 'Not responding'
    case 'incompatible':
      return 'Unsupported contract'
    default:
      return 'Revoked'
  }
}

function healthChipClass(runner: Runner): string {
  switch (runnerHealth(runner)) {
    case 'active':
      return 'chip-success'
    case 'stale':
    case 'incompatible':
      return 'chip-warning'
    default:
      return 'chip-muted'
  }
}

/**
 * A revoked runner reads as muted rather than as an alarm: somebody meant to revoke it, and colouring a
 * deliberate act red puts every decommissioned host in the same visual register as a fleet that has
 * fallen over.
 */
function healthIcon(runner: Runner): string {
  switch (runnerHealth(runner)) {
    case 'active':
      return 'fi fi-rr-check-circle'
    case 'stale':
      return 'fi fi-rr-time-past'
    case 'incompatible':
      return 'fi fi-rr-exclamation'
    default:
      return 'fi fi-rr-ban'
  }
}

// Every timestamp on the wire is optional in the generated contract, and some genuinely are: a runner
// that never called in has no last-seen. Rendering a placeholder beats a crash or the word "Invalid Date".
function formatTime(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : 'unknown'
}


function readRegistry(): Promise<RunnerRegistry> {
  return props.tenantId ? getRunnerRegistry(props.tenantId) : getAllRunnerRegistries()
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    registry.value = await readRegistry()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

/**
 * A quiet refresh, for the poll. The spinner belongs to a page the operator asked to load; making it
 * appear every ten seconds on its own would make a working fleet look like one that keeps reloading.
 */
async function refresh(): Promise<void> {
  if (loading.value || document.hidden) {
    return
  }

  try {
    registry.value = await readRegistry()
    loadError.value = null
  } catch {
    // Left as it was. A poll that fails once should not replace a screen full of accurate figures with
    // an error; the next tick either recovers or the operator refreshes and sees the failure properly.
  }
}

// In-flight work is the point of this page, and work that finished thirty seconds ago is not in flight.
// Paused while the tab is hidden, since nobody is reading it.
const refreshTimer = window.setInterval(() => void refresh(), 10_000)
onUnmounted(() => window.clearInterval(refreshTimer))

async function openIssueDialog(): Promise<void> {
  issuedToken.value = null
  tokenTenantId.value = props.tenantId ?? null
  tokenDialogOpen.value = true

  if (!spansAllTenants.value || selectableTenants.value.length > 0) {
    return
  }

  try {
    const tenants = await listTenants()
    selectableTenants.value = tenants
      .filter((tenant): tenant is typeof tenant & { id: string } => Boolean(tenant.id))
      .map((tenant) => ({ id: tenant.id, displayName: tenant.displayName ?? tenant.id }))
  } catch (error) {
    // The dialog stays open with no tenants to choose, and issuing is disabled until one is picked, so a
    // failure here cannot mint a token against the wrong tenant.
    loadError.value = error instanceof Error ? error.message : String(error)
  }
}

function closeTokenDialog(): void {
  tokenDialogOpen.value = false
  issuedToken.value = null
}

async function issueToken(): Promise<void> {
  // Cleared before retrying: a success that leaves the previous failure's alert up tells an operator the
  // opposite of what happened.
  const targetTenantId = props.tenantId ?? tokenTenantId.value
  if (!targetTenantId) {
    return
  }

  loadError.value = null
  issuing.value = true
  try {
    issuedToken.value = await issueRegistrationToken(
      targetTenantId,
      [],
      optionalCount(validForHours.value),
      optionalCount(maxUses.value),
    )
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error)
    tokenDialogOpen.value = false
  } finally {
    issuing.value = false
  }
}

async function revokeToken(token: PendingToken): Promise<void> {
  if (!token.tokenId) {
    return
  }

  loadError.value = null
  try {
    await revokeRegistrationToken(token.tokenId)
    await load()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error)
  }
}

function confirmRevoke(runner: Runner): void {
  revokeTarget.value = runner
  revokeDialogOpen.value = true
}

/**
 * The id is optional in the generated contract even though the server always sends one. Guarding here
 * rather than asserting keeps the failure a no-op instead of a revoke request against "undefined".
 */
function revokeTargetId(): string | null {
  return revokeTarget.value?.id ?? null
}

async function revoke(): Promise<void> {
  if (!revokeTarget.value) {
    return
  }

  const id = revokeTargetId()
  if (!id) {
    return
  }

  revoking.value = true
  try {
    await revokeRunner(id)
    revokeDialogOpen.value = false
    await load()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error)
  } finally {
    revoking.value = false
  }
}

function confirmDelete(runner: Runner): void {
  deleteTarget.value = runner
  deleteDialogOpen.value = true
}

async function removeRunner(): Promise<void> {
  const id = deleteTarget.value?.id
  if (!id) {
    return
  }

  deleting.value = true
  try {
    await deleteRunner(id)
    deleteDialogOpen.value = false
    await load()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : String(error)
  } finally {
    deleting.value = false
  }
}

// Watched rather than mounted. The router reuses this component when moving between two
// /tenants/:tenantId/runners URLs, so onMounted alone would leave the previous tenant's registry on
// screen while every action targeted it.
watch(() => props.tenantId, load, { immediate: true })
</script>

<style scoped>
.runners-description {
  color: var(--color-text-muted);
  font-size: 0.875rem;
  margin: 0.25rem 0 0;
}

.runners-toolbar-actions {
  display: flex;
  gap: 0.75rem;
}

/* The fleet summary reads left to right as one line of figures, and wraps rather than scrolls so a
   narrow window drops to a second row instead of hiding the count that matters. */
.runners-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 2rem;
}

.runners-stat-value {
  font-size: 1.5rem;
  font-weight: 600;
  line-height: 1.6;
}

.runners-stat-total {
  font-size: 0.875rem;
  font-weight: 400;
  color: var(--color-text-muted);
}

.runners-stat-warning {
  color: var(--color-warning);
}

.runners-stat-label {
  font-size: 0.75rem;
  color: var(--color-text-muted);
}

.runners-stall {
  border-color: var(--color-warning);
  background: var(--color-warning-soft);
}

.runners-stall-title {
  font-weight: 600;
  color: var(--color-warning);
}

.runners-stall-detail {
  font-size: 0.875rem;
  margin: 0.25rem 0 0;
}

/* A div inside the cell rather than the cell itself. `display: flex` on a `td` takes it out of the
   table's column model, and the row then renders wider than the card that contains it — the actions
   column spilled past the card's right edge. The wrapper keeps the cell a cell and lays out only its
   contents: a gap that holds whether the buttons sit side by side or wrap onto two lines. */
.actions-cell {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 0.5rem;
  align-items: center;
}

.runners-name {
  font-weight: 500;
}

.runners-subtle {
  font-size: 0.75rem;
  color: var(--color-text-muted);
}

.runners-job + .runners-job {
  margin-top: 0.25rem;
}

.runners-dialog {
  max-width: 40rem;
}

.runners-dialog-warning {
  color: var(--color-warning);
  font-size: 0.875rem;
}

.runners-dialog textarea {
  width: 100%;
  font-family: var(--font-mono, monospace);
  word-break: break-all;
}
</style>
