var builder = DistributedApplication.CreateBuilder(args);

// 1. Local Postgres with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();
var agentdb = postgres.AddDatabase("agentdb");

// 2. Local Redis with persistent volume
var redis = builder.AddRedis("redis")
    .WithDataVolume();

// 3. Local Temporal Dev Server Container (with Web UI on 8233)
var temporal = builder.AddContainer("temporal", "temporalio/temporal", "latest")
    .WithArgs("server", "start-dev", "--ip", "0.0.0.0")
    .WithHttpEndpoint(targetPort: 7233, name: "grpc")
    .WithHttpEndpoint(targetPort: 8233, name: "ui");

// 4. API Service
var api = builder.AddProject<Projects.AgentMesh_Api>("api")
    .WithReference(agentdb)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WithHttpEndpoint(targetPort: 5000, name: "http")
    .WaitFor(temporal);

// 5. Worker Service
var worker = builder.AddProject<Projects.AgentMesh_Worker>("worker")
    .WithReference(agentdb)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WaitFor(temporal);

builder.Build().Run();
