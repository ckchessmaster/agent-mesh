# Phase 3: Ingress, Auth & Spark "Hello-World" Integration

## 1. Phase Objective & Scope

The objective of Phase 3 is to secure the control plane endpoints with configurable authentication, build container packaging and Kubernetes Helm charts with long-running SSE Ingress timeouts, and perform end-to-end verification with **Gemini Spark** as a cloud MCP client.

By the end of this phase:
1. `AgentMesh.Api` enforces API key and `X-Agent-Id` header validation.
2. REST management endpoints are available under `/api/*` for session and job inspection.
3. Helm deployment manifests are ready for Kubernetes with NGINX ingress configured for unbuffered SSE streaming.
4. Gemini Spark can be configured with the MCP endpoint and execute the hello-world staging prompt with zero host HITL modals.

---

## 2. Prerequisites & Environment

* Completion of **Phase 1** and **Phase 2**.
* Container engine (Docker / Podman) or CI build pipeline.
* Kubernetes cluster (or Minikube / K3s) with NGINX Ingress controller installed.
* Gemini Spark environment with custom MCP server integration capability.

---

## 3. Architecture & Authentication Flow

```text
External Cloud Agent (Gemini Spark)
        │
        │ HTTP Headers:
        │   Authorization: Bearer <STATIC_API_KEY>
        │   X-Agent-Id: gemini-spark
        ▼
NGINX Ingress (K8s)
  ├── Annotations: proxy-buffering: "off", read/send-timeout: 3600s
  └── Routes:
        ├── /mcp  ──> AgentMesh.Api (Port 5000)
        └── /api  ──> AgentMesh.Api (Port 5000)
        ▼
ApiKeyAuthenticationHandler
  ├── 1. Validates Bearer token / X-Api-Key against configured secret or DB
  ├── 2. Resolves X-Agent-Id against 'agents' table (Trust Tier check)
  └── 3. Injects ClaimsPrincipal (AgentId, TrustTier) into HttpContext
        ▼
StagingTools / Controllers
```

---

## 4. Step-by-Step Implementation Guide

### Step 1: Implement API Key Authentication (`AgentMesh.Api`)

```csharp
// src/AgentMesh.Api/Auth/ApiKeyAuthenticationOptions.cs
using Microsoft.AspNetCore.Authentication;

namespace AgentMesh.Api.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public string HeaderName { get; set; } = "X-Api-Key";
    public string ConfiguredKey { get; set; } = string.Empty;
}
```

```csharp
// src/AgentMesh.Api/Auth/ApiKeyAuthenticationHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentMesh.Api.Auth;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentMeshDbContext dbContext,
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Extract Token from Authorization: Bearer <key> or X-Api-Key
        string? providedKey = null;
        if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedKey = authHeader.ToString()["Bearer ".Length..].Trim();
        }
        else if (Request.Headers.TryGetValue(Options.HeaderName, out var customHeader))
        {
            providedKey = customHeader.ToString();
        }

        var masterKey = configuration["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        if (string.IsNullOrEmpty(providedKey) || providedKey != masterKey)
        {
            return AuthenticateResult.Fail("Invalid or missing API Key.");
        }

        // 2. Identify Agent via X-Agent-Id header (defaults to gemini-spark if omitted)
        var agentId = Request.Headers.TryGetValue("X-Agent-Id", out var idHeader)
            ? idHeader.ToString()
            : "gemini-spark";

        var agent = await dbContext.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        var trustTier = agent != null ? ((int)agent.TrustTier).ToString() : "1";

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, agentId),
            new Claim("AgentId", agentId),
            new Claim("TrustTier", trustTier)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
```

---

### Step 2: Implement REST Inspection Endpoints (`AgentMesh.Api`)

```csharp
// src/AgentMesh.Api/Controllers/SessionsController.cs
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController(AgentMeshDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSessions()
    {
        var sessions = await db.AgentSessions
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Ok(sessions);
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var session = new AgentSession
        {
            Title = req.Title,
            Status = "active",
            MetadataJson = req.MetadataJson ?? "{}"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetSessions), new { id = session.Id }, session);
    }
}

public record CreateSessionRequest(string Title, string? MetadataJson);
```

