using AgentMesh.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb");

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Ok(new { Service = "AgentMesh API", Status = "Online" }));

app.Run();
