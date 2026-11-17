using System.Net;
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

namespace AgentMesh.Tests.Observability;

/// <summary>
/// The Prometheus scrape endpoint that the Kubernetes PodMonitor reads.
/// </summary>
/// <remarks>
/// This endpoint exists because the API kept getting OOMKilled and nothing it exposed could tell
/// "the GC has committed 450Mi and will hand it back shortly" apart from "something is leaking".
/// <c>dotnet_gc_last_collection_heap_size_bytes</c> broken down by generation - LOH especially -
/// plus <c>dotnet_gc_last_collection_memory_committed_size_bytes</c> is what separates those two.
///
/// The disabled case is the one worth protecting. The scrape endpoint is unauthenticated by design,
/// so it is opt-in through configuration and served on a listener the Service does not publish; a
/// default-on regression would hand <c>/metrics</c> to anything that can reach the ingress.
/// </remarks>
public class PrometheusMetricsEndpointTests
{
    private static WebApplicationFactory<AgentMesh.Api.Program> CreateFactory(params (string Key, string Value)[] settings)
    {
        // A name per factory: the seeding below has to be invisible to the other factory's
        // database, and UseInMemoryDatabase's lambda re-runs per context, so the identity is
        // hoisted rather than generated inside it.
        var dbName = "MetricsEndpointDb_" + Guid.NewGuid();

        return new WebApplicationFactory<AgentMesh.Api.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AgentMeshDbContext) ||
                    d.ServiceType.Namespace?.StartsWith("Npgsql") == true).ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<AgentMeshDbContext>(opts => opts.UseInMemoryDatabase(dbName));

                services.AddSingleton(new Mock<IStorageService>().Object);

                var mockRedis = new Mock<IConnectionMultiplexer>();
                mockRedis.Setup(r => r.IsConnected).Returns(true);
                mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                    .Returns(new Mock<IDatabase>().Object);
                services.AddSingleton(mockRedis.Object);

                services.AddSingleton(new Mock<ITemporalClient>().Object);
            });
        });
    }

    [Fact]
    public async Task MetricsEndpoint_WhenEnabled_ExposesHeapSizePerGenerationIncludingLoh()
    {
        using var factory = CreateFactory(("Metrics:Prometheus:Enabled", "true"));
        var client = factory.CreateClient();

        // A completed request has to exist before the scrape, or there is nothing for the request
        // meters to report yet and the assertion below would depend on scrape-vs-record ordering.
        (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // The whole reason for the endpoint: committed heap and its generation breakdown, which is
        // what decides whether the container limit holds.
        body.Should().Contain("dotnet_gc_last_collection_memory_committed_size_bytes");
        body.Should().Contain("dotnet_gc_last_collection_heap_size_bytes");
        body.Should().Contain("gc_heap_generation=\"loh\"");

        // And that the framework meters are flowing through the same exporter, so request rate and
        // in-flight work can be read next to the heap.
        body.Should().Contain("http_server_request_duration_seconds");
    }

    [Fact]
    public async Task MetricsEndpoint_WhenNotEnabled_IsNotRoutedAtAll()
    {
        using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MetricsEndpoint_ConfiguredOnItsOwnPort_DoesNotAnswerOnTheApplicationListener()
    {
        // The scrape listener is a separate port, and the endpoint gates on it. Under TestServer no
        // real port is involved, so this pins the property that matters: enabling the endpoint with
        // a port configured must not open /metrics on the application's own listener.
        using var factory = CreateFactory(
            ("Metrics:Prometheus:Enabled", "true"),
            ("Metrics:Prometheus:Port", "9464"));

        var response = await factory.CreateClient().GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
