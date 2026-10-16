using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using Temporalio.Client;

namespace AgentMesh.Tests.Controllers;

public class StatusEndpointTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>
{
    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;

    public StatusEndpointTests(WebApplicationFactory<AgentMesh.Api.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Replace DbContext with InMemory
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AgentMeshDbContext) ||
                    d.ServiceType.Namespace?.StartsWith("Npgsql") == true).ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }

                services.AddDbContext<AgentMeshDbContext>(opts =>
                    opts.UseInMemoryDatabase("StatusEndpointTestDb"));

                // Mock Storage
                var mockStorage = new Mock<IStorageService>();
                mockStorage.Setup(s => s.EnsureBucketExistsAsync(default)).Returns(Task.CompletedTask);
                services.AddSingleton(mockStorage.Object);

                // Mock Redis
                var mockRedis = new Mock<IConnectionMultiplexer>();
                var mockDb = new Mock<IDatabase>();
                mockRedis.Setup(r => r.IsConnected).Returns(true);
                mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
                mockDb.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(2));
                services.AddSingleton(mockRedis.Object);

                // Mock Temporal Client
                var mockTemporal = new Mock<ITemporalClient>();
                services.AddSingleton(mockTemporal.Object);
            });
        });
    }

    [Fact]
    public async Task GetStatus_Returns200Ok_WithExpectedServiceStatusStructure()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.TryGetProperty("services", out var services).Should().BeTrue();
        services.TryGetProperty("api", out var apiStatus).Should().BeTrue();
        services.TryGetProperty("postgres", out var postgresStatus).Should().BeTrue();
        services.TryGetProperty("redis", out var redisStatus).Should().BeTrue();
        services.TryGetProperty("s3", out var s3Status).Should().BeTrue();
        services.TryGetProperty("temporal", out var temporalStatus).Should().BeTrue();
    }

    [Fact]
    public async Task GetRoot_ReturnsOnlineStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("status").GetString().Should().Be("Online");
        content.GetProperty("service").GetString().Should().Be("AgentMesh API");
    }
}
