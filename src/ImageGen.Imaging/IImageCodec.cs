namespace ImageGen.Imaging;

/// <summary>
/// Encodes and decodes images to and from a specific file format. Implementations
/// are stateless and thread-safe; register one per format for dependency injection.
/// </summary>
public interface IImageCodec
{
    /// <summary>Encodes tightly-packed 24-bit RGB pixels to <paramref name="path"/>,
    /// creating the directory if needed.</summary>
    /// <param name="rgb">RGB bytes, length must equal <paramref name="width"/> × <paramref name="height"/> × 3.</param>
    void Save(string path, ReadOnlySpan<byte> rgb, int width, int height);

    /// <summary>Encodes a <see cref="RawImage"/> to <paramref name="path"/>.</summary>
    void Save(string path, RawImage image);

    /// <summary>Decodes the image at <paramref name="path"/> into 24-bit RGB pixels.</summary>
    /// <exception cref="ImageFormatException">The file is not a valid image of this
    /// codec's format, or uses a feature the codec does not support.</exception>
    RawImage Load(string path);
}

/// <summary>Thrown when an image file is malformed or uses an unsupported feature.</summary>
public sealed class ImageFormatException(string message) : Exception(message);
