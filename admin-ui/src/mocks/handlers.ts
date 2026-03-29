import { http, HttpResponse, delay } from 'msw'
import protocolMockData from '../../mock/data/protocol_response_1.json'

const base = import.meta.env.VITE_API_BASE_URL ?? ''

let jobTick = 0

let crawlConfigs = [
  {
    id: 'config-1',
    clientId: '1',
    organizationUrl: 'https://dev.azure.com/meister-propr',
    projectId: 'Meister-ProPR',
    crawlIntervalSeconds: 60,
    isActive: true,
    createdAt: '2024-03-27T10:00:00Z',
    updatedAt: '2024-03-27T10:00:00Z'
  },
  {
    id: 'config-2',
    clientId: '2',
    organizationUrl: 'https://dev.azure.com/cloud-native',
    projectId: 'Infrastructure',
    crawlIntervalSeconds: 300,
    isActive: false,
    createdAt: '2024-03-27T11:00:00Z',
    updatedAt: '2024-03-27T11:30:00Z'
  }
]

let dismissedFindings = [
  {
    id: 'd1',
    clientId: '1',
    patternText: 'postgres uses hardcoded credentials postgrespassword devpass ensure this compose file is strictly for development/test',
    label: 'False positive: dev credentials',
    createdAt: new Date(Date.now() - 86400000).toISOString()
  },
  {
    id: 'd2',
    clientId: '1',
    patternText: 'Potential use of insecure industrial protocol (Modbus/TCP) without TLS encryption layer in the communication stack.',
    label: 'Intentional: Legacy support',
    createdAt: new Date(Date.now() - 172800000).toISOString()
  }
]

let promptOverrides = [
  {
    id: 'o1',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'SystemPrompt',
    overrideText: 'You are an expert code reviewer specialising in .NET/C# and general cloud-native architecture. Prioritize security and naming consistency.',
    createdAt: new Date(Date.now() - 86400000 * 2).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 2).toISOString()
  },
  {
    id: 'o2',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'AgenticLoopGuidance',
    overrideText: 'When reviewing Bicep files, always check for resource naming best practices and ensure identity-based access is used over connection strings.',
    createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 3).toISOString()
  }
]

