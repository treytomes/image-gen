namespace ImageGen.Imaging;

/// <summary>
/// An uncompressed image as tightly-packed 24-bit RGB pixels: three bytes per
/// pixel (R, G, B), row-major, no padding. <see cref="Pixels"/> length is always
/// <see cref="Width"/> × <see cref="Height"/> × 3.
/// </summary>
public sealed class RawImage
{
    public RawImage(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid dimensions {width}x{height}.");
        if (pixels.Length != width * height * 3)
            throw new ArgumentException(
                $"Expected {width * height * 3} RGB bytes for {width}x{height}, got {pixels.Length}.");

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    /// <summary>Tightly-packed RGB bytes (length == Width × Height × 3).</summary>
    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }
}
