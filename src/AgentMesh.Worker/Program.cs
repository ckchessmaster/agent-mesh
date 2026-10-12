using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Worker.Activities;
using Microsoft.EntityFrameworkCore;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb", configureDbContextOptions: options => options.UseSnakeCaseNamingConvention());

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";

builder.Services.AddHttpClient<NotificationActivities>();

builder.Services.AddHostedTemporalWorker(
    clientTargetHost: temporalHost,
    clientNamespace: "default",
    taskQueue: "agent-task-queue")
    .AddWorkflow<AgentTaskWorkflow>()
    .AddScopedActivities<DatabaseActivities>()
    .AddScopedActivities<ExecutionActivities>()
    .AddScopedActivities<NotificationActivities>();

var host = builder.Build();
host.Run();
