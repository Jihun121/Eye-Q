namespace ConveyorInspector.Models;

public enum InspectionResult
{
    Normal,
    Defect,
    NoDetection
}

public enum InspectionStatus
{
    Idle,
    Running,
    Pass,
    Fail
}

public class InspectionRecord
{
    public InspectionStatus Status { get; set; }
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Judgment => Status == InspectionStatus.Pass ? "OK" : "NG";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public List<InspectionDetection> Detections { get; set; } = [];
    public string OriginalImagePath { get; set; } = string.Empty;
    public string ResultImagePath { get; set; } = string.Empty;
    public string ResultJsonPath { get; set; } = string.Empty;
    public bool HasResultImage => !string.IsNullOrWhiteSpace(ResultImagePath);

    public string StatusText => Status switch
    {
        InspectionStatus.Pass => "정상",
        InspectionStatus.Fail => "불량",
        InspectionStatus.Running => "검사 중",
        _ => "대기"
    };
}

public sealed class InspectionDetection
{
    public int ClassIndex { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public bool IsDefect { get; set; }
    public bool IsAccepted { get; set; }
    public InspectionBoundingBox Bbox { get; set; } = new();
}

public sealed class InspectionBoundingBox
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
