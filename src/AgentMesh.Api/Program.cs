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

// Register MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<StagingTools>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapMcp("/mcp");

app.MapGet("/", () => Results.Ok(new { Service = "AgentMesh API", Status = "Online" }));

app.Run();
