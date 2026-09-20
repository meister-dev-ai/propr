// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  dispatchProviderAction,
  listAiConnections,
  listPermittedProviders,
  readProviderActionInvocation,
  submitProviderActionValues,
} from '@/services/aiConnectionsService'
import type {
  AiConnectionDto,
  AiDeclaredActionDto,
  AiProviderActionInvocationDto,
} from '@/services/aiConnectionsService'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

vi.mock('@/services/aiConnectionsService', async () => {
  const actual = await vi.importActual<typeof import('@/services/aiConnectionsService')>(
    '@/services/aiConnectionsService',
  )

  return {
    ApiFieldValidationError: actual.ApiFieldValidationError,
    listAiConnections: vi.fn(),
    createAiConnection: vi.fn(),
    updateAiConnection: vi.fn(),
    discoverAiModels: vi.fn(),
    probeAiConnection: vi.fn(),
    listPermittedProviders: vi.fn(),
    verifyAiConnection: vi.fn(),
    activateAiConnection: vi.fn(),
    deactivateAiConnection: vi.fn(),
    deleteAiConnection: vi.fn(),
    dispatchProviderAction: vi.fn(),
    submitProviderActionValues: vi.fn(),
    readProviderActionInvocation: vi.fn(),
  }
})

// A family this console has never seen, described only by what the server says it declares.
const connectAction: AiDeclaredActionDto = {
  addInKey: 'meisterdev/example',
  id: 'connect',
  label: 'Connect account',
  inputs: [
    {
      name: 'callbackUrl',
      label: 'Callback URL',
      kind: 'string',
      isRequired: false,
      isSecret: false,
      isComputed: false,
    },
  ],
}

const disconnectAction: AiDeclaredActionDto = {
  addInKey: 'meisterdev/example',
  id: 'disconnect',
  label: 'Disconnect account',
  inputs: [],
}

const connection = (
  action: AiDeclaredActionDto = connectAction,
  offeredActionIds: string[] = [action.id ?? ''],
): AiConnectionDto =>
  ({
    id: 'conn-1',
    displayName: 'Example',
    providerKind: 'openAiCompatible',
    baseUrl: 'https://api.example.com/v1',
    authMode: 'apiKey',
    discoveryMode: 'manualOnly',
    isActive: true,
    configuredModels: [],
    purposeBindings: [],
    verification: { status: 'neverVerified' },
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    declaredFields: [],
    computedFields: [],
    declaredActions: [action],
    offeredActionIds,
  }) as unknown as AiConnectionDto

const connectable = (offeredActionIds: string[]): AiConnectionDto =>
  ({
    ...connection(),
    declaredActions: [connectAction, disconnectAction],
    offeredActionIds,
  }) as unknown as AiConnectionDto

const pending = (waitingFor: string): AiProviderActionInvocationDto =>
  ({
    id: 'run-1',
    connectionId: 'conn-1',
    addInKey: 'meisterdev/example',
    actionId: 'connect',
    state: 'Pending',
    waitingFor,
    expiresAt: new Date(Date.now() + 600_000).toISOString(),
  }) as unknown as AiProviderActionInvocationDto

let api!: ReturnType<typeof useClientAiConnectionsTab>

async function mountComposable() {
  mount(
    defineComponent({
      setup() {
        api = useClientAiConnectionsTab({ clientId: 'c1' })
        return () => h('div')
      },
    }),
  )
  await flushPromises()
}

