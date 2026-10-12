using AgentMesh.Api.Auth;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Tools;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb", configureDbContextOptions: options => options.UseSnakeCaseNamingConvention());

// Configure Temporal Client
var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
builder.Services.AddTemporalClient(clientTargetHost: temporalHost, clientNamespace: "default");

// Configure Authentication & Authorization
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.DefaultScheme, null);
builder.Services.AddAuthorization();

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
builder.Services.AddSignalR();

// Register MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<StagingTools>();

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapHub<AgentMeshHub>("/hubs/agent-mesh");
app.MapMcp("/mcp").RequireAuthorization();

app.MapGet("/", () => Results.Ok(new { Service = "AgentMesh API", Status = "Online" }));

app.Run();
