<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <header class="app-header">
    <a href="https://meister-dev.ai" target="_blank" rel="noopener noreferrer" class="app-brand">
      <img :src="icon" alt="" aria-hidden="true" class="app-icon"/>
      <div>
        <span class="app-title">ProPR</span>
        <span class="app-subtitle">Admin Console</span>
        <span :class="['app-edition-badge', edition === 'commercial' ? 'app-edition-badge-commercial' : 'app-edition-badge-community']">
          {{ edition === 'commercial' ? 'Commercial' : 'Community' }}
        </span>
      </div>
    </a>
    <nav class="app-nav">
      <RouterLink v-if="canViewClients" :to="{ name: 'clients' }" class="nav-link" :class="{ 'router-link-active': $route.name === 'clients' || $route.name === 'client-detail' }"><i class="fi fi-rr-users"></i> Clients</RouterLink>
      <RouterLink :to="{ name: 'reviews' }" class="nav-link" :class="{ 'router-link-active': $route.name === 'reviews' || $route.name === 'job-protocol' || $route.name === 'pr-review' }"><i class="fi fi-rr-search"></i> Reviews</RouterLink>
      <div v-if="hasAnyAdministrationAccess" class="nav-dropdown" @mouseenter="adminDropdownOpen = true" @mouseleave="adminDropdownOpen = false">
        <button class="nav-link dropdown-toggle" :class="{ 'router-link-active': $route.name === 'tenant-directory' || $route.name === 'tenant-settings' || $route.name === 'tenant-members' || $route.name === 'users' || $route.name === 'thread-memory' || $route.name === 'provider-settings' || $route.name === 'licensing' }" @click="adminDropdownOpen = !adminDropdownOpen">
          <i class="fi fi-rr-shield-check"></i> Administration
          <i class="fi fi-rr-angle-small-down ml-1 text-xs"></i>
        </button>
        <div v-if="adminDropdownOpen" class="dropdown-menu">
          <RouterLink v-if="defaultTenantAdminRoute" :to="defaultTenantAdminRoute" class="dropdown-item" :class="{ 'active': $route.name === 'tenant-directory' || $route.name === 'tenant-settings' || $route.name === 'tenant-members' }" @click="adminDropdownOpen = false"><i class="fi fi-rr-building"></i> Tenants</RouterLink>
          <RouterLink v-if="isAdmin" :to="{ name: 'licensing' }" class="dropdown-item" :class="{ 'active': $route.name === 'licensing' }" @click="adminDropdownOpen = false"><i class="fi fi-rr-badge"></i> Licensing</RouterLink>
          <RouterLink v-if="isAdmin" :to="{ name: 'provider-settings' }" class="dropdown-item" :class="{ 'active': $route.name === 'provider-settings' }" @click="adminDropdownOpen = false"><i class="fi fi-rr-plug-connection"></i> SCM Providers</RouterLink>
          <RouterLink v-if="isAdmin" :to="{ name: 'users' }" class="dropdown-item" :class="{ 'active': $route.name === 'users' }" @click="adminDropdownOpen = false"><i class="fi fi-rr-user"></i> Users</RouterLink>
          <RouterLink v-if="isAdmin" :to="{ name: 'thread-memory' }" class="dropdown-item" :class="{ 'active': $route.name === 'thread-memory' }" @click="adminDropdownOpen = false"><i class="fi fi-rr-brain"></i> Memory</RouterLink>
        </div>
      </div>
    </nav>
    <div class="header-actions">
      <RouterLink :to="{ name: 'settings' }" class="nav-link nav-link-button" :class="{ 'router-link-active': $route.name === 'settings' }">
        <i class="fi fi-rr-settings"></i>
        <span>Settings</span>
      </RouterLink>
      <button class="logout-btn" @click="logout" aria-label="Logout">
        <i class="fi fi-rr-exit"></i>
        <span>Logout</span>
      </button>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useSession } from '@/composables/useSession'
import icon from '@/assets/logo_standalone.png'

const router = useRouter()
const { clearTokens, isAdmin, clientRoles, tenantRoles, edition } = useSession()

/** True for global admins or users with any visible client role. */
const canViewClients = computed(
  () => isAdmin.value || Object.values(clientRoles.value).some((r) => r >= 0),
)

const canAccessTenantAdministration = computed(
  () => edition.value !== 'community' && (isAdmin.value || Object.values(tenantRoles.value).some((role) => role >= 1)),
)

const hasAnyTenantAdminRole = computed(
  () => isAdmin.value || Object.values(tenantRoles.value).some((role) => role >= 1),
)

