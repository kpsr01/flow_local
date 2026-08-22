using System.Buffers.Binary;
using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class PcmWaveFileTests
{
    [Fact]
    public void WriteAndReadData_RoundTripsCanonicalPcm16Mono16Khz()
    {
        var path = Path.GetTempFileName();
        var pcm = new byte[] { 0, 0, 255, 127, 0, 128, 52, 18 };

        try
        {
            using (var wave = new PcmWaveFile(path))
            {
                wave.Write(pcm.AsSpan(0, 4));
                wave.Write(pcm.AsSpan(4));
            }

            var file = File.ReadAllBytes(path);
            Assert.Equal("RIFF"u8.ToArray(), file[..4]);
            Assert.Equal("WAVEfmt "u8.ToArray(), file[8..16]);
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(20, 2)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(22, 2)));
            Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24, 4)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(34, 2)));
            Assert.Equal("data"u8.ToArray(), file[36..40]);
            Assert.Equal(pcm, PcmWaveFile.ReadData(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadData_RejectsIncompleteHeader()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "RIFF"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => PcmWaveFile.ReadData(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadData_RejectsIncompatibleFormat()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (new PcmWaveFile(path)) { }
            var file = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24, 4), 48000);
            File.WriteAllBytes(path, file);

            Assert.Throws<InvalidDataException>(() => PcmWaveFile.ReadData(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
