using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Infrastructure.Storage;
using AgentMesh.Worker.Activities;
using AgentMesh.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb", configureDbContextOptions: options => options.UseSnakeCaseNamingConvention());
builder.AddRedisClient("redis");

// SeaweedFS S3 Storage
builder.Services.AddSeaweedS3Storage(builder.Configuration);

// Repositories the macro turn needs: the checkpoint store (context recovery) and Job B's memory
// store (injected into the prompt stack). TryAddScoped so a test host can substitute a fake by
// registering first — the same convention AgentMeshRepositoryServiceCollectionExtensions uses.
builder.Services.TryAddScoped<IWorkspaceStateRepository, WorkspaceStateRepository>();
builder.Services.TryAddScoped<IAgentMemoryRepository, AgentMemoryRepository>();

// The workspace probe is stateless and shell-backed, so a singleton is correct and it keeps one
// resolved setsid path per process rather than per scope. Built by a factory rather than by type
// because its constructor takes an optional shell-runner delegate and a log sink, neither of which
// the container can supply.
builder.Services.TryAddSingleton<IWorkspaceReconciler>(sp =>
    new ShellWorkspaceReconciler(
        shellRunner: null,
        log: message => sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AgentMesh.Worker.WorkspaceReconciler")
            .LogWarning("{Message}", message)));

builder.Services.TryAddScoped<IPromptStackBuilder, PromptStackBuilder>();
builder.Services.TryAddScoped<IAgentTurnRunner, HttpAgentTurnRunner>();

// The worker dispatches turns over HTTP to the API's dispatcher and observes them in Postgres.
// The client is named so its timeout is configured here rather than at the call site: a dispatch
// that hangs forever would hold an activity slot, and the whole point of the durable pause is that
// nothing holds a slot.
builder.Services.AddHttpClient(HttpAgentTurnRunner.HttpClientName, client =>
{
    // Dispatch is a POST that starts a turn and returns the session id; the turn itself is observed
    // afterwards, so a short timeout is safe — and it is what makes the runner's re-attach path
    // (rather than a hung request) the thing that recovers a slow API.
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Server-side PR merge, used by GitActivities after an approved review signal. The worker holds no
// interactive credentials of its own; it reads the same GitHub:* settings the API does.
builder.Services.AddHttpClient(GitHubPullRequestService.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.TryAddScoped<IGitHubPullRequestService, GitHubPullRequestService>();

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
var temporalNamespace = builder.Configuration["Temporal:Namespace"] ?? "default";

builder.Services.AddHttpClient<NotificationActivities>();

builder.Services.AddHostedTemporalWorker(
    clientTargetHost: temporalHost,
    clientNamespace: temporalNamespace,
    taskQueue: "agent-task-queue")
    .AddWorkflow<AgentTaskWorkflow>()
    // The macro task workflow. Registered alongside the legacy one rather than replacing it:
    // AgentTaskWorkflow is what DiscordWebhookController and StagingTools start today, and both
    // keep running on their own workflow ids.
    .AddWorkflow<TaskExecutionWorkflow>()
    .AddScopedActivities<DatabaseActivities>()
    .AddScopedActivities<ExecutionActivities>()
    .AddScopedActivities<NotificationActivities>()
    .AddScopedActivities<GitActivities>();

var host = builder.Build();
host.Run();
