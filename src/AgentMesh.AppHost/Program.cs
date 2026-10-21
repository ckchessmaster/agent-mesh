var builder = DistributedApplication.CreateBuilder(args);

// 0. OIDC Provider Configuration (Dex for local dev, or external IdP in production)
var externalOidcAuthority = builder.Configuration["Oidc:Authority"]
    ?? builder.Configuration["OIDC_AUTHORITY"];
var oidcClientId = builder.Configuration["Oidc:ClientId"]
    ?? builder.Configuration["OIDC_CLIENT_ID"]
    ?? "agentmesh-dashboard";

IResourceBuilder<ContainerResource>? dex = null;
string oidcAuthority;

if (string.IsNullOrWhiteSpace(externalOidcAuthority))
{
    dex = builder.AddDockerfile("dex", "dex")
        .WithVolume("dex-data", "/var/dex")
        .WithHttpEndpoint(port: 8085, targetPort: 5556, name: "http");

    oidcAuthority = "http://localhost:8085/dex";
}
else
{
    oidcAuthority = externalOidcAuthority;
}

// 1. Local Postgres with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin(pg => pg.WithHostPort(5050));
var agentdb = postgres.AddDatabase("agentdb");

// 2. Local Redis with persistent volume
var redis = builder.AddRedis("redis")
    .WithDataVolume();

// 3. Local Temporal Dev Server Container (with Web UI on 8233)
var temporal = builder.AddContainer("temporal", "temporalio/temporal", "latest")
    .WithArgs("server", "start-dev", "--ip", "0.0.0.0")
    .WithHttpEndpoint(port: 7233, targetPort: 7233, name: "grpc")
    .WithHttpEndpoint(port: 8233, targetPort: 8233, name: "ui");

// 4. Local SeaweedFS S3 Container
var seaweedfs = builder.AddContainer("seaweedfs", "chrislusf/seaweedfs", "latest")
    .WithArgs("server", "-s3", "-dir=/data")
    .WithHttpEndpoint(port: 8333, targetPort: 8333, name: "s3")
    .WithHttpEndpoint(port: 9333, targetPort: 9333, name: "master");

// 5. API Service
var api = builder.AddProject<Projects.AgentMesh_Api>("api")
    .WithHttpEndpoint(port: 5000, targetPort: 5000, name: "http", isProxied: false)
    .WithReference(agentdb)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WithEnvironment("Storage__S3__ServiceUrl", "http://localhost:8333")
    .WithEnvironment("Storage__S3__BucketName", "agent-mesh-artifacts")
    .WithEnvironment("Storage__S3__AccessKey", "agentmesh-storage-key")
    .WithEnvironment("Storage__S3__SecretKey", "AgentMesh#SecureS3Pass2026!")
    .WithEnvironment("Oidc__Authority", oidcAuthority)
    .WithEnvironment("Oidc__ClientId", oidcClientId)
    .WithEnvironment("Oidc__RequireHttpsMetadata", string.IsNullOrWhiteSpace(externalOidcAuthority) ? "false" : "true")
    .WaitFor(temporal)
    .WaitFor(seaweedfs);

if (dex != null)
{
    api.WaitFor(dex);
}

// 6. Worker Service
var worker = builder.AddProject<Projects.AgentMesh_Worker>("worker")
    .WithReference(agentdb)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WithEnvironment("Storage__S3__ServiceUrl", "http://localhost:8333")
    .WithEnvironment("Storage__S3__BucketName", "agent-mesh-artifacts")
    .WithEnvironment("Storage__S3__AccessKey", "agentmesh-storage-key")
    .WithEnvironment("Storage__S3__SecretKey", "AgentMesh#SecureS3Pass2026!")
    .WaitFor(temporal)
    .WaitFor(seaweedfs);

// 7. React Vite Frontend
builder.AddNpmApp("dashboard", "../AgentMesh.Dashboard", scriptName: "dev")
    .WithReference(api)
    .WithEnvironment("VITE_OIDC_AUTHORITY", oidcAuthority)
    .WithEnvironment("VITE_OIDC_CLIENT_ID", oidcClientId)
    .WithHttpEndpoint(port: 3000, targetPort: 3000, env: "PORT", isProxied: false);

builder.Build().Run();
