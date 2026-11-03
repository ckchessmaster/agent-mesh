using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

public class PersonaIdleReaperServiceTests
{
    private readonly AgentMeshDbContext _db;
    private readonly Mock<IPersonaContainerManager> _containerManagerMock;
    private readonly IServiceProvider _serviceProvider;

    public PersonaIdleReaperServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _containerManagerMock = new Mock<IPersonaContainerManager>();

        var services = new ServiceCollection();
        services.AddScoped(_ => _db);
        _serviceProvider = services.BuildServiceProvider();
    }

    private PersonaIdleReaperService CreateService(Dictionary<string, string?>? configValues = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        return new PersonaIdleReaperService(
            _serviceProvider,
            _containerManagerMock.Object,
            config,
            NullLogger<PersonaIdleReaperService>.Instance);
    }

    [Fact]
    public async Task ReapCycle_AutoUpdatesIdleRunner_WhenUpdateAvailableAndCompletelyIdle()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Image: "ghcr.io/ckchessmaster/agent-mesh-persona-runner:1.9.4",
            ConfiguredImage: "ghcr.io/ckchessmaster/agent-mesh-persona-runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService(new Dictionary<string, string?>
        {
            ["PersonaContainer:AutoUpdateIdleRunners"] = "true",
            ["PersonaContainer:IdleTimeoutMinutes"] = "30"
        });

        // Use reflection to invoke private ReapIdlePersonasAsync
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var task = (Task)method.Invoke(service, [CancellationToken.None])!;
        await task;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync("agent-smith", It.IsAny<CancellationToken>()), Times.Once);
        _containerManagerMock.Verify(m => m.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReapCycle_DoesNotAutoUpdate_WhenPersonaHasActiveThinkingSession()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        _db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            PersonaId = "agent-smith",
            Status = "thinking",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService();
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _containerManagerMock.Verify(m => m.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReapCycle_DoesNotAutoUpdate_WhenPersonaHasActiveJob()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        _db.ProjectJobs.Add(new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid().ToString(),
            Title = "Active background job",
            AssignedAgentId = "agent-smith",
            Status = ProjectJobStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService();
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReapCycle_DoesNotAutoUpdate_WhenStartedRecentlyUnderGracePeriod()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-20), // only 20s ago
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService();
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReapCycle_DoesNotAutoUpdate_WhenConfigDisabled()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService(new Dictionary<string, string?>
        {
            ["PersonaContainer:AutoUpdateIdleRunners"] = "false"
        });
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReapCycle_AutoUpdatesIdleRunner_WhenContainerIsInErrorState()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "error",
            Runtime: "kubernetes",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService();
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert
        _containerManagerMock.Verify(m => m.RestartAsync("agent-smith", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReapCycle_AutoUpdates_WhenThinkingSessionIsStale()
    {
        // Arrange
        var persona = new Persona { Id = "agent-smith", Name = "Agent Smith", DriverId = "agy" };
        _db.Personas.Add(persona);
        _db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            PersonaId = "agent-smith",
            Status = "thinking",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-15) // stale thinking session
        });
        await _db.SaveChangesAsync();

        var status = new PersonaEnvironmentStatus(
            PersonaId: "agent-smith",
            Status: "running",
            Runtime: "docker",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Image: "runner:1.9.4",
            ConfiguredImage: "runner:1.9.5",
            UpdateAvailable: true);

        _containerManagerMock.Setup(m => m.GetStatusAsync("agent-smith", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var service = CreateService();
        var method = typeof(PersonaIdleReaperService).GetMethod("ReapIdlePersonasAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        // Assert - should restart because session is stale
        _containerManagerMock.Verify(m => m.RestartAsync("agent-smith", It.IsAny<CancellationToken>()), Times.Once);
    }
}
