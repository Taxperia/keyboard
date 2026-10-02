using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KeyBridge.Services;

internal static class NetworkProtocol
{
    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximumLength, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > maximumLength)
        {
            throw new InvalidDataException("Geçersiz ağ paketi uzunluğu.");
        }

        var payload = new byte[length];
        return await ReadExactlyAsync(stream, payload, cancellationToken) ? payload : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
