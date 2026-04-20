// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { reactive } from 'vue'
import { describe, expect, it } from 'vitest'
import ProviderConnectionForm from '@/components/ProviderConnectionForm.vue'

describe('ProviderConnectionForm', () => {
  it('captures create-mode connection details and emits submit', async () => {
    const form = reactive({
      providerFamily: 'github' as const,
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken' as const,
      oAuthTenantId: '',
      oAuthClientId: '',
      displayName: '',
      secret: '',
      isActive: true,
    })

    const wrapper = mount(ProviderConnectionForm, {
      props: {
        mode: 'create',
        form,
        submitLabel: 'Save Connection',
        busyLabel: 'Saving…',
        submitButtonClass: 'btn-primary provider-create-submit',
      },
    })

    await wrapper.find('input[placeholder="e.g. GitHub Enterprise"]').setValue('GitHub Enterprise')
    await wrapper.find('input[placeholder="https://github.com"]').setValue('https://github.example.com')
    await wrapper.find('input[placeholder="Paste the provider secret"]').setValue('ghp_test')
    await wrapper.get('.provider-create-submit').trigger('click')

    expect(form.displayName).toBe('GitHub Enterprise')
    expect(form.hostBaseUrl).toBe('https://github.example.com')
    expect(form.secret).toBe('ghp_test')
    expect(wrapper.emitted('submit')).toHaveLength(1)
  })

  it('supports edit-mode save and cancel actions', async () => {
    const form = reactive({
      providerFamily: 'github' as const,
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken' as const,
      oAuthTenantId: '',
      oAuthClientId: '',
      displayName: 'GitHub Cloud',
      secret: '',
      isActive: true,
    })

    const wrapper = mount(ProviderConnectionForm, {
      props: {
        mode: 'edit',
        form,
        submitLabel: 'Save Changes',
        busyLabel: 'Saving…',
        showCancel: true,
      },
    })

    expect(wrapper.text()).not.toContain('Provider')
    expect(wrapper.text()).toContain('leave blank to keep current')

    await wrapper.find('input[placeholder="Optional replacement secret"]').setValue('replacement-secret')
    await wrapper.get('.provider-form-submit').trigger('click')
    await wrapper.get('.provider-form-cancel').trigger('click')

    expect(form.secret).toBe('replacement-secret')
    expect(wrapper.emitted('submit')).toHaveLength(1)
    expect(wrapper.emitted('cancel')).toHaveLength(1)
  })

  it('includes Azure DevOps in the shared provider selector', async () => {
    const form = reactive({
      providerFamily: 'github' as const,
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken' as const,
      oAuthTenantId: '',
      oAuthClientId: '',
      displayName: '',
      secret: '',
      isActive: true,
    })

    const wrapper = mount(ProviderConnectionForm, {
      props: {
        mode: 'create',
        form,
        submitLabel: 'Save Connection',
        busyLabel: 'Saving…',
      },
    })

    const providerSelect = wrapper.findAll('select')[0]
    expect(providerSelect.exists()).toBe(true)
    expect(providerSelect.findAll('option').map((option) => option.text())).toEqual([
      'Azure DevOps',
      'GitHub',
      'GitLab',
      'Forgejo',
    ])

    await providerSelect.setValue('azureDevOps')

    expect(form.providerFamily).toBe('azureDevOps')
    expect(wrapper.find('input[placeholder="https://dev.azure.com"]').exists()).toBe(true)
  })

  it('shows Azure DevOps OAuth fields in create mode when Azure DevOps is selected', async () => {
    const form = reactive({
      providerFamily: 'azureDevOps' as const,
      hostBaseUrl: 'https://dev.azure.com',
      authenticationKind: 'oauthClientCredentials' as const,
      oAuthTenantId: '',
      oAuthClientId: '',
      displayName: '',
      secret: '',
      isActive: true,
    })

    const wrapper = mount(ProviderConnectionForm, {
      props: {
        mode: 'create',
        form,
        submitLabel: 'Save Connection',
        busyLabel: 'Saving…',
      },
    })

    expect(wrapper.find('input[placeholder="https://dev.azure.com"]').exists()).toBe(true)
    expect(wrapper.find('input[placeholder="contoso.onmicrosoft.com or tenant GUID"]').exists()).toBe(true)
    expect(wrapper.find('input[placeholder="Azure app registration client ID"]').exists()).toBe(true)

    await wrapper.find('input[placeholder="contoso.onmicrosoft.com or tenant GUID"]').setValue('contoso.onmicrosoft.com')
    await wrapper.find('input[placeholder="Azure app registration client ID"]').setValue('11111111-1111-1111-1111-111111111111')

    expect(form.oAuthTenantId).toBe('contoso.onmicrosoft.com')
    expect(form.oAuthClientId).toBe('11111111-1111-1111-1111-111111111111')
  })
})
