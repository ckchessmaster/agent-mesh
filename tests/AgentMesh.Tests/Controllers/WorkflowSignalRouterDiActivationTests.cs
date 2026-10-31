using System.Reflection;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
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
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="IWorkflowSignalRouter"/> and all controllers depending on it
/// activate cleanly from the API service configuration without throwing or injecting null.
/// </summary>
public class WorkflowSignalRouterDiActivationTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>
{
    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;
    private readonly string _dbName = "WorkflowDiDb_" + Guid.NewGuid();

    public WorkflowSignalRouterDiActivationTests(WebApplicationFactory<AgentMesh.Api.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var stale = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AgentMeshDbContext) ||
                    d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration") ||
                    d.ServiceType.Namespace?.StartsWith("Npgsql") == true).ToList();
                foreach (var d in stale) services.Remove(d);

                services.AddDbContext<AgentMeshDbContext>(opts => opts.UseInMemoryDatabase(_dbName));

                var mockStorage = new Mock<IStorageService>();
                mockStorage.Setup(s => s.EnsureBucketExistsAsync(default)).Returns(Task.CompletedTask);
                services.AddSingleton(mockStorage.Object);

                var mockRedis = new Mock<IConnectionMultiplexer>();
                var mockDb = new Mock<IDatabase>();
                mockRedis.Setup(r => r.IsConnected).Returns(true);
                mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
                mockDb.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(2));
                services.AddSingleton(mockRedis.Object);

                var mockTemporal = new Mock<ITemporalClient>();
                services.AddSingleton(mockTemporal.Object);
            });
        });
    }

    [Fact]
    public void IWorkflowSignalRouter_ResolvesFromServiceContainer()
    {
        using var scope = _factory.Services.CreateScope();
        var router = scope.ServiceProvider.GetService<IWorkflowSignalRouter>();

        router.Should().NotBeNull("IWorkflowSignalRouter must be registered in DI");
        router.Should().BeOfType<WorkflowSignalRouter>();
    }

    [Fact]
    public void MacroTurnsController_ResolvesWithRequiredWorkflowRouter()
    {
        using var scope = _factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<MacroTurnsController>(scope.ServiceProvider);

        controller.Should().NotBeNull();

        var workflowsField = typeof(MacroTurnsController)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => typeof(IWorkflowSignalRouter).IsAssignableFrom(f.FieldType));

        workflowsField.Should().NotBeNull();
        workflowsField!.GetValue(controller).Should().NotBeNull("MacroTurnsController requires IWorkflowSignalRouter");
    }

    [Fact]
    public void MacroTurnController_ResolvesFromServiceContainer()
    {
        using var scope = _factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<MacroTurnController>(scope.ServiceProvider);

        controller.Should().NotBeNull();
    }

    [Fact]
    public void ProjectJobsController_ResolvesWithNonNullWorkflowRouter()
    {
        using var scope = _factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ProjectJobsController>(scope.ServiceProvider);

        controller.Should().NotBeNull();

        var workflowsField = typeof(ProjectJobsController)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => typeof(IWorkflowSignalRouter).IsAssignableFrom(f.FieldType));

        workflowsField.Should().NotBeNull();
        var routerValue = workflowsField!.GetValue(controller);
        routerValue.Should().NotBeNull("ProjectJobsController optional workflows parameter must be injected non-null by DI");
        routerValue.Should().BeAssignableTo<IWorkflowSignalRouter>();
    }
}
