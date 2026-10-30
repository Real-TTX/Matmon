using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>Which answers cross the remote access tunnel in pieces: only for a cloud that asked for it, never what the cloud
/// rewrites whole (HTML, CSS), and only when the body is large or of unknown length - an agent package (40-55 MB) used to
/// be refused there as "too large for remote access".</summary>
public sealed class TunnelStreamingRuleTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("application/octet-stream", 45L * 1024 * 1024, true)]
    [InlineData("application/json", null, true)]
    [InlineData("application/octet-stream", 1024L, false)]
    [InlineData("text/html; charset=utf-8", 45L * 1024 * 1024, false)]
    [InlineData("text/css", null, false)]
    public void Large_or_unknown_bodies_the_cloud_does_not_rewrite_are_streamed(string contentType, long? length, bool expected) =>
        Assert.Equal(expected, TunnelClient.ShouldStream(true, Id, contentType, length));

    [Fact]
    public void An_older_cloud_that_did_not_ask_gets_one_message()
    {
        Assert.False(TunnelClient.ShouldStream(false, Id, "application/octet-stream", 45L * 1024 * 1024));
    }

    [Fact]
    public async Task The_body_goes_out_as_pieces_headed_by_the_request_id()
    {
        var toCloud = new System.IO.Pipelines.Pipe();
        var toInstance = new System.IO.Pipelines.Pipe();
        var instance = System.Net.WebSockets.WebSocket.CreateFromStream(new Duplex(toInstance.Reader.AsStream(), toCloud.Writer.AsStream()), new System.Net.WebSockets.WebSocketCreationOptions { IsServer = false });
        var cloud = System.Net.WebSockets.WebSocket.CreateFromStream(new Duplex(toCloud.Reader.AsStream(), toInstance.Writer.AsStream()), new System.Net.WebSockets.WebSocketCreationOptions { IsServer = true });
        var expected = Enumerable.Range(0, 2 * TunnelClient.StreamPieceBytes + 99).Select(i => (byte)(i % 253)).ToArray();

        var sending = Task.Run(async () =>
        {
            await TunnelClient.SendPiecesAsync(instance, new SemaphoreSlim(1, 1), Id, new MemoryStream(expected), default);
            toCloud.Writer.Complete();
        });

        var received = new MemoryStream();
        var pieces = 0;
        var buffer = new byte[TunnelClient.StreamPieceBytes + 64];
        while (true)
        {
            using var message = new MemoryStream();
            System.Net.WebSockets.WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await cloud.ReceiveAsync(buffer, default);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                break; // the stand-in pipe ended
            }
            if (message.Length == 0)
            {
                break;
            }
            Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, result.MessageType);
            var bytes = message.ToArray();
            Assert.Equal(Id, System.Text.Encoding.ASCII.GetString(bytes, 0, TunnelClient.StreamIdLength));
            received.Write(bytes, TunnelClient.StreamIdLength, bytes.Length - TunnelClient.StreamIdLength);
            if (++pieces == 3)
            {
                break;
            }
        }

        await sending;
        Assert.Equal(3, pieces);
        Assert.Equal(expected, received.ToArray());
    }

    private sealed class Duplex(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcde!")]
    public void An_id_that_cannot_head_a_piece_is_never_streamed(string id) =>
        Assert.False(TunnelClient.ShouldStream(true, id, "application/octet-stream", 45L * 1024 * 1024));
}
