using System;
using System.IO;

namespace TimeGrapher.Core.AudioIo;

/// <summary>
/// Streams 32-bit IEEE float PCM data to a WAV file incrementally.
/// Port of the Qt WavStreamWriter: writes a 44-byte placeholder header on Open(),
/// then patches the RIFF chunkSize and data chunkSize on Close().
///
/// Usage:
///   var writer = new WavStreamWriter();
///   writer.Open("output.wav", 48000, 1);
///   writer.Write(buffer);   // call as many times as needed
///   writer.Close();         // patches the header with final sizes
/// </summary>
public sealed class WavStreamWriter : IDisposable
{
    // WAV format constants.
    private const ushort KFmtIeeeFloat = 3;     // WAVE_FORMAT_IEEE_FLOAT
    private const ushort KBitsPerSample = 32;   // 32-bit float
    private const int KHeaderSize = 44;         // bytes in a standard WAV header

    private FileStream? _file;

    private int _sampleRate;
    private int _channels;
    private ulong _bytesWritten;  // audio bytes only (after header)

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool IsOpen => _file != null;

    public ulong FramesWritten
    {
        get
        {
            if (_channels == 0) return 0;
            return _bytesWritten / (sizeof(float) * (ulong)_channels);
        }
    }

    /// <summary>
    /// Opens the file and writes a placeholder WAV header.
    /// Returns true on success.
    /// </summary>
    public bool Open(string filePath, int sampleRate, int channels)
    {
        if (_file != null)
        {
            Console.Error.WriteLine("WavStreamWriter: already open, call close() first");
            return false;
        }

        if (sampleRate <= 0 || channels <= 0)
        {
            Console.Error.WriteLine("WavStreamWriter: invalid sampleRate or channels");
            return false;
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _bytesWritten = 0;

        try
        {
            _file = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WavStreamWriter: cannot open file: " + ex.Message);
            _file = null;
            return false;
        }

        return WriteHeader();
    }

    /// <summary>
    /// Appends interleaved float samples to the file (count = frames * channels).
    /// Samples must be in [-1.0, 1.0]. Returns true on success.
    /// </summary>
    public bool Write(ReadOnlySpan<float> samples)
    {
        if (_file == null)
        {
            Console.Error.WriteLine("WavStreamWriter: not open");
            return false;
        }
        if (samples.Length <= 0)
            return true; // nothing to do, not an error

        // Write each float in native IEEE-754 little-endian format.
        Span<byte> word = stackalloc byte[4];
        try
        {
            for (int i = 0; i < samples.Length; ++i)
            {
                int bits = BitConverter.SingleToInt32Bits(samples[i]);
                word[0] = (byte)bits;
                word[1] = (byte)(bits >> 8);
                word[2] = (byte)(bits >> 16);
                word[3] = (byte)(bits >> 24);
                _file.Write(word);
            }
        }
        catch
        {
            Console.Error.WriteLine("WavStreamWriter: write error");
            return false;
        }

        _bytesWritten += (ulong)samples.Length * sizeof(float);
        return true;
    }

    /// <summary>
    /// Finalises the file by patching the RIFF and data chunk sizes, then closes it.
    /// Returns true on success.
    /// </summary>
    public bool Close()
    {
        if (_file == null)
            return true;

        if (!PatchHeader())
        {
            Console.Error.WriteLine("WavStreamWriter: failed to patch header");
            _file.Dispose();
            _file = null;
            return false;
        }

        _file.Dispose();
        _file = null;
        return true;
    }

    public void Dispose()
    {
        if (_file != null)
            Close();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes a standard 44-byte WAV header with placeholder sizes (patched on close).
    /// All values little-endian.
    /// </summary>
    private bool WriteHeader()
    {
        if (_file == null) return false;

        ushort blockAlign = (ushort)(_channels * sizeof(float));
        uint byteRate = (uint)(_sampleRate * blockAlign);
        const uint placeholder = 0;

        try
        {
            // RIFF chunk
            WriteFourCc("RIFF");
            WriteU32(placeholder);            // chunkSize - patched later
            WriteFourCc("WAVE");

            // fmt sub-chunk
            WriteFourCc("fmt ");
            WriteU32(16);                     // fmtChunkSize
            WriteU16(KFmtIeeeFloat);
            WriteU16((ushort)_channels);
            WriteU32((uint)_sampleRate);
            WriteU32(byteRate);
            WriteU16(blockAlign);
            WriteU16(KBitsPerSample);

            // data sub-chunk
            WriteFourCc("data");
            WriteU32(placeholder);            // dataChunkSize - patched later
        }
        catch
        {
            Console.Error.WriteLine("WavStreamWriter: error writing header");
            return false;
        }

        return _file.Position == KHeaderSize;
    }

    /// <summary>
    /// Seeks back and overwrites the two size fields in the WAV header.
    ///   chunkSize @ offset 4  = (KHeaderSize - 8) + bytesWritten
    ///   dataSize  @ offset 40 = bytesWritten
    /// </summary>
    private bool PatchHeader()
    {
        if (_file == null) return false;

        uint dataSize = (uint)_bytesWritten;
        uint chunkSize = (uint)((KHeaderSize - 8) + (long)_bytesWritten);

        try
        {
            _file.Seek(4, SeekOrigin.Begin);
            WriteU32(chunkSize);

            _file.Seek(40, SeekOrigin.Begin);
            WriteU32(dataSize);
        }
        catch
        {
            return false;
        }

        return true;
    }

    private void WriteFourCc(string fourCc)
    {
        // ASCII 4-char tag.
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)fourCc[0];
        b[1] = (byte)fourCc[1];
        b[2] = (byte)fourCc[2];
        b[3] = (byte)fourCc[3];
        _file!.Write(b);
    }

    private void WriteU16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        b[0] = (byte)v;
        b[1] = (byte)(v >> 8);
        _file!.Write(b);
    }

    private void WriteU32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)v;
        b[1] = (byte)(v >> 8);
        b[2] = (byte)(v >> 16);
        b[3] = (byte)(v >> 24);
        _file!.Write(b);
    }
}
