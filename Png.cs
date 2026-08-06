using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace ImageGen;

/// <summary>
/// Minimal dependency-free PNG encoder for 24-bit RGB buffers.
/// Writes a standard 8-bit truecolor PNG (no alpha) with zlib-compressed
/// IDAT data. Good enough for saving generated images without pulling in a
/// commercially-licensed imaging library.
/// </summary>
static class Png
{
    /// <param name="rgb">Tightly packed RGB bytes, length == width * height * 3.</param>
    public static void Save(string path, ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException($"Expected {width * height * 3} RGB bytes, got {rgb.Length}.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = File.Create(path);

        // PNG signature.
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: width, height, bit depth 8, color type 2 (truecolor), no interlace.
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type = truecolor RGB
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(fs, "IHDR", ihdr);

        // Raw scanlines, each prefixed with a filter-type byte (0 = none).
        var stride = width * 3;
        var raw = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (stride + 1);
            raw[dst] = 0; // filter: none
            rgb.Slice(y * stride, stride).CopyTo(raw.AsSpan(dst + 1, stride));
        }

        WriteChunk(fs, "IDAT", ZlibCompress(raw));
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        // zlib header for default compression, 32K window.
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        // zlib trailer: Adler-32 of the uncompressed data, big-endian.
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        ms.Write(adler);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(typeBytes);
        s.Write(data);

        // CRC-32 over type + data.
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        // Crc32.GetCurrentHash yields little-endian; PNG wants big-endian.
        crc.GetCurrentHash(crcBytes);
        crcBytes.Reverse();
        s.Write(crcBytes);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }
}
