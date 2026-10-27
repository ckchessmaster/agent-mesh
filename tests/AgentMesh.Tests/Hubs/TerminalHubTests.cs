using System;
using System.Threading;
using System.Threading.Tasks;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Hubs;

public class TerminalHubTests
{
    private readonly Mock<HubCallerContext> _mockContext;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<ISingleClientProxy> _mockCallerProxy;
    private readonly Mock<IHubContext<TerminalHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockHubClients;
    private readonly Mock<ISingleClientProxy> _mockTargetClientProxy;
    private readonly Mock<IPersonaContainerManager> _mockContainerManager;

    public TerminalHubTests()
    {
        _mockContext = new Mock<HubCallerContext>();
        _mockContext.Setup(c => c.ConnectionId).Returns("conn-test-1");

        _mockClients = new Mock<IHubCallerClients>();
        _mockCallerProxy = new Mock<ISingleClientProxy>();
        _mockClients.Setup(c => c.Caller).Returns(_mockCallerProxy.Object);

        _mockHubContext = new Mock<IHubContext<TerminalHub>>();
        _mockHubClients = new Mock<IHubClients>();
        _mockTargetClientProxy = new Mock<ISingleClientProxy>();
        _mockHubClients.Setup(c => c.Client("conn-test-1")).Returns(_mockTargetClientProxy.Object);
        _mockHubContext.Setup(c => c.Clients).Returns(_mockHubClients.Object);

        _mockContainerManager = new Mock<IPersonaContainerManager>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Attach_EmptyPersonaId_SendsErrorMessage(string? personaId)
    {
        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach(personaId!);

        _mockCallerProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => ((string)args[0]).Contains("Persona ID is required")), default), Times.Once);
        _mockContainerManager.Verify(m => m.AttachTerminalAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Func<string, Task>>(), It.IsAny<Func<int, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Attach_NullContainerManager_SendsNotConfiguredMessage()
    {
        var hub = new TerminalHub(_mockHubContext.Object, containerManager: null)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook");

        _mockCallerProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => ((string)args[0]).Contains("Terminal runtime is not configured")), default), Times.Once);
    }

    [Fact]
    public async Task Attach_ValidPersona_SendsInitialBanner_AndInvokesContainerManager()
    {
        var mockSession = new Mock<IPersonaTerminalSession>();
        Func<string, Task>? capturedOutput = null;
        Func<int, Task>? capturedClosed = null;

        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int, int, Func<string, Task>, Func<int, Task>, CancellationToken>((p, c, r, onOut, onClosed, ct) =>
            {
                capturedOutput = onOut;
                capturedClosed = onClosed;
            })
            .ReturnsAsync(mockSession.Object);

        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook", 80, 24);

        // Verify initial banner was sent
        _mockCallerProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => ((string)args[0]).Contains("Connected to dedicated persona terminal: @rook")), default), Times.Once);

        // Verify streaming callbacks are wired
        capturedOutput.Should().NotBeNull();
        capturedClosed.Should().NotBeNull();

        // Simulate streaming output from container
        await capturedOutput!("root@rook:/workspace# \n");
        _mockTargetClientProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => (string)args[0] == "root@rook:/workspace# \n"), default), Times.Once);

        // Simulate closed event from container
        await capturedClosed!(0);
        _mockTargetClientProxy.Verify(c => c.SendCoreAsync("Closed", It.Is<object[]>(args => (int)args[0] == 0), default), Times.Once);
    }

    [Fact]
    public async Task Attach_ValidPersona_AfterHubDisposed_OutputCallbackContinuesViaHubContext()
    {
        var mockSession = new Mock<IPersonaTerminalSession>();
        Func<string, Task>? capturedOutput = null;

        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int, int, Func<string, Task>, Func<int, Task>, CancellationToken>((p, c, r, onOut, onClosed, ct) =>
            {
                capturedOutput = onOut;
            })
            .ReturnsAsync(mockSession.Object);

        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook", 80, 24);

        // SignalR disposes Hub after invocation
        hub.Dispose();

        // Background output streaming callback MUST succeed without throwing ObjectDisposedException
        var act = async () => await capturedOutput!("test echo from bash\n");
        await act.Should().NotThrowAsync();

        _mockTargetClientProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => (string)args[0] == "test echo from bash\n"), default), Times.Once);
    }

    [Fact]
    public async Task Input_WithActiveSession_DispatchesToSession()
    {
        var mockSession = new Mock<IPersonaTerminalSession>();
        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var hub1 = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };
        await hub1.Attach("rook", 80, 24);
        hub1.Dispose();

        // New Hub instance for Input invocation (standard SignalR ephemeral lifecycle)
        var hub2 = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub2.Input("ls -la\n");

        mockSession.Verify(s => s.SendInputAsync("ls -la\n"), Times.Once);
    }

    [Fact]
    public async Task Resize_WithActiveSession_DispatchesToSession()
    {
        var mockSession = new Mock<IPersonaTerminalSession>();
        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var hub1 = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };
        await hub1.Attach("rook", 80, 24);
        hub1.Dispose();

        // New Hub instance for Resize invocation
        var hub2 = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub2.Resize(120, 35);

        mockSession.Verify(s => s.ResizeAsync(120, 35), Times.Once);
    }

    [Fact]
    public async Task Attach_ReplacesExistingSession_AndDisposesOldSession()
    {
        var mockSession1 = new Mock<IPersonaTerminalSession>();
        var mockSession2 = new Mock<IPersonaTerminalSession>();

        _mockContainerManager
            .SetupSequence(m => m.AttachTerminalAsync(
                "rook",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession1.Object)
            .ReturnsAsync(mockSession2.Object);

        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook", 80, 24);
        await hub.Attach("rook", 100, 30);

        mockSession1.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_DisposesActiveSession()
    {
        var mockSession = new Mock<IPersonaTerminalSession>();
        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook", 80, 24);
        await hub.OnDisconnectedAsync(null);

        mockSession.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Attach_ContainerManagerThrows_SendsErrorMessage()
    {
        _mockContainerManager
            .Setup(m => m.AttachTerminalAsync(
                "rook",
                80,
                24,
                It.IsAny<Func<string, Task>>(),
                It.IsAny<Func<int, Task>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pod connection failed"));

        var hub = new TerminalHub(_mockHubContext.Object, _mockContainerManager.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object
        };

        await hub.Attach("rook", 80, 24);

        _mockCallerProxy.Verify(c => c.SendCoreAsync("Output", It.Is<object[]>(args => ((string)args[0]).Contains("Pod connection failed")), default), Times.Once);
    }
}
