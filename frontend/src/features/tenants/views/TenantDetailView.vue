<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<!-- One workspace for everything a tenant owns, laid out the way a client's is: a directory to come from, a
     sidebar of grouped sections, and one section on screen at a time. What used to be four separate pages
     (settings, members, budget, spend) are sections here, and those routes redirect in. -->

<template>
  <PageWithSidebar>
    <template #sidebar>
      <AppNavDrawer>
        <RouterLink class="back-link" :to="{ name: 'tenant-directory' }" style="margin-bottom: 0">
          <i class="fi fi-rr-arrow-left"></i> Back to tenants
        </RouterLink>

        <div v-if="vm.tenant.value" class="detail-page-title" style="margin-bottom: 1.5rem">
          <h2 style="font-size: 1.25rem">{{ vm.tenant.value.displayName }}</h2>
          <p class="detail-page-subtitle">Tenant configuration</p>
        </div>

        <div class="sidebar-nav">
          <div v-for="group in visibleGroups" :key="group.title" class="sidebar-nav-group">
            <h4>{{ group.title }}</h4>
            <button
              v-for="entry in group.entries"
              :key="entry.section"
              type="button"
              class="sidebar-nav-link"
              :class="{ active: activeSection === entry.section }"
              :data-testid="`tenant-nav-${entry.section}`"
              @click="activeSection = entry.section"
            >
              <i class="fi" :class="entry.icon"></i> {{ entry.label }}
            </button>
          </div>
        </div>
      </AppNavDrawer>
    </template>

    <p v-if="loadFailed" class="error" data-testid="tenant-detail-error">{{ vm.policyError.value || 'The tenant could not be loaded.' }}</p>
    <p v-else-if="vm.isLoading.value" class="loading">Loading tenant…</p>

    <template v-else>
      <div v-if="activeSection === 'overview'">
        <section class="section-card">
          <div class="section-card-header">
            <div>
              <h2>{{ vm.tenant.value?.displayName ?? 'Tenant' }}</h2>
              <p class="section-subtitle">/{{ vm.tenant.value?.slug }}</p>
            </div>
            <span :class="vm.tenant.value?.isActive ? 'chip chip-success' : 'chip chip-muted'">
              {{ vm.tenant.value?.isActive ? 'Active' : 'Inactive' }}
            </span>
          </div>

          <div v-if="vm.ssoUnavailableMessage.value" class="section-card-body">
            <p class="muted-hint">{{ vm.ssoUnavailableMessage.value }}</p>
          </div>

          <div class="section-card-body tenant-policy-body">
            <p class="muted-hint">
              Tenant memberships are created when someone signs in through an enabled provider and passes that
              provider's access rules.
            </p>
            <p class="muted-hint">
              Use provider domain restrictions and auto-create settings to control who can join this tenant.
            </p>
            <p v-if="!isEditable" class="muted-hint" data-testid="tenant-readonly-notice">
              The System tenant is managed internally and cannot be changed.
            </p>
            <p v-if="vm.policyError.value" class="error">{{ vm.policyError.value }}</p>
          </div>
        </section>
      </div>

      <div v-if="activeSection === 'clients'">
        <TenantClientsSection :tenant-id="vm.tenantId" />
      </div>

      <template v-if="isEditable">
        <div v-if="activeSection === 'connections'">
          <TenantAiConnectionsSection :tenant-id="vm.tenantId" />
        </div>
        <div v-if="activeSection === 'logical-models'">
          <TenantLogicalModelsSection :tenant-id="vm.tenantId" />
        </div>
        <div v-if="activeSection === 'pricing'">
          <TenantModelCatalogSection :tenant-id="vm.tenantId" />
        </div>
        <div v-if="activeSection === 'compliance'">
          <TenantProviderAllowListSection :tenant-id="vm.tenantId" />
        </div>
      </template>

      <div v-if="activeSection === 'members'">
        <TenantMembersSection />
      </div>

      <div v-if="vm.isTenantSsoAvailable.value && activeSection === 'sso'">
        <TenantSsoSection
          :providers="vm.providers.value"
          :busy-provider-id="vm.deletingProviderId.value"
          :busy="vm.creatingProvider.value"
          :error="vm.providerError.value"
          :redirect-uri="vm.providerRedirectUri.value"
          @create="vm.createProvider"
          @update="vm.updateProvider"
          @delete="vm.removeProvider"
        />
      </div>

      <template v-if="isBudgetingAvailable">
        <div v-if="activeSection === 'budget'">
          <TenantBudgetSection />
        </div>
        <div v-if="activeSection === 'spend'">
          <TenantSpendSection />
        </div>
      </template>
    </template>
  </PageWithSidebar>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { AppNavDrawer, PageWithSidebar } from '@/components'
