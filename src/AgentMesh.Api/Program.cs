using System.Security.Claims;
using AgentMesh.Api.Auth;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Storage;
using AgentMesh.Infrastructure.Containers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb", configureDbContextOptions: options => options.UseSnakeCaseNamingConvention());
builder.AddRedisClient("redis");

// Configure Temporal Client
var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
builder.Services.AddTemporalClient(clientTargetHost: temporalHost, clientNamespace: "default");

// Configure Authentication & Authorization
builder.Services.AddSingleton<IApiKeyService, ApiKeyService>();
builder.Services.AddHttpContextAccessor();

var oidcAuthority = builder.Configuration["Oidc:Authority"] ?? "http://localhost:8085/dex";
var oidcClientId = builder.Configuration["Oidc:ClientId"] ?? "agentmesh-dashboard";
var requireHttpsMetadata = builder.Configuration.GetValue<bool>("Oidc:RequireHttpsMetadata", false);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = AuthConstants.CompositeScheme;
    options.DefaultChallengeScheme = AuthConstants.CompositeScheme;
})
.AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(AuthConstants.CompositeScheme, null)
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null)
.AddJwtBearer(AuthConstants.JwtBearerScheme, options =>
{
    options.Authority = oidcAuthority;
    options.Audience = oidcClientId;
    options.RequireHttpsMetadata = requireHttpsMetadata;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = oidcAuthority,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                var email = identity.FindFirst(ClaimTypes.Email)?.Value
                    ?? identity.FindFirst("email")?.Value
                    ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? "admin@agentmesh.local";

                var name = identity.FindFirst(ClaimTypes.Name)?.Value
                    ?? identity.FindFirst("name")?.Value
                    ?? email;

                if (!identity.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
                {
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, email));
                }
                if (!identity.HasClaim(c => c.Type == "AgentId"))
                {
                    identity.AddClaim(new Claim("AgentId", "operator"));
                }
                if (!identity.HasClaim(c => c.Type == "TrustTier"))
                {
                    identity.AddClaim(new Claim("TrustTier", "3"));
                }
                if (!identity.HasClaim(c => c.Type == ClaimTypes.Role))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
                    identity.AddClaim(new Claim(ClaimTypes.Role, "Operator"));
                }
            }
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

// Configure SeaweedFS S3 Storage
builder.Services.AddSeaweedS3Storage(builder.Configuration);

// Per-job workspaces + gated handoff.
// The durable workspace state is what makes handoff work across machines: a runner's local
// directory is a cache, so anything not saved here is treated as lost when the next persona starts.
builder.Services.Configure<JobHandoffOptions>(builder.Configuration.GetSection(JobHandoffOptions.SectionName));
builder.Services.AddScoped<IJobWorkspaceService, JobWorkspaceService>();
builder.Services.AddScoped<JobTurnDispatcher>();
builder.Services.AddScoped<IJobHandoffService, JobHandoffService>();
builder.Services.AddHostedService<JobHandoffReaper>();

// Dedicated Persona Container Environments & Idle Reaper
builder.Services.AddPersonaContainerManager(builder.Configuration);
builder.Services.AddHostedService<PersonaIdleReaperService>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Configure Controllers & SignalR
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// Register MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<StagingTools>()
    .WithTools<SessionTools>();

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapHub<AgentMeshHub>("/hubs/agent-mesh");
app.MapMcp("/mcp");

app.MapGet("/", () => Results.Ok(new { Service = "AgentMesh API", Status = "Online", Version = "1.0.0" }));

app.MapGet("/api/status", async (IServiceProvider sp, IConfiguration config) =>
{
    var swTotal = System.Diagnostics.Stopwatch.StartNew();
    var services = new Dictionary<string, object>();

    // 1. PostgreSQL Check
    try
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetService<AgentMeshDbContext>();
        if (db != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var canConnect = await db.Database.CanConnectAsync();
            sw.Stop();
            services["postgres"] = new
            {
                status = canConnect ? "Healthy" : "Unhealthy",
                latencyMs = sw.ElapsedMilliseconds,
                description = "PostgreSQL 18 Database (agentdb)"
            };
        }
        else
        {
            services["postgres"] = new { status = "Unhealthy", error = "DbContext not registered", description = "PostgreSQL 18 Database (agentdb)" };
        }
    }
    catch (Exception ex)
    {
        services["postgres"] = new { status = "Unhealthy", error = ex.Message, description = "PostgreSQL 18 Database (agentdb)" };
    }

    // 2. Redis Check
    try
    {
        var redis = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
        if (redis != null && redis.IsConnected)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ping = await redis.GetDatabase().PingAsync();
            sw.Stop();
            services["redis"] = new
            {
                status = "Healthy",
                latencyMs = Math.Max(1, (long)ping.TotalMilliseconds),
                description = "Redis 8.6 Distributed Cache"
            };
        }
        else
        {
            services["redis"] = new { status = "Unhealthy", error = "Redis client not connected", description = "Redis 8.6 Distributed Cache" };
        }
    }
    catch (Exception ex)
    {
        services["redis"] = new { status = "Unhealthy", error = ex.Message, description = "Redis 8.6 Distributed Cache" };
    }

    // 3. Temporal Check
    try
    {
        var temporal = sp.GetService<Temporalio.Client.ITemporalClient>();
        if (temporal != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sysInfo = await temporal.Connection.WorkflowService.GetSystemInfoAsync(new());
            sw.Stop();
            services["temporal"] = new
            {
                status = "Healthy",
                latencyMs = sw.ElapsedMilliseconds,
                version = sysInfo.ServerVersion,
                description = $"Temporal Dev Server ({config["Temporal:Host"] ?? "localhost:7233"})"
            };
        }
        else
        {
            services["temporal"] = new { status = "Unhealthy", error = "Temporal client not registered", description = "Temporal Dev Server" };
        }
    }
    catch (Exception ex)
    {
        services["temporal"] = new { status = "Unhealthy", error = ex.Message, description = "Temporal Dev Server" };
    }

    // 4. SeaweedFS S3 Check
    try
    {
        var storage = sp.GetService<AgentMesh.Core.Services.IStorageService>();
        if (storage != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await storage.EnsureBucketExistsAsync();
            sw.Stop();
            services["s3"] = new
            {
                status = "Healthy",
                latencyMs = sw.ElapsedMilliseconds,
                bucket = config["Storage:S3:BucketName"] ?? "agent-mesh-artifacts",
                description = $"SeaweedFS S3 Storage ({config["Storage:S3:ServiceUrl"] ?? "http://localhost:8333"})"
            };
        }
        else
        {
            services["s3"] = new { status = "Unhealthy", error = "Storage service not registered", description = "SeaweedFS S3 Storage" };
        }
    }
    catch (Exception ex)
    {
        services["s3"] = new { status = "Unhealthy", error = ex.Message, description = "SeaweedFS S3 Storage" };
    }

    // 5. API Gateway Check
    swTotal.Stop();
    services["api"] = new
    {
        status = "Healthy",
        latencyMs = swTotal.ElapsedMilliseconds,
        description = "ASP.NET Core 10 Web API"
    };

    var isAllHealthy = services.Values.All(v =>
    {
        var prop = v.GetType().GetProperty("status");
        return prop?.GetValue(v)?.ToString() == "Healthy";
    });

    return Results.Ok(new
    {
        status = isAllHealthy ? "Healthy" : "Degraded",
        timestamp = DateTime.UtcNow,
        environment = app.Environment.EnvironmentName,
        services
    });
});

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

app.Run();

namespace AgentMesh.Api
{
    public partial class Program { }
}