const hasAnyAdministrationAccess = computed(
  () => isAdmin.value || canAccessTenantAdministration.value,
)

const defaultTenantAdminRoute = computed(() => {
  return canAccessTenantAdministration.value ? { name: 'tenant-directory' } : null
})

const adminDropdownOpen = ref(false)

function logout() {
  clearTokens()
  router.push({ name: 'login' })
}
</script>

<style scoped>
.app-nav {
  display: flex;
  gap: 2rem;
  align-items: center;
  justify-content: center;
  flex: 1;
  margin: 0 2rem;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.nav-link {
  color: var(--color-text);
  text-decoration: none;
  font-size: 0.95rem;
  font-weight: 500;
  opacity: 0.7;
  transition: all 0.2s;
  padding: 0.5rem 0;
  border-bottom: 2px solid transparent;
}

.nav-link:hover,
.nav-link.router-link-active {
  opacity: 1;
  color: var(--color-text);
  border-bottom-color: var(--color-accent);
}

.nav-link-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 0.75rem;
  padding: 0.55rem 0.9rem;
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-weight: 600;
  font-size: 0.95rem;
  opacity: 1;
}

.nav-link-button.router-link-active,
.nav-link-button:hover {
  border-bottom-color: transparent;
  border-color: rgba(34, 211, 238, 0.3);
  background: rgba(34, 211, 238, 0.08);
}

.nav-link-button i {
  color: var(--color-accent);
}

.logout-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 0.75rem;
  padding: 0.55rem 0.9rem;
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-weight: 600;
  font-size: 0.95rem;
  transition: background-color 0.2s ease, border-color 0.2s ease, color 0.2s ease, transform 0.2s ease;
}

.logout-btn i {
  font-size: 1rem;
  color: var(--color-danger);
}

.logout-btn:hover {
  background: rgba(239, 68, 68, 0.08);
  border-color: rgba(239, 68, 68, 0.28);
  color: var(--color-text);
  transform: translateY(-1px);
}

.app-title {
  display: block;
  font-size: 1.4rem;
  font-weight: 800;
  line-height: 1;
  letter-spacing: -0.04em;
  color: var(--color-text);
}

.app-subtitle {
  display: block;
  margin-top: 0.18rem;
  font-size: 0.7rem;
  color: var(--color-text-muted);
  letter-spacing: 0.12em;
  text-transform: uppercase;
  font-weight: 700;
}

.app-edition-badge {
  display: inline-flex;
  align-items: center;
  margin-top: 0.4rem;
  padding: 0.2rem 0.55rem;
  border-radius: 999px;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.04em;
}

.app-edition-badge-community {
  background: rgba(148, 163, 184, 0.16);
  color: var(--color-text-muted);
}

.app-edition-badge-commercial {
  background: rgba(34, 197, 94, 0.16);
  color: var(--color-success);
}

/* Dropdown */
.nav-dropdown {
  position: relative;
  display: flex;
  align-items: center;
}

.dropdown-toggle {
  background: none;
  border: none;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-family: inherit;
}

.dropdown-menu {
  position: absolute;
  top: 100%;
  left: 50%;
  transform: translateX(-50%);
  margin-top: 0.5rem;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 0.75rem;
  padding: 0.5rem;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 160px;
  box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2);
  z-index: 50;
  animation: dropdown-fade-in 0.2s cubic-bezier(0.16, 1, 0.3, 1);
}

/* Invisible bridge so hover doesn't break when moving cursor to dropdown */
.nav-dropdown::after {
  content: '';
  position: absolute;
  top: 100%;
  left: 0;
  right: 0;
  height: 0.75rem;
  z-index: 10;
}

@keyframes dropdown-fade-in {
  from { opacity: 0; transform: translate(-50%, -10px); }
  to { opacity: 1; transform: translate(-50%, 0); }
}

.dropdown-item {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.75rem;
  color: var(--color-text);
  text-decoration: none;
  border-radius: 0.5rem;
  font-size: 0.9rem;
  font-weight: 500;
  transition: all 0.2s;
  opacity: 0.8;
}

.dropdown-item i {
  font-size: 1.1rem;
  opacity: 0.7;
}

.dropdown-item:hover, .dropdown-item.active {
  background: rgba(34, 211, 238, 0.08);
  color: var(--color-accent);
  opacity: 1;
}

.dropdown-item:hover i, .dropdown-item.active i {
  opacity: 1;
}

.ml-1 {
  margin-left: 0.25rem;
}

.text-xs {
  font-size: 0.75rem;
}
</style>