describe('the actions a provider family declares', () => {
  beforeEach(async () => {
    vi.mocked(listAiConnections).mockResolvedValue([connection()])
    vi.mocked(listPermittedProviders).mockResolvedValue({ isRestricted: false, providers: [] })
    vi.mocked(dispatchProviderAction).mockReset()
    vi.mocked(submitProviderActionValues).mockReset()
    vi.mocked(readProviderActionInvocation).mockReset()
    await mountComposable()
  })

  afterEach(() => {
    api.closeDeclaredAction()
    vi.useRealTimers()
  })

  it('offers one affordance per declared action, taking its label from the declaration', () => {
    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.label)).toEqual([
      'Connect account',
    ])
  })

  // The declaration says which operations the family has; the server says which of them this connection is in a
  // state for. A family whose credential is written by its own sign-in declares both and offers one at a time.
  it('renders only the declared actions this connection is offered', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([connectable(['disconnect'])])
    await mountComposable()

    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.id)).toEqual(['disconnect'])
  })

  it('renders no affordance for a connection offered none of what its family declares', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([connectable([])])
    await mountComposable()

    expect(api.declaredActionsFor(api.profiles.value[0])).toEqual([])
  })

  // The point of the whole arrangement: finishing a sign-in changes which buttons are rendered, without the
  // operator reloading the page. The list is re-read when the run reaches a terminal state, and the connection
  // that comes back is offered the disconnect where it was offered the sign-in.
  it('re-reads the connections when a run reaches a terminal state, so the affordances change', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([connectable(['connect'])])
    await mountComposable()
    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.id)).toEqual(['connect'])

    vi.mocked(listAiConnections).mockResolvedValue([connectable(['disconnect'])])
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: { ...pending('Waiting.'), state: 'Completed', message: 'Connected.' },
      result: { kind: 'completed', message: 'Connected.' },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.id)).toEqual(['disconnect'])
  })

  // A sign-in finishes at the vendor long after the dispatch returned, so the change of affordances has to
  // follow the poll as well as the call that started the run.
  it('re-reads the connections when a polled run reaches a terminal state', async () => {
    vi.useFakeTimers()
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(listAiConnections).mockResolvedValue([connectable(['connect'])])
    await mountComposable()

    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)
    vi.mocked(readProviderActionInvocation).mockResolvedValue({
      ...pending('Waiting.'),
      state: 'Completed',
      message: 'Connected.',
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)
    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.id)).toEqual(['connect'])

    vi.mocked(listAiConnections).mockResolvedValue([connectable(['disconnect'])])
    await vi.advanceTimersByTimeAsync(3000)

    expect(api.declaredActionsFor(api.profiles.value[0]).map((action) => action.id)).toEqual(['disconnect'])
  })

  it('sends the family key back exactly as the server reported it', async () => {
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: { ...pending('Waiting.'), state: 'Completed', message: 'Connected.' },
      result: { kind: 'completed', message: 'Connected.' },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(dispatchProviderAction).toHaveBeenCalledWith('conn-1', 'meisterdev/example', 'connect')
    expect(api.actionRun.value?.result?.kind).toBe('completed')
  })

  // An operator whose deployment cannot satisfy the family's requirement reads it before anything runs, rather
  // than finding out through a run that expires.
  it('states the requirement before the action starts when the family declared one', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null)
    const coLocated = { ...connectAction, coLocationNotice: 'Start this from the machine running the host.' }

    await api.openDeclaredAction(api.profiles.value[0], coLocated)

    expect(dispatchProviderAction).not.toHaveBeenCalled()
    expect(api.actionRun.value?.action.coLocationNotice).toBe(
      'Start this from the machine running the host.',
    )

    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)

    await api.startDeclaredAction()

    expect(dispatchProviderAction).toHaveBeenCalledOnce()
  })

  // The address was written by the provider family. Assigning one to this window would run a script address in
  // the administration origin, so it is opened in a tab of its own with the opener detached.
  it('opens an address in a new tab with the opener detached', async () => {
    const opened = vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(opened).toHaveBeenCalledWith('https://auth.example.com/authorize', '_blank', 'noopener,noreferrer')
    opened.mockRestore()
  })

  // The fallback every flow keeps: an operator whose browser is somewhere else finishes by pasting.
  it('offers the action inputs once the operator has been sent to the provider', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(api.actionFormFields.value.map((field) => field.name)).toEqual(['callbackUrl'])
  })

  it('renders the fields a form result asked for and continues the same run when they are submitted', async () => {
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the values.'),
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'callbackUrl',
            label: 'Callback URL',
            kind: 'string',
            isRequired: true,
            isSecret: false,
            isComputed: false,
          },
        ],
      },
    } as never)
    vi.mocked(submitProviderActionValues).mockResolvedValue({
      invocation: { ...pending('Waiting.'), state: 'Completed', message: 'Connected.' },
      result: { kind: 'completed', message: 'Connected.' },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)
    expect(api.actionFormFields.value.map((field) => field.name)).toEqual(['callbackUrl'])

    api.actionRun.value!.values.callbackUrl = 'https://auth.example.com/cb?code=abc'
    await api.submitDeclaredActionValues()

    expect(submitProviderActionValues).toHaveBeenCalledWith('run-1', {
      callbackUrl: 'https://auth.example.com/cb?code=abc',
    })
    expect(api.actionRun.value?.invocation?.state).toBe('Completed')
  })

  // A checkbox renders from the value under its name, so a boolean declared true has to be in the run's values
  // before the form is shown. Otherwise the box the family declared as ticked renders unticked, and an operator
  // who leaves it alone submits no value while one who ticks and unticks it submits "false".
  it('opens a form field on the default the family declared for it', async () => {
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the values.'),
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'reuseExistingGrant',
            label: 'Reuse the existing grant',
            kind: 'bool',
            isRequired: false,
            isSecret: false,
            isComputed: false,
            defaultValue: 'true',
          },
        ],
      },
    } as never)
    vi.mocked(submitProviderActionValues).mockResolvedValue({
      invocation: { ...pending('Waiting.'), state: 'Completed', message: 'Connected.' },
      result: { kind: 'completed', message: 'Connected.' },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(api.actionRun.value?.values.reuseExistingGrant).toBe('true')

    await api.submitDeclaredActionValues()

    expect(submitProviderActionValues).toHaveBeenCalledWith('run-1', { reuseExistingGrant: 'true' })
  })

  // The fallback form is the action's own declared inputs, shown once the operator has been sent to the
  // provider. It is rendered by the same fields and needs the same defaults.
  it('opens the fallback form on the defaults the action declared for its inputs', async () => {
    const action: AiDeclaredActionDto = {
      ...connectAction,
      inputs: [
        {
          name: 'reuseExistingGrant',
          label: 'Reuse the existing grant',
          kind: 'bool',
          isRequired: false,
          isSecret: false,
          isComputed: false,
          defaultValue: 'true',
        },
      ],
    }

    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(listAiConnections).mockResolvedValue([connection(action)])
    await mountComposable()

    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/start', awaitCompletion: true },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], action)

    expect(api.actionRun.value?.values.reuseExistingGrant).toBe('true')
  })

  it('shows what a failed result said', async () => {
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: { ...pending('Waiting.'), state: 'Failed', message: 'The provider refused.' },
      result: { kind: 'failed', message: 'The provider refused.' },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    expect(api.actionRun.value?.result?.kind).toBe('failed')
    expect(api.actionRun.value?.result?.message).toBe('The provider refused.')
  })

  it('polls an open run until it reaches a terminal state and then stops', async () => {
    vi.useFakeTimers()
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)
    vi.mocked(readProviderActionInvocation)
      .mockResolvedValueOnce(pending('Waiting for the provider.'))
      .mockResolvedValueOnce({
        ...pending('Waiting.'),
        state: 'Completed',
        message: 'Connected.',
      } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)

    await vi.advanceTimersByTimeAsync(3000)
    expect(readProviderActionInvocation).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(3000)
    expect(api.actionRun.value?.invocation?.state).toBe('Completed')

    // Terminal, so nothing is read again however long the page stays open.
    await vi.advanceTimersByTimeAsync(30_000)
    expect(readProviderActionInvocation).toHaveBeenCalledTimes(2)
  })

  it('shows the host diagnosis on a run that ran out of its window', async () => {
    vi.useFakeTimers()
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider to reach port 1455 on this machine.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)
    vi.mocked(readProviderActionInvocation).mockResolvedValue({
      ...pending('Waiting for the provider to reach port 1455 on this machine.'),
      state: 'Expired',
      message: 'Waiting for the provider to reach port 1455 on this machine.',
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)
    await vi.advanceTimersByTimeAsync(3000)

    expect(api.actionRun.value?.invocation?.state).toBe('Expired')
    expect(api.actionRun.value?.invocation?.message).toContain('1455')
  })

  // One run is held at a time. Starting a second while the first is open would leave the first pending at the
  // host with nothing polling it, and its answer would arrive with nothing watching for it.
  it('refuses to start a second operation while one is still open', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null)
    vi.mocked(dispatchProviderAction).mockResolvedValue({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    } as never)

    await api.openDeclaredAction(api.profiles.value[0], connectAction)
    expect(api.isActionRunActive.value).toBe(true)
    expect(dispatchProviderAction).toHaveBeenCalledOnce()

    await api.openDeclaredAction(api.profiles.value[0], {
      ...connectAction,
      id: 'disconnect',
      label: 'Disconnect account',
    })

    expect(dispatchProviderAction).toHaveBeenCalledOnce()
    expect(api.actionRun.value?.action.id).toBe('connect')

    api.closeDeclaredAction()
    expect(api.isActionRunActive.value).toBe(false)
  })

  // A dispatch outlives the run it came from. One whose run the operator has closed opens no tab: the address
  // was written by the provider family and the operator is no longer expecting to be sent anywhere.
  it('drops a dispatch whose run the operator has already closed', async () => {
    const opened = vi.spyOn(window, 'open').mockReturnValue(null)

    let answer: (value: unknown) => void = () => {}
    vi.mocked(dispatchProviderAction).mockReturnValue(
      new Promise((resolve) => {
        answer = resolve
      }) as never,
    )

    const starting = api.openDeclaredAction(api.profiles.value[0], connectAction)
    api.closeDeclaredAction()

    answer({
      invocation: pending('Waiting for the provider.'),
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    })
    await starting
    await flushPromises()

    expect(opened).not.toHaveBeenCalled()
    expect(api.actionRun.value).toBeNull()
    opened.mockRestore()
  })
})
