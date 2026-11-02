using System.Net.WebSockets;
using System.Text;

using FluentAssertions;

using Whalefleet.Api.Terminal;
using Whalefleet.Application.Abstractions.Terminal;

using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 终端文本消息按消息边界解析，二进制输入保持字节流语义。
/// </summary>
public sealed class TerminalFragmentationTests
{
    [Fact]
    public async Task 接收_分片控制消息_仅在完整消息时调整尺寸()
    {
        using var socket = new IncomingSocket([
            (Encoding.UTF8.GetBytes("{\"type\":\"res"), WebSocketMessageType.Text, false),
            (Encoding.UTF8.GetBytes("ize\",\"cols\":120,"), WebSocketMessageType.Text, false),
            (Encoding.UTF8.GetBytes("\"rows\":40}"), WebSocketMessageType.Text, true),
        ]);
        await using var session = new Session();
        await ContainerTerminalEndpoint.PumpFromClientAsync(socket, session, CancellationToken.None);
        session.Size.Should().Be(new TerminalConsoleSize(120, 40));
        session.Input.Length.Should().Be(0);
    }

    [Fact]
    public async Task 接收_分片二进制与非控制文本_按原始字节写入()
    {
        using var socket = new IncomingSocket([
            (new byte[] { 0, 255 }, WebSocketMessageType.Binary, false),
            (new byte[] { 1, 2 }, WebSocketMessageType.Binary, true),
            (Encoding.UTF8.GetBytes("plain"), WebSocketMessageType.Text, false),
            (Encoding.UTF8.GetBytes(" text"), WebSocketMessageType.Text, true),
        ]);
        await using var session = new Session();
        await ContainerTerminalEndpoint.PumpFromClientAsync(socket, session, CancellationToken.None);
        session.Input.ToArray().Should().Equal(new byte[] { 0, 255, 1, 2 }.Concat(Encoding.UTF8.GetBytes("plain text")));
        session.Size.Should().BeNull();
    }

    [Fact]
    public async Task 接收_控制消息超过上限_关闭连接且不写入容器()
    {
        using var socket = new IncomingSocket([(new byte[65537], WebSocketMessageType.Text, true)]);
        await using var session = new Session();
        var reason = await ContainerTerminalEndpoint.PumpFromClientAsync(socket, session, CancellationToken.None);
        reason.Should().Be("message-too-big");
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        session.Input.Length.Should().Be(0);
    }

    private sealed class Session : IContainerTerminalSession
    {
        public MemoryStream Input { get; } = new();
        public TerminalConsoleSize? Size
        {
            get; private set;
        }
        public long? ExitCode => null;
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            this.Input.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(TerminalConsoleSize size, CancellationToken cancellationToken = default)
        {
            this.Size = size;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => this.Input.DisposeAsync();
    }

    private sealed class IncomingSocket((byte[] Bytes, WebSocketMessageType Type, bool End)[] frames) : WebSocket
    {
        private int _index;
        private int _offset;
        private WebSocketCloseStatus? _closeStatus;
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _closeStatus is null ? WebSocketState.Open : WebSocketState.CloseSent;
        public override string? SubProtocol => null;
        public override void Abort()
        {
        }
        public override void Dispose()
        {
        }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => this.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_index == frames.Length)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var frame = frames[_index];
            var count = Math.Min(buffer.Count, frame.Bytes.Length - _offset);
            frame.Bytes.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var complete = _offset == frame.Bytes.Length;
            if (complete)
            {
                _index++;
                _offset = 0;
            }

            return Task.FromResult(new WebSocketReceiveResult(count, frame.Type, complete && frame.End));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
