using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5050");

var app = builder.Build();

var root = app.Environment.ContentRootPath;
var uploadRoot = Path.Combine(root, "uploads");
var dbPath = Path.Combine(root, "inspection.db");

Directory.CreateDirectory(uploadRoot);

InitializeDatabase(dbPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadRoot),
    RequestPath = "/uploads"
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/inspections", () =>
{
    var inspections = new List<InspectionListItem>();

    using var connection = OpenConnection(dbPath);
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, timestamp, judgment, label, confidence, original_image_path, result_image_path, json_path, created_at
        FROM inspections
        ORDER BY id DESC
        LIMIT 200;
        """;

    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        inspections.Add(new InspectionListItem
        {
            Id = reader.GetInt64(0),
            Timestamp = reader.GetString(1),
            Judgment = reader.GetString(2),
            Label = reader.GetString(3),
            Confidence = reader.GetDouble(4),
            OriginalImageUrl = ToUploadUrl(uploadRoot, reader.GetString(5)),
            ResultImageUrl = ToUploadUrl(uploadRoot, reader.GetString(6)),
            JsonUrl = ToUploadUrl(uploadRoot, reader.GetString(7)),
            CreatedAt = reader.GetString(8)
        });
    }

    return Results.Ok(inspections);
});

app.MapPost("/api/inspections", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "multipart/form-data 요청이 필요합니다." });
    }

    var form = await request.ReadFormAsync();
    var jsonFile = form.Files.GetFile("json");
    var originalFile = form.Files.GetFile("original");
    var resultFile = form.Files.GetFile("result");

    if (jsonFile == null || originalFile == null || resultFile == null)
    {
        return Results.BadRequest(new { message = "json, original, result 파일을 모두 보내야 합니다." });
    }

    InspectionUploadJson payload;
    await using (var jsonStream = jsonFile.OpenReadStream())
    {
        payload = await JsonSerializer.DeserializeAsync<InspectionUploadJson>(
            jsonStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? new InspectionUploadJson();
    }

    if (string.IsNullOrWhiteSpace(payload.Timestamp))
    {
        payload.Timestamp = DateTimeOffset.Now.ToString("O");
    }

    var folderName = CreateUploadFolderName(payload);
    var inspectionDirectory = Path.Combine(uploadRoot, folderName);
    Directory.CreateDirectory(inspectionDirectory);

    var jsonPath = Path.Combine(inspectionDirectory, "result.json");
    var originalPath = Path.Combine(inspectionDirectory, "original.jpg");
    var resultPath = Path.Combine(inspectionDirectory, "result.jpg");

    await SaveFormFileAsync(jsonFile, jsonPath);
    await SaveFormFileAsync(originalFile, originalPath);
    await SaveFormFileAsync(resultFile, resultPath);

    long id;
    using (var connection = OpenConnection(dbPath))
    using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            INSERT INTO inspections
                (timestamp, judgment, label, confidence, original_image_path, result_image_path, json_path, created_at)
            VALUES
                ($timestamp, $judgment, $label, $confidence, $original_image_path, $result_image_path, $json_path, $created_at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestamp", payload.Timestamp);
        command.Parameters.AddWithValue("$judgment", payload.Judgment ?? "");
        command.Parameters.AddWithValue("$label", payload.Label ?? "");
        command.Parameters.AddWithValue("$confidence", payload.Confidence);
        command.Parameters.AddWithValue("$original_image_path", originalPath);
        command.Parameters.AddWithValue("$result_image_path", resultPath);
        command.Parameters.AddWithValue("$json_path", jsonPath);
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.Now.ToString("O"));

        id = (long)(command.ExecuteScalar() ?? 0L);
    }

    return Results.Ok(new
    {
        id,
        originalImageUrl = ToUploadUrl(uploadRoot, originalPath),
        resultImageUrl = ToUploadUrl(uploadRoot, resultPath),
        jsonUrl = ToUploadUrl(uploadRoot, jsonPath)
    });
});

app.Run();

static void InitializeDatabase(string dbPath)
{
    using var connection = OpenConnection(dbPath);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS inspections (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            judgment TEXT NOT NULL,
            label TEXT NOT NULL,
            confidence REAL NOT NULL,
            original_image_path TEXT NOT NULL,
            result_image_path TEXT NOT NULL,
            json_path TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        """;
    command.ExecuteNonQuery();
}

static SqliteConnection OpenConnection(string dbPath)
{
    var connection = new SqliteConnection($"Data Source={dbPath}");
    connection.Open();
    return connection;
}

static async Task SaveFormFileAsync(IFormFile file, string path)
{
    await using var input = file.OpenReadStream();
    await using var output = File.Create(path);
    await input.CopyToAsync(output);
}

static string CreateUploadFolderName(InspectionUploadJson payload)
{
    var judgment = string.IsNullOrWhiteSpace(payload.Judgment) ? "UNKNOWN" : payload.Judgment.Trim();

    if (DateTimeOffset.TryParse(payload.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
    {
        return $"{timestamp:yyyyMMdd_HHmmss_fff}_{SanitizePathPart(judgment)}";
    }

    return $"{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{SanitizePathPart(judgment)}";
}

static string SanitizePathPart(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
}

static string ToUploadUrl(string uploadRoot, string path)
{
    var relative = Path.GetRelativePath(uploadRoot, path).Replace('\\', '/');
    return $"/uploads/{relative}";
}

public sealed class InspectionUploadJson
{
    public string Timestamp { get; set; } = "";
    public string? Judgment { get; set; }
    public string? Label { get; set; }
    public double Confidence { get; set; }
}

public sealed class InspectionListItem
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string Judgment { get; set; } = "";
    public string Label { get; set; } = "";
    public double Confidence { get; set; }
    public string OriginalImageUrl { get; set; } = "";
    public string ResultImageUrl { get; set; } = "";
    public string JsonUrl { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
