<template>
  <section class="tenant-provider-section">
    <div class="tenant-provider-header">
      <h2>Continue with single sign-on</h2>
      <p>Select the identity provider your tenant configured for sign-in.</p>
    </div>

    <p v-if="providers.length === 0" class="tenant-provider-empty">
      No external providers are enabled for this tenant.
    </p>

    <div v-else class="tenant-provider-list">
      <a
        v-for="provider in providers"
        :key="provider.providerId"
        :data-testid="`tenant-provider-link-${provider.providerId}`"
        :href="buildProviderHref(provider.providerId)"
        class="tenant-provider-link"
      >
        <span class="tenant-provider-brand" :class="`tenant-provider-brand-${providerBrand(provider.providerKind)}`" aria-hidden="true">
          {{ providerIcon(provider.providerKind) }}
        </span>
        <span class="tenant-provider-content">
          <span class="tenant-provider-name">{{ provider.displayName }}</span>
          <span class="tenant-provider-kind">{{ provider.providerLabel }}</span>
        </span>
        <span class="tenant-provider-arrow" aria-hidden="true">&rarr;</span>
      </a>
    </div>
  </section>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router'
import { getTenantExternalChallengeUrl, type TenantLoginProviderDto } from '@/services/tenantAuthService'

const props = defineProps<{
  tenantSlug: string
  providers: TenantLoginProviderDto[]
}>()

const router = useRouter()

function buildProviderHref(providerId: string): string {
  const resolvedCallbackRoute = router.resolve({
    name: 'tenant-login-callback',
    params: { tenantSlug: props.tenantSlug },
  })

  const returnUrl = typeof window === 'undefined'
    ? resolvedCallbackRoute.href
    : new URL(resolvedCallbackRoute.href, window.location.origin).toString()

  return getTenantExternalChallengeUrl(props.tenantSlug, providerId, returnUrl)
}

function providerBrand(providerKind: string): string {
  const normalized = providerKind.trim().replaceAll(' ', '').replaceAll('-', '').toLowerCase()
  if (normalized === 'entraid') {
    return 'microsoft'
  }

  if (normalized === 'github') {
    return 'github'
  }

  if (normalized === 'google') {
    return 'google'
  }

  return 'generic'
}

function providerIcon(providerKind: string): string {
  switch (providerBrand(providerKind)) {
    case 'microsoft':
      return 'M'
    case 'github':
      return 'GH'
    case 'google':
      return 'G'
    default:
      return 'SSO'
  }
}
</script>

<style scoped>
.tenant-provider-section {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.tenant-provider-header {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.tenant-provider-header h2 {
  margin: 0;
  font-size: 1.05rem;
}

.tenant-provider-header p,
.tenant-provider-empty {
  margin: 0;
  color: var(--color-text-muted);
}

.tenant-provider-list {
  display: grid;
  gap: 0.75rem;
}

.tenant-provider-link {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 0.9rem 1rem;
  border-radius: 16px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  text-decoration: none;
  color: var(--color-text);
  background: rgba(255, 255, 255, 0.03);
}

.tenant-provider-brand {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 2.5rem;
  height: 2.5rem;
  border-radius: 0.85rem;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  border: 1px solid rgba(255, 255, 255, 0.08);
}

.tenant-provider-brand-microsoft {
  background: rgba(0, 120, 212, 0.18);
  color: #8ac8ff;
}

.tenant-provider-brand-github {
  background: rgba(255, 255, 255, 0.08);
  color: #f5f5f5;
}

.tenant-provider-brand-google {
  background: rgba(66, 133, 244, 0.18);
  color: #9cc3ff;
}

.tenant-provider-brand-generic {
  background: rgba(255, 255, 255, 0.08);
  color: var(--color-text-muted);
}

.tenant-provider-content {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.2rem;
}

.tenant-provider-link:hover {
  border-color: rgba(255, 255, 255, 0.24);
  background: rgba(255, 255, 255, 0.06);
}

.tenant-provider-name {
  font-weight: 600;
}

.tenant-provider-kind {
  font-size: 0.82rem;
  color: var(--color-text-muted);
}

.tenant-provider-arrow {
  color: var(--color-text-muted);
}
</style>
