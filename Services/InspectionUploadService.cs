using System.IO;
using System.Net.Http;

namespace ConveyorInspector.Services;

public sealed class InspectionUploadService : IDisposable
{
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("http://localhost:5050"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task UploadAsync(InspectionSaveResult saved, CancellationToken cancellationToken = default)
    {
        await _uploadLock.WaitAsync(cancellationToken);

        try
        {
            await UploadCoreAsync(saved, cancellationToken);
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private async Task UploadCoreAsync(InspectionSaveResult saved, CancellationToken cancellationToken)
    {
        if (saved.OriginalImagePath == null ||
            saved.ResultImagePath == null ||
            !File.Exists(saved.ResultJsonPath) ||
            !File.Exists(saved.OriginalImagePath) ||
            !File.Exists(saved.ResultImagePath))
        {
            throw new FileNotFoundException("업로드할 검사 결과 파일이 부족합니다.");
        }

        using var form = new MultipartFormDataContent();
        AddFile(form, "json", saved.ResultJsonPath, "application/json");
        AddFile(form, "original", saved.OriginalImagePath, "image/jpeg");
        AddFile(form, "result", saved.ResultImagePath, "image/jpeg");

        using var response = await _httpClient.PostAsync("/api/inspections", form, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void AddFile(
        MultipartFormDataContent form,
        string name,
        string path,
        string contentType)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(content, name, Path.GetFileName(path));
    }

    public void Dispose()
    {
        _uploadLock.Dispose();
        _httpClient.Dispose();
    }
}