export const handlers = [
  http.post(`${base}/auth/login`, async () => {
    await delay(500)
    // The dummy token contains a base64 payload: {"global_role":"Admin"}
    // This allows useSession to resolve isAdmin === true.
    return HttpResponse.json({ 
      accessToken: 'dummyHeader.eyJnbG9iYWxfcm9sZSI6IkFkbWluIn0=.dummySignature',
      refreshToken: 'mock-refresh'
    })
  }),

  http.post(`${base}/auth/refresh`, async () => {
    return HttpResponse.json({ accessToken: 'mock-access' })
  }),

  http.get(`${base}/clients`, async () => {
    await delay(300)
    return HttpResponse.json([
      { id: '1', displayName: 'Acme Corp', isActive: true, hasAdoCredentials: true, createdAt: new Date().toISOString() },
      { id: '2', displayName: 'Globex Inc', isActive: false, hasAdoCredentials: false, createdAt: new Date().toISOString() },
      { id: '3', displayName: 'Umbrella Corp', isActive: true, hasAdoCredentials: true, createdAt: new Date().toISOString() }
    ])
  }),

  http.get(`${base}/clients/:id`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json({
        id: params.id, 
        displayName: 'Mocked Client ' + params.id, 
        isActive: true, 
        hasAdoCredentials: true, 
        createdAt: new Date().toISOString(), 
        reviewerId: '0000-1111-2222-3333'
    })
  }),
  
  http.patch(`${base}/clients/:id`, async ({ request }) => {
    await delay(300)
    const body = await request.json() as any
    return HttpResponse.json({
        id: '1', displayName: body.displayName ?? 'Mocked Client', isActive: body.isActive ?? true, hasAdoCredentials: true, createdAt: new Date().toISOString()
    })
  }),

  http.get(`${base}/admin/users`, async () => {
    await delay(400)
    return HttpResponse.json([
      { id: 'u1', username: 'admin', globalRole: 'Admin', isActive: true, createdAt: new Date().toISOString() },
      { id: 'u2', username: 'jsmith', globalRole: 'User', isActive: true, createdAt: new Date().toISOString() }
    ])
  }),

  http.get(`${base}/admin/users/:id`, async () => {
    return HttpResponse.json({
       assignments: [
         { assignmentId: 'a1', clientId: '1', role: 'ClientAdministrator', assignedAt: new Date().toISOString() },
         { assignmentId: 'a2', clientId: '2', role: 'ClientUser', assignedAt: new Date().toISOString() }
       ]
    })
  }),

  http.get(`${base}/users/me/pats`, async () => {
    await delay(200)
    return HttpResponse.json([
      { id: 'p1', label: 'CI Pipeline', createdAt: new Date().toISOString(), lastUsedAt: new Date().toISOString(), expiresAt: null, isRevoked: false },
      { id: 'p2', label: 'Local Dev Proxy', createdAt: new Date().toISOString(), lastUsedAt: null, expiresAt: new Date(Date.now() + 86400000).toISOString(), isRevoked: false }
    ])
  }),

  http.post(`${base}/users/me/pats`, async () => {
    await delay(300)
    return HttpResponse.json({ token: 'mock-pat-' + Math.random().toString(36).substring(7) })
  }),

  http.get(`${base}/jobs`, async () => {
    await delay(400)
    jobTick++
    
    const isCompleted = false
    
    return HttpResponse.json({
      items: [
        {
          id: 'job-123',
          projectId: 'proj-x',
          repositoryId: 'backend-service',
          pullRequestId: 42,
          organizationUrl: 'https://dev.azure.com/acme',
          status: 'completed',
          iterationId: 2,
          submittedAt: new Date(Date.now() - 86400000).toISOString(),
          processingStartedAt: new Date(Date.now() - 86000000).toISOString(),
          completedAt: new Date(Date.now() - 85000000).toISOString(),
          totalInputTokens: 12000,
          totalOutputTokens: 850,
          resultSummary: 'Found 3 minor issues. Suggested improvements.',
          prTitle: 'feat: Add authentication middleware',
          prRepositoryName: 'backend-service',
          prSourceBranch: 'feature/auth-middleware',
          prTargetBranch: 'main',
          aiModel: 'claude-opus-4-5'
        },
        {
          id: 'job-124',
          projectId: 'proj-y',
          repositoryId: 'frontend-app',
          pullRequestId: 89,
          organizationUrl: 'https://dev.azure.com/acme',
          status: isCompleted ? 'completed' : 'processing',
          iterationId: Math.ceil(jobTick / 2) || 1,
          submittedAt: new Date(Date.now() - 1000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 500000).toISOString(),
          completedAt: isCompleted ? new Date().toISOString() : null,
          totalInputTokens: 5000 + (jobTick * 200),
          totalOutputTokens: jobTick * 100,
          resultSummary: isCompleted 
            ? 'Automated review finished. LGTM!' 
            : `Evaluating subjob ${jobTick}: src/components/Component${Math.ceil(jobTick/2)}.vue`,
          prTitle: 'refactor: Migrate to Composition API',
          prRepositoryName: 'frontend-app',
          prSourceBranch: 'refactor/composition-api',
          prTargetBranch: 'develop',
          aiModel: 'gpt-4o'
        },
        {
          id: 'job-125',
          projectId: 'proj-z',
          repositoryId: 'infrastructure',
          pullRequestId: 12,
          organizationUrl: 'https://dev.azure.com/acme',
          status: 'failed',
          iterationId: 1,
          submittedAt: new Date(Date.now() - 200000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 190000000).toISOString(),
          completedAt: new Date(Date.now() - 180000000).toISOString(),
          errorMessage: 'Failed to access ADO repository due to expired token.',
          prTitle: 'chore: Update Terraform modules',
          prRepositoryName: 'infrastructure',
          prSourceBranch: 'chore/terraform-update',
          prTargetBranch: 'main',
          aiModel: 'gemini-2.5-pro'
        }
      ]
    })
  }),

  http.get(`${base}/Reviews/:jobId`, async ({ params }) => {
    await delay(300)
    
    const jobId = params.jobId as string
    
    // Simulating "No Synthesis" for a failed job
    if (jobId === 'job-125') {
        return HttpResponse.json({
            jobId,
            status: 'failed',
            result: null
        })
    }

    // Simulating "In Progress" for a processing job
    if (jobId === 'job-124') {
        const isSynthesizing = jobTick > 8 // Lets say it synthesizes after 4 file reviews
        return HttpResponse.json({
            jobId,
            status: 'processing',
            result: isSynthesizing ? {
                summary: "Partial summary: The review is ongoing...",
                comments: []
            } : null
        })
    }
    
    // Provide a mocked synthesis review result for others (completed)
    return HttpResponse.json({
        jobId,
        status: 'completed',
        result: {
            summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
            comments: [
                {
                    filePath: "/.azure/modules/network.bicep",
                    lineNumber: 7,
                    severity: "suggestion",
                    message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
                },
                {
                    filePath: "/.azure/modules/containerApps.bicep",
                    lineNumber: 101,
                    severity: "error",
                    message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
                },
                {
                    filePath: "/.azure/modules/containerEnvironment.bicep",
                    lineNumber: 10,
                    severity: "warning",
                    message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
                }
            ]
        }
    })
  }),

  // Admin-authenticated result endpoint (used by management UI instead of /Reviews/:jobId)
  http.get(`${base}/jobs/:id/result`, async ({ params }) => {
    await delay(300)

    const id = params.id as string

    if (id === 'job-125') {
      return new HttpResponse(null, { status: 404 })
    }

    if (id === 'job-124') {
      const isSynthesizing = jobTick > 8
      if (!isSynthesizing) return new HttpResponse(null, { status: 404 })
      return HttpResponse.json({
        jobId: id,
        status: 'processing',
        submittedAt: new Date(Date.now() - 1000000).toISOString(),
        completedAt: null,
        result: {
          summary: "Partial summary: The review is ongoing...",
          comments: []
        }
      })
    }

    return HttpResponse.json({
      jobId: id,
      status: 'completed',
      submittedAt: new Date(Date.now() - 86400000).toISOString(),
      completedAt: new Date(Date.now() - 85000000).toISOString(),
      result: {
        summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
        comments: [
          {
            filePath: "/.azure/modules/network.bicep",
            lineNumber: 7,
            severity: "suggestion",
            message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
          },
          {
            filePath: "/.azure/modules/containerApps.bicep",
            lineNumber: 101,
            severity: "error",
            message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
          },
          {
            filePath: "/.azure/modules/containerEnvironment.bicep",
            lineNumber: 10,
            severity: "warning",
            message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
          }
        ]
      }
    })
  }),

  http.get(`${base}/jobs/:id/protocol`, async ({ params }) => {
    await delay(600)
    
    if (params.id === 'job-124') {
        const events = []
        const currentTick = Math.min(jobTick, 8)
        
        for (let i = 1; i <= currentTick; i++) {
            // Odd ticks generate a fresh ToolCall
            events.push({
                id: `e${i}_call`,
                occurredAt: new Date(Date.now() - 500000 + i * 1500).toISOString(),
                kind: 'ToolCall',
                name: 'AnalyzeCodeChunk',
                inputTokens: 500, outputTokens: 0,
                inputTextSample: `function execute() {\n  return "processing chunk ${Math.ceil(i/2)}";\n}`,
                outputSummary: null
            })
            // Even ticks "answer" the previous call with a ToolResult
            if (i % 2 === 0) {
                events.push({
                    id: `e${i}_result`,
                    occurredAt: new Date(Date.now() - 500000 + i * 1500 + 800).toISOString(),
                    kind: 'ToolResult',
                    name: 'AnalyzeCodeChunk',
                    inputTokens: 0, outputTokens: 100,
                    inputTextSample: null,
                    outputSummary: `Analysis complete. Chunk ${i/2} is clean and optimal.`
                })
            }
        }
        
        const isCompleted = false
        return HttpResponse.json([
          {
            id: 'pass124',
            jobId: 'job-124',
            label: `src/components/Component${Math.ceil(currentTick/2)}.vue`,
            startedAt: new Date(Date.now() - 500000).toISOString(),
            completedAt: isCompleted ? new Date().toISOString() : null,
            outcome: isCompleted ? 'Success' : 'Processing',
            iterationCount: Math.ceil(currentTick / 2),
            toolCallCount: Math.floor(currentTick / 2),
            finalConfidence: isCompleted ? 99 : null,
            totalInputTokens: 5000 + (currentTick * 200),
            totalOutputTokens: currentTick * 100,
            events
          }
        ])
    }

    return HttpResponse.json(protocolMockData)
  }),

  // Crawl Configurations
  http.get(`${base}/admin/crawl-configurations`, async () => {
    await delay(400)
    return HttpResponse.json(crawlConfigs)
  }),

  http.post(`${base}/admin/crawl-configurations`, async ({ request }) => {
    await delay(600)
    const body = await request.json() as any
    const newConfig = {
      id: `config-${Math.random().toString(36).substr(2, 9)}`,
      ...body,
      isActive: true,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    crawlConfigs.unshift(newConfig)
    return HttpResponse.json(newConfig, { status: 201 })
  }),

  http.patch(`${base}/admin/crawl-configurations/:configId`, async ({ params, request }) => {
    await delay(500)
    const { configId } = params
    const body = await request.json() as any
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })
    
    crawlConfigs[idx] = { 
      ...crawlConfigs[idx], 
      ...body, 
      updatedAt: new Date().toISOString() 
    }
    return HttpResponse.json(crawlConfigs[idx])
  }),

  http.delete(`${base}/admin/crawl-configurations/:configId`, async ({ params }) => {
    await delay(400)
    const { configId } = params
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })
    
    crawlConfigs.splice(idx, 1)
    return new HttpResponse(null, { status: 204 })
  }),

  // Dismissals
  http.get(`${base}/clients/:clientId/dismissals`, async () => {
    await delay(300)
    return HttpResponse.json(dismissedFindings)
  }),

  http.post(`${base}/clients/:clientId/dismissals`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `d-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      patternText: body.originalMessage,
      label: body.label,
      createdAt: new Date().toISOString()
    }
    dismissedFindings.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/dismissals/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    dismissedFindings = dismissedFindings.filter(d => d.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  // Prompt Overrides
  http.get(`${base}/clients/:clientId/prompt-overrides`, async () => {
    await delay(300)
    return HttpResponse.json(promptOverrides)
  }),

  http.post(`${base}/clients/:clientId/prompt-overrides`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `o-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      scope: body.scope,
      crawlConfigId: body.crawlConfigId,
      promptKey: body.promptKey,
      overrideText: body.overrideText,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    promptOverrides.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/prompt-overrides/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    promptOverrides = promptOverrides.filter(o => o.id !== id)
    return new HttpResponse(null, { status: 204 })
  })
]