```csharp
// src/AgentMesh.Api/Controllers/JobsController.cs
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobsController(AgentMeshDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetJob(Guid id)
    {
        var job = await db.AgentJobs
            .Include(j => j.RequestedByAgent)
            .FirstOrDefaultAsync(j => j.Id == id);

        return job == null ? NotFound() : Ok(job);
    }
}
```

---

### Step 3: Containerization Dockerfiles

#### `deploy/docker/Dockerfile.api`
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/AgentMesh.Api/AgentMesh.Api.csproj
RUN dotnet publish src/AgentMesh.Api/AgentMesh.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "AgentMesh.Api.dll"]
```

#### `deploy/docker/Dockerfile.worker`
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/AgentMesh.Worker/AgentMesh.Worker.csproj
RUN dotnet publish src/AgentMesh.Worker/AgentMesh.Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AgentMesh.Worker.dll"]
```

---

### Step 4: Kubernetes Helm Manifests (`deploy/helm/agent-mesh/`)

#### `deploy/helm/agent-mesh/Chart.yaml`
```yaml
apiVersion: v2
name: agent-mesh
description: Heterogeneous Agent Control Plane & Communication Bus
type: application
version: 0.1.0
appVersion: "1.0.0"
```

#### `deploy/helm/agent-mesh/templates/ingress.yaml`
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-ingress
  annotations:
    cert-manager.io/cluster-issuer: {{ .Values.ingress.clusterIssuer }}
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-buffering: "off"
    nginx.ingress.kubernetes.io/server-snippets: |
      location /mcp {
        proxy_set_header Connection '';
        chunked_transfer_encoding off;
      }
spec:
  ingressClassName: {{ .Values.ingress.className }}
  tls:
    - secretName: {{ .Values.ingress.tlsSecretName }}
      hosts:
        - {{ .Values.ingress.host }}
  rules:
    - host: {{ .Values.ingress.host }}
      http:
        paths:
          - path: /mcp
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 5000
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 5000
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-dashboard
                port:
                  number: 80
```

---

## 5. Spark E2E "Hello-World" Verification Playbook

### Step 1: Pre-seed Database Records
Execute against cluster PostgreSQL:
```sql
INSERT INTO agents (id, name, runtime_type, trust_tier, persona_prompt)
VALUES ('gemini-spark', 'Gemini Spark', 'McpCloud', 2, 'Lead cloud autonomous agent')
ON CONFLICT (id) DO NOTHING;

INSERT INTO agent_sessions (id, title, status)
VALUES ('a0000000-0000-0000-0000-000000000001', 'Spark MVP Session', 'active')
ON CONFLICT (id) DO NOTHING;
```

### Step 2: Configure Gemini Spark MCP Client
* In Gemini Spark's MCP settings, add:
  * **Server URL**: `https://agents.yourdomain.local/mcp`
  * **Headers**:
    * `Authorization: Bearer <STATIC_API_KEY>`
    * `X-Agent-Id: gemini-spark`

### Step 3: Execute Test Prompt
Send prompt to Gemini Spark:
> "Record an operational journal update for our MVP milestone with title 'Architecture Verified' and note 'Temporal workflow execution confirmed' under session a0000000-0000-0000-0000-000000000001."

### Step 4: Verification Checks
1. **Zero HITL Dialog**: Gemini Spark issues tool call `stage_agent_intent` automatically without asking user for interactive confirmation.
2. **API & Temporal**: Temporal UI shows workflow `job-<UUID>` with Status `Completed`.
3. **Database State**:
   ```sql
   SELECT id, requested_by_agent_id, target_service, action_verb, execution_status, result 
   FROM agent_jobs 
   ORDER BY created_at DESC LIMIT 1;
   ```
   * Result: Status is `Completed`, `requested_by_agent_id = 'gemini-spark'`.

---

## 6. Agent Execution Prompt & Checklist

```markdown
### AI Developer Task: Phase 3 Execution
Please execute Phase 3 of AgentMesh according to docs/phases/phase-3-auth-ingress-and-spark-e2e-mvp.md.
Follow these steps:
- [ ] Implement ApiKeyAuthenticationHandler and register with ASP.NET Core auth pipeline.
- [ ] Implement SessionsController and JobsController with [Authorize].
- [ ] Create Dockerfiles for Api and Worker under deploy/docker/.
- [ ] Scaffold Helm templates with SSE Ingress annotations under deploy/helm/agent-mesh/.
- [ ] Validate authentication rejecting unauthorized requests and accepting valid API keys.
```
