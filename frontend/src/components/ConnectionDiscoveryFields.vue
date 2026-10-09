<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<script setup lang="ts">
import type { useConnectionDiscovery } from '@/composables/useConnectionDiscovery'
import type { DiscoverySourceKind } from '@/services/providerDiscoveryService'

const props = withDefaults(defineProps<{
  discovery: ReturnType<typeof useConnectionDiscovery>
  idPrefix: string
  locked?: boolean
  disabled?: boolean
  showSource?: boolean
  knowledge?: boolean
}>(), { locked: false, disabled: false, showSource: false, knowledge: false })
const value = (event: Event) => (event.target as HTMLSelectElement).value
</script>

<template>
  <div v-if="locked && discovery.state.saved && (discovery.state.unresolved || !discovery.ready.value)" class="form-group saved-selection">
    <span>Saved scope</span>
    <span>{{ discovery.state.saved.providerScopePath || discovery.state.saved.organizationScopeId || 'Scope unavailable' }}</span>
    <span>Saved project coordinate</span>
    <span>{{ discovery.state.saved.providerProjectKey || 'Project coordinate unavailable' }}</span>
    <button v-if="discovery.state.unresolved" type="button" class="btn-secondary" :disabled="disabled || discovery.state.loading.connections"
      @click="discovery.state.connectionId ? discovery.selectSavedConnection(discovery.state.connectionId) : discovery.resolveForEdit(discovery.state.saved)">Retry discovery</button>
  </div>
  <div class="form-group">
    <label :for="idPrefix + '-connection'">Connection</label>
    <div class="input-wrapper">
      <select :id="idPrefix + '-connection'" :value="discovery.state.connectionId"
        :disabled="disabled || discovery.state.loading.connections || locked && !discovery.state.unresolved"
        @change="locked ? discovery.selectSavedConnection(value($event)) : discovery.selectConnection(value($event))">
        <option value="">{{ discovery.state.loading.connections ? 'Loading connections...' : 'Select a connection' }}</option>
        <option v-for="connection in discovery.state.connections" :key="connection.id" :value="connection.id">
          {{ connection.displayName || connection.hostBaseUrl }}
        </option>
      </select>
    </div>
    <span v-if="discovery.state.errors.connections || discovery.state.errors.descriptor" class="field-error">
      {{ discovery.state.errors.connections || discovery.state.errors.descriptor }}
    </span>
    <span v-if="discovery.state.ambiguous" class="field-help">Choose the connection for this saved target.</span>
    <span v-else-if="discovery.state.unresolved" class="field-help">The saved target is unavailable. You can update unrelated settings.</span>
    <span v-if="knowledge && discovery.state.descriptor && !discovery.state.descriptor.supportsKnowledgeSources" class="field-help">
      This connection does not support knowledge source creation.
    </span>
  </div>
  <div v-if="discovery.state.descriptor" class="form-group">
    <label :for="idPrefix + '-scope'">{{ discovery.state.descriptor.scopeLabel }}</label>
    <div class="input-wrapper">
      <select :id="idPrefix + '-scope'" :value="discovery.state.scopeKey"
        :disabled="disabled || locked || discovery.state.loading.scopes"
        @change="discovery.selectScope(value($event))">
        <option value="">{{ discovery.state.loading.scopes ? 'Loading scopes...' : 'Select a scope' }}</option>
        <option v-for="scope in discovery.state.scopes" :key="scope.scopeKey ?? ''" :value="scope.scopeKey">
          {{ scope.displayName || scope.scopeKey }}
        </option>
      </select>
    </div>
    <span v-if="discovery.state.errors.scopes" class="field-error">{{ discovery.state.errors.scopes }}</span>
    <span v-else-if="!discovery.state.loading.scopes && !discovery.state.scopes.length" class="field-help">No accessible scopes were returned.</span>
  </div>
  <div v-if="discovery.state.descriptor?.projectLabel" class="form-group">
    <label :for="idPrefix + '-project'">{{ discovery.state.descriptor.projectLabel }}</label>
    <div class="input-wrapper">
      <select :id="idPrefix + '-project'" :value="discovery.state.projectId"
        :disabled="disabled || locked || !discovery.state.scopeKey || discovery.state.loading.projects"
        @change="discovery.selectProject(value($event))">
        <option value="">{{ discovery.state.loading.projects ? 'Loading projects...' : 'Select a project' }}</option>
        <option v-for="project in discovery.state.projects" :key="project.projectId ?? ''" :value="project.projectId">
          {{ project.projectName || project.projectId }}
        </option>
      </select>
    </div>
    <span v-if="discovery.state.errors.projects" class="field-error">{{ discovery.state.errors.projects }}</span>
  </div>
  <div v-if="showSource && discovery.state.descriptor" class="form-group">
    <label :for="idPrefix + '-kind'">Source kind</label>
    <div class="input-wrapper">
      <select :id="idPrefix + '-kind'" :value="discovery.state.sourceKind" :disabled="disabled || locked"
        @change="discovery.selectSourceKind(value($event) as DiscoverySourceKind)">
        <option v-for="kind in discovery.state.descriptor.sourceKinds" :key="kind.kind" :value="kind.kind">{{ kind.label }}</option>
      </select>
    </div>
  </div>
  <div v-if="showSource" class="form-group">
    <label :for="idPrefix + '-source'">{{ discovery.state.descriptor?.sourceKinds?.find(kind => kind.kind === discovery.state.sourceKind)?.label || 'Source' }}</label>
    <div class="input-wrapper">
      <select :id="idPrefix + '-source'" :value="discovery.state.sourceKey"
        :disabled="disabled || locked || discovery.state.loading.sources || !discovery.ready.value"
        @change="discovery.selectSource(value($event))">
        <option value="">{{ discovery.state.loading.sources ? 'Loading sources...' : 'Select a source' }}</option>
        <option v-for="source in discovery.state.sources" :key="discovery.sourceKey(source)" :value="discovery.sourceKey(source)">
          {{ source.displayName }}
        </option>
      </select>
    </div>
  </div>
  <span v-if="discovery.state.errors.sources || discovery.state.errors.branches" class="field-error">
    {{ discovery.state.errors.sources || discovery.state.errors.branches }}
  </span>
</template>

<style scoped>
.form-group { display: flex; flex-direction: column; gap: 0.5rem; }
label { font-weight: 500; }
.input-wrapper select { width: 100%; min-height: 2.75rem; padding: 0.625rem 0.75rem; border: 1px solid var(--color-border); border-radius: var(--radius-sm); color: var(--color-text); background: var(--color-bg); font: inherit; }
.field-error { color: var(--color-danger); font-size: 0.875rem; }
.field-help { color: var(--color-text-muted); font-size: 0.875rem; }
.saved-selection { overflow-wrap: anywhere; color: var(--color-text-muted); }
</style>
