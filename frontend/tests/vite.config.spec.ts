import { describe, expect, it } from 'vitest'
import { rewriteApiProxyPath } from '../vite.config'

describe('rewriteApiProxyPath', () => {
  it('preserves tenant admin endpoints that are already rooted at /api', () => {
    expect(rewriteApiProxyPath('/api/admin/tenants')).toBe('/api/admin/tenants')
    expect(rewriteApiProxyPath('/api/admin/tenants/tenant-1')).toBe('/api/admin/tenants/tenant-1')
    expect(rewriteApiProxyPath('/api/admin/tenants/tenant-1/memberships')).toBe('/api/admin/tenants/tenant-1/memberships')
  })

  it('rewrites non-tenant endpoints to match backend routing', () => {
    expect(rewriteApiProxyPath('/api/clients')).toBe('/clients')
    expect(rewriteApiProxyPath('/api/auth/me')).toBe('/auth/me')
    expect(rewriteApiProxyPath('/api/admin/providers')).toBe('/admin/providers')
  })
})
