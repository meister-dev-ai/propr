import { http, HttpResponse, delay } from 'msw'
import protocolMockData from '../../mock/data/protocol_response_1.json'

const base = import.meta.env.VITE_API_BASE_URL ?? ''

let jobTick = 0

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
    
    const isCompleted = jobTick > 8
    
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
          resultSummary: 'Found 3 minor issues. Suggested improvements.'
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
          resultSummary: isCompleted ? 'Automated review finished. LGTM!' : null
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
          errorMessage: 'Failed to access ADO repository due to expired token.'
        }
      ]
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
        
        const isCompleted = jobTick > 8
        return HttpResponse.json([
          {
            id: 'pass124',
            jobId: 'job-124',
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
  })
]
