using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentMesh.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using k8s;
using Xunit;

namespace AgentMesh.Tests.Containers;

public class TerminalSessionTests
{
    private class TrackingWebSocket : WebSocket
    {
        public List<(byte Channel, string Message)> SentMessages { get; } = new();
        public bool IsClosed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => IsClosed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => IsClosed ? "closed" : null;
        public override WebSocketState State => IsClosed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => "v4.channel.k8s.io";

        public override void Abort() => IsClosed = true;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            IsClosed = true;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            IsClosed = true;
            return Task.CompletedTask;
        }

        public override void Dispose() => IsClosed = true;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (IsClosed)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "closed");
            }

            var tcs = new TaskCompletionSource<WebSocketReceiveResult>();
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var bytes = buffer.ToArray();
            if (bytes.Length > 0)
            {
                var channel = bytes[0];
                var payload = Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1);
                SentMessages.Add((channel, payload));
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task KubernetesTerminalSession_SendInputAsync_SendsDataOnStdInChannel()
    {
        var mockWs = new TrackingWebSocket();
        var demuxer = new StreamDemuxer(mockWs);
        demuxer.Start();

        var writeLock = new SemaphoreSlim(1, 1);
        var sessionType = typeof(AgentMesh.Infrastructure.Containers.KubernetesPersonaContainerManager)
            .GetNestedType("KubernetesTerminalSession", System.Reflection.BindingFlags.NonPublic)!;

        var session = (IPersonaTerminalSession)Activator.CreateInstance(
            sessionType,
            mockWs,
            demuxer,
            writeLock,
            NullLogger.Instance)!;

        await session.SendInputAsync("echo Hello\n");

        mockWs.SentMessages.Should().ContainSingle(m => m.Channel == (byte)ChannelIndex.StdIn && m.Message == "echo Hello\n");

        await session.DisposeAsync();
        mockWs.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task KubernetesTerminalSession_ResizeAsync_SendsFormattedDimensionsOnResizeChannel()
    {
        var mockWs = new TrackingWebSocket();
        var demuxer = new StreamDemuxer(mockWs);
        demuxer.Start();

        var writeLock = new SemaphoreSlim(1, 1);
        var sessionType = typeof(AgentMesh.Infrastructure.Containers.KubernetesPersonaContainerManager)
            .GetNestedType("KubernetesTerminalSession", System.Reflection.BindingFlags.NonPublic)!;

        var session = (IPersonaTerminalSession)Activator.CreateInstance(
            sessionType,
            mockWs,
            demuxer,
            writeLock,
            NullLogger.Instance)!;

        await session.ResizeAsync(120, 40);

        mockWs.SentMessages.Should().ContainSingle(m => m.Channel == (byte)ChannelIndex.Resize && m.Message == "{\"Width\":120,\"Height\":40}");

        await session.DisposeAsync();
    }

    [Fact]
    public async Task DockerProcessTerminalSession_SendInputAndDispose_WorksCleanly()
    {
        var sessionType = typeof(AgentMesh.Infrastructure.Containers.DockerPersonaContainerManager)
            .GetNestedType("DockerProcessTerminalSession", System.Reflection.BindingFlags.NonPublic)!;

        var psi = new ProcessStartInfo
        {
            FileName = "cat",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        var proc = Process.Start(psi)!;

        var session = (IPersonaTerminalSession)Activator.CreateInstance(
            sessionType,
            proc,
            proc.StandardInput)!;

        await session.SendInputAsync("ping\n");
        await session.ResizeAsync(80, 24);

        await session.DisposeAsync();
        // Disposing session kills and disposes process
        var exitedOrDisposed = false;
        try { exitedOrDisposed = proc.HasExited; }
        catch (InvalidOperationException) { exitedOrDisposed = true; }
        exitedOrDisposed.Should().BeTrue();
    }
}
