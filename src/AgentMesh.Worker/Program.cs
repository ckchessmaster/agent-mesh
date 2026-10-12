using AgentMesh.Infrastructure.Data;
using AgentMesh.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb");

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
