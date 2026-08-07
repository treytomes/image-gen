using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace ImageGen.Imaging;

/// <summary>
/// Minimal dependency-free PNG codec for 24-bit RGB images.
///
/// Encoding always writes 8-bit truecolor (no alpha) with a single zlib-compressed
/// IDAT and filter type 0. Decoding supports the common non-interlaced 8-bit cases:
/// truecolor (RGB) and truecolor-with-alpha (RGBA, alpha dropped), with all five
/// scanline filters. Grayscale, palette, 16-bit, and interlaced PNGs are rejected
/// with <see cref="ImageFormatException"/> — enough for our own output plus typical
/// editor exports, without pulling in a commercially-licensed imaging library.
/// </summary>
public sealed class Png : IImageCodec
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // --- Encoding ---------------------------------------------------------------

    /// <param name="rgb">Tightly packed RGB bytes, length == width * height * 3.</param>
    public void Save(string path, ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException($"Expected {width * height * 3} RGB bytes, got {rgb.Length}.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = File.Create(path);

        fs.Write(Signature);

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

    public void Save(string path, RawImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Save(path, image.Pixels, image.Width, image.Height);
    }

    // --- Decoding ---------------------------------------------------------------

    public RawImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    /// <summary>Decodes a PNG from an in-memory buffer. Exposed for testing.</summary>
    public RawImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new ImageFormatException("Not a PNG file (bad signature).");

        var pos = Signature.Length;

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        var sawIhdr = false;
        using var idat = new MemoryStream();

        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(pos, 4));
            if (length < 0 || pos + 12 + length > png.Length)
                throw new ImageFormatException("Truncated or corrupt chunk.");

            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, length);

            if (Matches(type, "IHDR"))
            {
                if (length != 13)
                    throw new ImageFormatException("Invalid IHDR length.");
                width = BinaryPrimitives.ReadInt32BigEndian(data[0..]);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                var interlace = data[12];

                if (width <= 0 || height <= 0)
                    throw new ImageFormatException($"Invalid dimensions {width}x{height}.");
                if (bitDepth != 8)
                    throw new ImageFormatException($"Unsupported bit depth {bitDepth} (only 8 is supported).");
                if (colorType is not (2 or 6))
                    throw new ImageFormatException(
                        $"Unsupported color type {colorType} (only 2=RGB and 6=RGBA are supported).");
                if (interlace != 0)
                    throw new ImageFormatException("Interlaced PNGs are not supported.");
                sawIhdr = true;
            }
            else if (Matches(type, "IDAT"))
            {
                if (!sawIhdr)
                    throw new ImageFormatException("IDAT before IHDR.");
                idat.Write(data);
            }
            else if (Matches(type, "IEND"))
            {
                break;
            }
            // Other chunks (ancillary or unknown) are ignored.

            pos += 12 + length; // length(4) + type(4) + data + crc(4)
        }

        if (!sawIhdr)
            throw new ImageFormatException("Missing IHDR chunk.");
        if (idat.Length == 0)
            throw new ImageFormatException("Missing image data (no IDAT).");

        var channels = colorType == 6 ? 4 : 3;
        var inflated = ZlibDecompress(idat.GetBuffer().AsSpan(0, (int)idat.Length));
        return Unfilter(inflated, width, height, channels);
    }

    /// <summary>Reverses PNG scanline filtering and drops alpha, yielding packed RGB.</summary>
    private static RawImage Unfilter(byte[] data, int width, int height, int channels)
    {
        var stride = width * channels;
        var expected = height * (stride + 1); // each row is prefixed with a filter byte
        if (data.Length < expected)
            throw new ImageFormatException(
                $"Decompressed data too short: got {data.Length}, expected {expected}.");

        // Reconstructed raw scanlines (no filter bytes), in the source channel count.
        var recon = new byte[height * stride];

        for (var y = 0; y < height; y++)
        {
            var filter = data[y * (stride + 1)];
            var srcRow = data.AsSpan(y * (stride + 1) + 1, stride);
            var curRow = recon.AsSpan(y * stride, stride);
            var prevRow = y > 0 ? recon.AsSpan((y - 1) * stride, stride) : default;

            for (var x = 0; x < stride; x++)
            {
                var raw = srcRow[x];
                int a = x >= channels ? curRow[x - channels] : 0;       // left
                int b = prevRow.IsEmpty ? 0 : prevRow[x];               // up
                int c = prevRow.IsEmpty || x < channels ? 0 : prevRow[x - channels]; // up-left

                int recovered = filter switch
                {
                    0 => raw,                                    // None
                    1 => raw + a,                                // Sub
                    2 => raw + b,                                // Up
                    3 => raw + (a + b) / 2,                      // Average
                    4 => raw + Paeth(a, b, c),                   // Paeth
                    _ => throw new ImageFormatException($"Unknown scanline filter {filter}."),
                };
                curRow[x] = (byte)recovered;
            }
        }

        if (channels == 3)
            return new RawImage(recon, width, height);

        // Drop the alpha channel (RGBA -> RGB).
        var rgb = new byte[width * height * 3];
        for (int p = 0, q = 0; p < recon.Length; p += 4, q += 3)
        {
            rgb[q] = recon[p];
            rgb[q + 1] = recon[p + 1];
            rgb[q + 2] = recon[p + 2];
        }
        return new RawImage(rgb, width, height);
    }

    /// <summary>The PNG Paeth predictor.</summary>
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static bool Matches(ReadOnlySpan<byte> type, string name) =>
        type.Length == 4 && type[0] == name[0] && type[1] == name[1]
        && type[2] == name[2] && type[3] == name[3];

    // --- zlib -------------------------------------------------------------------

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

    private static byte[] ZlibDecompress(ReadOnlySpan<byte> zlib)
    {
        if (zlib.Length < 2)
            throw new ImageFormatException("Truncated zlib stream.");

        // Skip the 2-byte zlib header; the 4-byte Adler-32 trailer is ignored (the
        // raw DEFLATE body is all .NET needs, and PNG chunks are CRC-checked anyway).
        using var input = new MemoryStream(zlib[2..].ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // --- chunk / checksum helpers ----------------------------------------------

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
