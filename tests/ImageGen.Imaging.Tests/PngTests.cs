using System.Buffers.Binary;
using ImageGen.Imaging;

namespace ImageGen.Imaging.Tests;

public class PngTests
{
    /// <summary>Builds a small deterministic RGB gradient for test input.</summary>
    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 3;
            rgb[i] = (byte)(x * 255 / Math.Max(1, width - 1));
            rgb[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
            rgb[i + 2] = 128;
        }
        return rgb;
    }

    private static string SaveTemp(byte[] rgb, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pngtest-{Guid.NewGuid():N}.png");
        Png.Save(path, rgb, width, height);
        return path;
    }

    [Fact]
    public void Save_WritesPngSignature()
    {
        var path = SaveTemp(Gradient(4, 4), 4, 4);
        try
        {
            var bytes = File.ReadAllBytes(path);
            byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            Assert.True(bytes.Length > signature.Length);
            Assert.Equal(signature, bytes[..8]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesIhdrWithCorrectDimensions()
    {
        const int width = 7, height = 3;
        var path = SaveTemp(Gradient(width, height), width, height);
        try
        {
            var bytes = File.ReadAllBytes(path);
            // IHDR is the first chunk after the 8-byte signature: 4-byte length,
            // 4-byte "IHDR" type, then width/height as big-endian int32s.
            var ihdrData = bytes.AsSpan(16); // 8 sig + 4 len + 4 type
            Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(ihdrData));
            Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(ihdrData[4..]));
            Assert.Equal(8, ihdrData[8]);  // bit depth
            Assert.Equal(2, ihdrData[9]);  // color type = truecolor RGB
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_RejectsBufferOfWrongLength()
    {
        // 4x4 needs 48 bytes; give it 47.
        Assert.Throws<ArgumentException>(() => Png.Save(
            Path.Combine(Path.GetTempPath(), "should-not-exist.png"),
            new byte[47], 4, 4));
    }

    // Anchor for the PNG decoder (TODO): once Png.Load exists, this round-trip is the
    // strongest correctness test — encode then decode must return the original pixels.
    // Skipped until the decoder lands so the suite stays green.
    [Fact(Skip = "Enable when Png.Load (decoder) is implemented.")]
    public void Load_RoundTripsSavedImage()
    {
        // const int width = 16, height = 9;
        // var original = Gradient(width, height);
        // var path = SaveTemp(original, width, height);
        // var (rgb, w, h) = Png.Load(path);
        // Assert.Equal(width, w);
        // Assert.Equal(height, h);
        // Assert.Equal(original, rgb);
    }
}