import { useSession } from '@/composables/useSession'
import TenantAiConnectionsSection from '@/features/tenants/components/TenantAiConnectionsSection.vue'
import TenantBudgetSection from '@/features/tenants/components/TenantBudgetSection.vue'
import TenantClientsSection from '@/features/tenants/components/TenantClientsSection.vue'
import TenantLogicalModelsSection from '@/features/tenants/components/TenantLogicalModelsSection.vue'
import TenantMembersSection from '@/features/tenants/components/TenantMembersSection.vue'
import TenantModelCatalogSection from '@/features/tenants/components/TenantModelCatalogSection.vue'
import TenantProviderAllowListSection from '@/features/tenants/components/TenantProviderAllowListSection.vue'
import TenantSpendSection from '@/features/tenants/components/TenantSpendSection.vue'
import TenantSsoSection from '@/features/tenants/components/TenantSsoSection.vue'
import { useTenantSettingsViewModel } from '@/features/tenants/view-models/useTenantSettingsViewModel'

type TenantSection =
  | 'overview'
  | 'clients'
  | 'connections'
  | 'logical-models'
  | 'pricing'
  | 'members'
  | 'sso'
  | 'compliance'
  | 'budget'
  | 'spend'

interface SidebarEntry {
  section: TenantSection
  label: string
  icon: string
}

interface SidebarGroup {
  title: string
  entries: SidebarEntry[]
  /** Whether this group's sections apply to this tenant at all. */
  available: () => boolean
}

const route = useRoute()
const router = useRouter()
const vm = useTenantSettingsViewModel()

const { getCapability, isCapabilityAvailable } = useSession()
const isBudgetingAvailable = computed(() => isCapabilityAvailable('budgeting'))

// The System tenant owns no connections, catalog or policy of its own, so those sections are not offered for it
// rather than offered and refused.
const isEditable = computed(() => vm.tenant.value?.isEditable !== false)

// A read that failed leaves nothing to configure, so the sections are replaced rather than shown empty.
const loadFailed = computed(() => vm.state.value.status === 'error' && vm.tenant.value === null)

const groups = computed<SidebarGroup[]>(() => [
  {
    title: 'Tenant',
    available: () => true,
    entries: [
      { section: 'overview', label: 'Overview', icon: 'fi-rr-building' },
      { section: 'clients', label: 'Clients', icon: 'fi-rr-users' },
    ],
  },
  {
    title: 'Configuration',
    available: () => isEditable.value,
    entries: [
      { section: 'connections', label: 'Connections', icon: 'fi-rr-plug' },
      { section: 'logical-models', label: 'Logical models', icon: 'fi-rr-cube' },
      { section: 'pricing', label: 'Model pricing overrides', icon: 'fi-rr-dollar' },
    ],
  },
  {
    title: 'Access & compliance',
    available: () => true,
    entries: [
      { section: 'members', label: 'Members', icon: 'fi-rr-user' },
      ...(vm.isTenantSsoAvailable.value
        ? [{ section: 'sso' as TenantSection, label: 'SSO providers', icon: 'fi-rr-shield-check' }]
        : []),
      ...(isEditable.value
        ? [{ section: 'compliance' as TenantSection, label: 'Compliance', icon: 'fi-rr-lock' }]
        : []),
    ],
  },
  {
    title: 'Analytics',
    available: () => isBudgetingAvailable.value,
    entries: [
      { section: 'budget', label: 'Budget', icon: 'fi-rr-dollar' },
      { section: 'spend', label: 'Spend', icon: 'fi-rr-chart-line-up' },
    ],
  },
])

const visibleGroups = computed(() => groups.value.filter(group => group.available() && group.entries.length > 0))

const availableSections = computed(() => visibleGroups.value.flatMap(group => group.entries.map(entry => entry.section)))

const activeSection = ref<TenantSection>(sectionFromRoute())

function sectionFromRoute(): TenantSection {
  const requested = typeof route.query?.section === 'string' ? route.query.section : null
  return requested && availableSections.value.includes(requested as TenantSection)
    ? (requested as TenantSection)
    : 'overview'
}

// The section lives in the URL so a tenant's Members or Budget can be linked to and survives a reload — the same
// contract the client workspace's ?tab= has, and what the old per-section routes gave before they redirected here.
watch(() => route.query?.section, () => {
  activeSection.value = sectionFromRoute()
})

watch(availableSections, (sections) => {
  if (!sections.includes(activeSection.value)) {
    activeSection.value = 'overview'
  }
})

watch(activeSection, (section) => {
  const next = section === 'overview' ? undefined : section
  const current = typeof route.query?.section === 'string' ? route.query.section : undefined
  if (current === next) {
    return
  }

  const query = { ...(route.query ?? {}) }
  if (next) {
    query.section = next
  } else {
    delete query.section
  }

  const navigate = typeof router.replace === 'function' ? router.replace : router.push
  navigate({ query })
})

// Referenced for the capability message the overview shows when SSO is unavailable.
void getCapability
</script>

<style scoped>
.tenant-policy-body {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}
</style>
