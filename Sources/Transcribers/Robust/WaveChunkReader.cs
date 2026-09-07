using NAudio.Wave;

namespace WhisperCLI.Transcribers.Robust;

public static class WaveChunkReader
{
    public static TimeSpan GetDuration(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        return reader.TotalTime;
    }

    public static MemoryStream ReadChunk(string wavPath, TimeSpan start, TimeSpan end)
    {
        using var reader = new WaveFileReader(wavPath);

        start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        end = end > reader.TotalTime ? reader.TotalTime : end;
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Chunk end must be after chunk start.");
        }

        reader.CurrentTime = start;
        long bytesRequested = (long)Math.Ceiling((end - start).TotalSeconds * reader.WaveFormat.AverageBytesPerSecond);
        bytesRequested -= bytesRequested % reader.WaveFormat.BlockAlign;

        using MemoryStream temp = new();
        using (var writer = new WaveFileWriter(temp, reader.WaveFormat))
        {
            byte[] buffer = new byte[64 * 1024];
            long remaining = bytesRequested;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = reader.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        return new MemoryStream(temp.ToArray(), writable: false);
    }
}
