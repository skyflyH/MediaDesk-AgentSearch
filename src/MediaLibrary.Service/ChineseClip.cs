using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MediaLibrary.Service;

internal interface IPhotoEncoder : IDisposable
{
    string Version { get; }
    float[] Image(Stream file);
    float[] Text(string query);
}

internal sealed class ChineseClip : IPhotoEncoder
{
    internal const string Model = "OFA-Sys/chinese-clip-vit-base-patch16";
    internal const string SourceRevision = "36e679e65c2a2fead755ae21162091293ad37834";
    internal const int TextLength = 52;
    internal const int Dimensions = 512;
    private const string PreprocessingRevision = "1";
    private const string VocabularyHash = "45BBAC6B341C319ADC98A532532882E91A9CEFC0329AA57BAC9AE761C27B291C";
    private readonly InferenceSession image;
    private readonly InferenceSession text;
    private readonly ChineseClipTokenizer tokenizer;
    public string Version { get; }

    public ChineseClip(string directory)
    {
        var files = new[] { "image.onnx", "text.onnx", "vocab.txt" };
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Photo model manifest is empty.");
        if (manifest.Model != Model || manifest.Revision != SourceRevision || manifest.ImageSize != ChineseClipPixels.Size ||
            manifest.TextLength != TextLength || manifest.Dimensions != Dimensions || manifest.Opset != 17 ||
            manifest.Resize != "pillow-bicubic-rgb" || manifest.ImageMean?.SequenceEqual(ChineseClipPixels.Mean) != true ||
            manifest.ImageStd?.SequenceEqual(ChineseClipPixels.Std) != true || manifest.Sha256 is null)
            throw new InvalidDataException("Unexpected photo model manifest.");
        var hashes = new List<string>();
        foreach (var name in files)
        {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!manifest.Sha256.TryGetValue(name, out var expected) || !hash.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                name == "vocab.txt" && hash != VocabularyHash)
                throw new InvalidDataException("Photo model digest mismatch.");
            hashes.Add(hash);
        }
        Version = "ChineseCLIP-" + SourceRevision[..8] + "-" + PreprocessingRevision + "-" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(string.Concat(hashes))))[..16];
        tokenizer = new ChineseClipTokenizer(Path.Combine(directory, "vocab.txt"));
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            EnableCpuMemArena = false
        };
        image = Open("image.onnx", [("pixel_values", typeof(float), new[] { 1, 3, 224, 224 })]);
        try
        {
            text = Open("text.onnx", [("input_ids", typeof(long), new[] { 1, TextLength }),
                ("attention_mask", typeof(long), new[] { 1, TextLength })]);
        }
        catch { image.Dispose(); throw; }

        InferenceSession Open(string name, (string Name, Type Type, int[] Shape)[] inputs)
        {
            var session = new InferenceSession(Path.Combine(directory, name), options);
            if (session.InputMetadata.Count == inputs.Length && inputs.All(input =>
                    session.InputMetadata.TryGetValue(input.Name, out var metadata) && metadata.ElementType == input.Type &&
                    metadata.Dimensions.SequenceEqual(input.Shape)) &&
                session.OutputMetadata.TryGetValue("embedding", out var output) && output.ElementType == typeof(float) &&
                output.Dimensions.SequenceEqual(new[] { 1, Dimensions }))
                return session;
            session.Dispose();
            throw new InvalidDataException("Unexpected ONNX input or output shape.");
        }
    }

    public float[] Image(Stream file)
    {
        var tensor = new DenseTensor<float>(ChineseClipPixels.Decode(file), [1, 3, 224, 224]);
        return Run(image, [NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);
    }

    public float[] Text(string query)
    {
        var (ids, mask) = tokenizer.Encode(query, TextLength);
        return Run(text, [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, TextLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, TextLength]))
        ]);
    }

    private static float[] Run(InferenceSession session, NamedOnnxValue[] inputs)
    {
        using var outputs = session.Run(inputs, ["embedding"]);
        return Normalize(outputs.Single().AsTensor<float>().ToArray());
    }

    internal static float[] Normalize(float[] values)
    {
        var magnitude = Math.Sqrt(values.Sum(value => (double)value * value));
        if (!double.IsFinite(magnitude) || magnitude < 1e-8)
            throw new InvalidDataException("Photo model produced an invalid vector.");
        for (var index = 0; index < values.Length; index++) values[index] = (float)(values[index] / magnitude);
        return values;
    }

    public void Dispose()
    {
        image.Dispose();
        text.Dispose();
    }

    private sealed record Manifest(string Model, string Revision, int Opset, int ImageSize, int TextLength,
        int Dimensions, float[]? ImageMean, float[]? ImageStd, string Resize, Dictionary<string, string>? Sha256);
}
