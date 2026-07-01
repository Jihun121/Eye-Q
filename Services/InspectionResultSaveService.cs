using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConveyorInspector.Models;
using OpenCvSharp;

namespace ConveyorInspector.Services;

public sealed class InspectionResultSaveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string RootDirectory { get; }

    public InspectionResultSaveService(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "InspectionResults")
            : rootDirectory;
    }

    public async Task<InspectionSaveResult> SaveAsync(
        InspectionOutput output,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = output.Result;
            var folderName = $"{record.Timestamp:yyyyMMdd_HHmmss_fff}_{record.Judgment}";
            var resultDirectory = Path.Combine(RootDirectory, folderName);

            Directory.CreateDirectory(resultDirectory);

            string? originalImagePath = SaveImageIfAvailable(
                output.OriginalFrame,
                resultDirectory,
                "original.jpg");

            string? resultImagePath = SaveImageIfAvailable(
                output.AnnotatedFrame,
                resultDirectory,
                "result.jpg");

            var resultJsonPath = Path.Combine(resultDirectory, "result.json");

            record.OriginalImagePath = originalImagePath ?? string.Empty;
            record.ResultImagePath = resultImagePath ?? string.Empty;
            record.ResultJsonPath = resultJsonPath;

            var jsonPayload = new InspectionResultJson
            {
                Timestamp = record.Timestamp.ToString("O"),
                Judgment = record.Judgment,
                Label = record.Label,
                Confidence = record.Confidence
            };

            var json = JsonSerializer.Serialize(jsonPayload, JsonOptions);
            File.WriteAllText(resultJsonPath, json);

            return new InspectionSaveResult(
                resultDirectory,
                originalImagePath,
                resultImagePath,
                resultJsonPath);
        }, cancellationToken);
    }

    private static string? SaveImageIfAvailable(Mat? image, string directory, string fileName)
    {
        if (image == null || image.Empty())
            return null;

        var path = Path.Combine(directory, fileName);
        Cv2.ImWrite(path, image);
        return path;
    }

    private sealed class InspectionResultJson
    {
        public string Timestamp { get; init; } = string.Empty;
        public string Judgment { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public float Confidence { get; init; }
    }
}

public sealed record InspectionSaveResult(
    string ResultDirectory,
    string? OriginalImagePath,
    string? ResultImagePath,
    string ResultJsonPath);
