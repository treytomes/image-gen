namespace ImageGen.Generation;

/// <summary>Outcome of a completed generation.</summary>
/// <param name="OutputPath">Absolute path to the saved PNG.</param>
/// <param name="LoadSeconds">Seconds spent loading the model.</param>
/// <param name="GenerationSeconds">Seconds spent sampling.</param>
public readonly record struct GenerationResult(string OutputPath, double LoadSeconds, double GenerationSeconds);
