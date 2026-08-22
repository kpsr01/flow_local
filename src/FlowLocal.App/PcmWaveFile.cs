using System.Buffers.Binary;
using System.IO;

namespace FlowLocal.App;

public enum WaveRepairResult
{
    Repaired,
    Invalid,
    Unavailable,
}

public sealed class PcmWaveFile : IDisposable
{
    private const int HeaderSize = 44;
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;
    private readonly FileStream _stream;
    private int _dataLength;
    private bool _disposed;

    public PcmWaveFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        WriteHeader(_stream, 0);
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((pcm.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 data must contain complete samples.", nameof(pcm));
        }

        var dataLength = checked(_dataLength + pcm.Length);
        if (dataLength > int.MaxValue - 36)
        {
            throw new IOException("The WAV file is too large.");
        }

        _stream.Write(pcm);
        _dataLength = dataLength;
        UpdateHeader();
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((pcm.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 data must contain complete samples.", nameof(pcm));
        }

        var dataLength = checked(_dataLength + pcm.Length);
        if (dataLength > int.MaxValue - 36)
        {
            throw new IOException("The WAV file is too large.");
        }

        await _stream.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
        _dataLength = dataLength;
        UpdateHeader();
    }

    public static WaveRepairResult TryRepair(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length < HeaderSize || stream.Length - HeaderSize > int.MaxValue - 36 ||
                ((stream.Length - HeaderSize) & 1) != 0)
            {
                return WaveRepairResult.Invalid;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (!IsPcmHeader(header))
            {
                return WaveRepairResult.Invalid;
            }

            var expectedLength = checked((int)stream.Length - HeaderSize);
            var riffLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(header[40..44]);
            if (riffLength < 36 || dataLength < 0 || (dataLength & 1) != 0 ||
                riffLength != dataLength + 36 || dataLength > expectedLength)
            {
                return WaveRepairResult.Invalid;
            }

            WriteHeader(stream, expectedLength);
            stream.Flush(flushToDisk: true);
            return WaveRepairResult.Repaired;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WaveRepairResult.Unavailable;
        }
    }

    public static FileStream OpenData(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = File.OpenRead(path);
        try
        {
            Validate(stream);
            stream.Position = HeaderSize;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static byte[] ReadData(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = OpenData(path);
        var data = new byte[checked((int)(stream.Length - HeaderSize))];
        stream.ReadExactly(data);
        return data;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UpdateHeader();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UpdateHeader()
    {
        var position = _stream.Position;
        WriteHeader(_stream, _dataLength);
        _stream.Position = position;
        _stream.Flush(flushToDisk: true);
    }

    private static void WriteHeader(Stream stream, int dataLength)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], checked(dataLength + 36));
        "WAVEfmt "u8.CopyTo(header[8..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..22], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..24], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..34], Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..36], BitsPerSample);
        "data"u8.CopyTo(header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..44], dataLength);
        stream.Position = 0;
        stream.Write(header);
    }

    private static void Validate(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The WAV header is incomplete.", exception);
        }

        if (!IsPcmHeader(header))
        {
            throw new InvalidDataException("The file is not canonical PCM16 mono 16 kHz WAV audio.");
        }

        var riffLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(header[40..44]);
        if (dataLength < 0 || (dataLength & 1) != 0 || (long)riffLength != (long)dataLength + 36 || stream.Length != (long)dataLength + HeaderSize)
        {
            throw new InvalidDataException("The WAV header or data length is corrupt.");
        }
    }

    private static bool IsPcmHeader(ReadOnlySpan<byte> header) =>
        header[..4].SequenceEqual("RIFF"u8) &&
        header[8..12].SequenceEqual("WAVE"u8) &&
        header[12..16].SequenceEqual("fmt "u8) &&
        BinaryPrimitives.ReadInt32LittleEndian(header[16..20]) == 16 &&
        BinaryPrimitives.ReadInt16LittleEndian(header[20..22]) == 1 &&
        BinaryPrimitives.ReadInt16LittleEndian(header[22..24]) == Channels &&
        BinaryPrimitives.ReadInt32LittleEndian(header[24..28]) == SampleRate &&
        BinaryPrimitives.ReadInt32LittleEndian(header[28..32]) == SampleRate * Channels * BitsPerSample / 8 &&
        BinaryPrimitives.ReadInt16LittleEndian(header[32..34]) == Channels * BitsPerSample / 8 &&
        BinaryPrimitives.ReadInt16LittleEndian(header[34..36]) == BitsPerSample &&
        header[36..40].SequenceEqual("data"u8);
}
