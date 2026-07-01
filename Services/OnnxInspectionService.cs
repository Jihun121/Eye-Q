using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ConveyorInspector.Models;
using System.Diagnostics;

namespace ConveyorInspector.Services;

/// <summary>
/// ONNX 모델을 사용하여 YOLO 기반 객체 탐지 검사를 수행합니다.
/// 모델 미로드 시 더미 추론(데모 모드)으로 동작합니다.
///
/// 지원 입력 형식:
/// - [1, 3, H, W]
/// - NCHW
/// - float32
/// - RGB
/// - 0~1 정규화
///
/// 현재 코드는 output0 기준으로 bbox/class 정보를 읽고,
/// OpenCV 창에 검출 결과를 표시합니다.
/// </summary>
public class OnnxInspectionService : IDisposable
{
    private InferenceSession? _session;
    private bool _isDemoMode = true;

    private string _inputName = "images";

    public bool IsModelLoaded => !_isDemoMode;

    public int InputWidth { get; set; } = 1024;
    public int InputHeight { get; set; } = 1024;
    public double? LastWaferMeanBrightness { get; private set; }
    public double? LastWaferMaxBrightness { get; private set; }

    /// <summary>
    /// confidence 기준값입니다.
    /// 너무 낮으면 오탐이 늘고, 너무 높으면 미탐이 늘어납니다.
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.25f;

    /// <summary>
    /// wafer 내부 결함으로 인정할 최소 confidence입니다.
    /// 배경 텍스처가 낮은 점수로 scratch처럼 잡히는 오탐을 줄이기 위해 일반 기준보다 높게 둡니다.
    /// </summary>
    public float DefectConfidenceThreshold { get; set; } = 0.40f;

    /// <summary>
    /// scratch/crack처럼 얇고 방향 영향을 많이 받는 결함에만 적용하는 낮은 기준값입니다.
    /// 전체 불량 기준을 낮추면 오탐이 늘 수 있어서 얇은 선형 결함만 따로 민감하게 봅니다.
    /// </summary>
    public float ThinDefectConfidenceThreshold { get; set; } = 0.25f;

    /// <summary>
    /// NMS IoU 기준값입니다.
    /// 같은 클래스 박스가 많이 겹칠 때 하나만 남기는 용도입니다.
    /// </summary>
    public float IouThreshold { get; set; } = 0.45f;

    /// <summary>
    /// OpenCV 결과 창 표시 여부입니다.
    /// </summary>
    /// <summary>
    /// 너무 많은 박스를 그리지 않도록 제한합니다.
    /// </summary>
    public int MaxDetectionsToDraw { get; set; } = 50;

    /// <summary>
    /// 클래스 이름 목록입니다.
    /// Roboflow data.yaml의 names 순서와 반드시 같아야 합니다.
    /// 현재 Roboflow 기준:
    /// 0 contamination
    /// 1 crack
    /// 2 edge_chipping
    /// 3 pattern_defect
    /// 4 scratch
    /// 5 wafer
    /// </summary>
    public string[] ClassNames { get; set; } =
    {
        "contamination",
        "crack",
        "edge_chipping",
        "pattern_defect",
        "scratch",
        "wafer"
    };

    /// <summary>
    /// 불량으로 판정할 클래스 인덱스입니다.
    /// 0~4는 불량, 5 wafer는 정상으로 처리합니다.
    /// </summary>
    public HashSet<int> DefectClassIndices { get; set; } = new()
    {
        0, 1, 2, 3, 4
    };

    public HashSet<int> ThinDefectClassIndices { get; set; } = new()
    {
        1, 4
    };

    public int WaferClassIndex { get; set; } = 5;

    private readonly Random _rnd = new();

    private sealed class YoloDetection
    {
        public int ClassIndex { get; init; }
        public string Label { get; init; } = "";
        public float Confidence { get; init; }
        public float X1 { get; init; }
        public float Y1 { get; init; }
        public float X2 { get; init; }
        public float Y2 { get; init; }
        public bool IsDefect { get; init; }
    }

    public bool LoadModel(string modelPath)
    {
        try
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                EnableMemoryPattern = false,
                EnableCpuMemArena = false,
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1
            };

            _session = new InferenceSession(modelPath, options);
            _isDemoMode = false;

            ReadModelInputShape();
            PrintModelMetadata();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ONNX 모델 로드 실패: {ex.Message}");

            _session?.Dispose();
            _session = null;
            _isDemoMode = true;

            return false;
        }
    }

    private void ReadModelInputShape()
    {
        if (_session == null)
            return;

        var inputMeta = _session.InputMetadata.First();

        _inputName = inputMeta.Key;

        var dims = inputMeta.Value.Dimensions.ToArray();

        // 일반적인 YOLO 입력: [1, 3, H, W]
        if (dims.Length == 4)
        {
            if (dims[2] > 0)
                InputHeight = dims[2];

            if (dims[3] > 0)
                InputWidth = dims[3];
        }
    }

    private void PrintModelMetadata()
    {
        if (_session == null)
            return;

        Console.WriteLine("=== ONNX Inputs ===");

        foreach (var input in _session.InputMetadata)
        {
            var dims = input.Value.Dimensions.ToArray();
            Console.WriteLine($"{input.Key}: {string.Join(", ", dims)}");
        }

        Console.WriteLine("=== ONNX Outputs ===");

        foreach (var output in _session.OutputMetadata)
        {
            var dims = output.Value.Dimensions.ToArray();
            Console.WriteLine($"{output.Key}: {string.Join(", ", dims)}");
        }

        Console.WriteLine($"사용 입력 크기: {InputWidth} x {InputHeight}");
    }

    public void UnloadModel()
    {
        _session?.Dispose();
        _session = null;
        _isDemoMode = true;
    }

    /// <summary>
    /// Mat 이미지를 검사하여 InspectionRecord를 반환합니다.
    /// </summary>
    public async Task<InspectionRecord> InspectAsync(Mat frame)
    {
        using var output = await InspectWithResultFrameAsync(frame);
        return output.Result;
    }

    public async Task<InspectionOutput> InspectWithResultFrameAsync(Mat frame)
    {
        if (_isDemoMode)
            return await DemoInspectAsync(frame);

        return await Task.Run(() => RunOnnx(frame));
    }

    private InspectionOutput RunOnnx(Mat frame)
    {
        LastWaferMeanBrightness = null;
        LastWaferMaxBrightness = null;

        if (_session == null)
        {
            return CreateOutput(CreateResult(
                InspectionStatus.Fail,
                "ONNX 세션 없음",
                0.0f
            ));
        }

        if (frame.Empty())
        {
            return CreateOutput(CreateResult(
                InspectionStatus.Fail,
                "입력 이미지 없음",
                0.0f
            ));
        }

        List<YoloDetection> detections;

        try
        {
            detections = RunInferenceDetections(frame);
        }
        catch (Exception ex) when (
            ex is OutOfMemoryException ||
            ex.Message.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
        {
            return CreateOutput(CreateResult(
                InspectionStatus.Fail,
                "ONNX 메모리 부족",
                0.0f
            ));
        }

        InspectionRecord inspectionResult = CreateInspectionResultFromDetections(detections);

        UpdateWaferBrightnessStats(frame, detections);
        PopulateInspectionDetails(inspectionResult, detections, frame);

        var annotatedFrame = frame.Clone();
        DrawDetections(annotatedFrame, detections, inspectionResult);

        return CreateOutput(inspectionResult, annotatedFrame, frame.Clone());
    }

    private List<YoloDetection> RunInferenceDetections(Mat frame)
    {
        if (_session == null)
            return new List<YoloDetection>();

        // 1. 전처리
        // 현재는 단순 Resize 방식입니다.
        // 이 방식은 박스 그리기는 쉽지만, 원본 비율이 크게 다르면 좌표가 약간 왜곡될 수 있습니다.
        // 실전에서는 LetterBox 방식으로 바꾸는 것이 더 좋습니다.
        using var resized = new Mat();
        Cv2.Resize(frame, resized, new OpenCvSharp.Size(InputWidth, InputHeight));

        using var floatMat = new Mat();
        resized.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);

        // 2. NHWC(OpenCV Mat) → NCHW(ONNX Tensor)
        // OpenCV는 BGR, YOLO 입력은 RGB 기준입니다.
        int h = InputHeight;
        int w = InputWidth;

        var inputTensor = new DenseTensor<float>(new[] { 1, 3, h, w });

        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                Vec3f pixel = floatMat.At<Vec3f>(row, col);

                float b = pixel.Item0;
                float g = pixel.Item1;
                float r = pixel.Item2;

                inputTensor[0, 0, row, col] = r;
                inputTensor[0, 1, row, col] = g;
                inputTensor[0, 2, row, col] = b;
            }
        }

        // 3. ONNX 추론
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);

        using (results)
        {
            Debug.WriteLine("=== Runtime Outputs ===");

            foreach (var result in results)
            {
                var tensor = result.AsTensor<float>();
                var dims = tensor.Dimensions.ToArray();

                Debug.WriteLine($"{result.Name}: {string.Join(", ", dims)}");
            }

            // YOLO Seg 모델이라도 output0에 bbox/class 정보가 들어있으므로 첫 번째 output만 사용합니다.
            var outputTensor = results.First().AsTensor<float>();
            return ParseYoloDetections(outputTensor);
        }
    }

    private List<YoloDetection> ParseYoloDetections(Tensor<float> outputTensor)
    {
        var dims = outputTensor.Dimensions.ToArray();

        Debug.WriteLine($"YOLO output shape: {string.Join(", ", dims)}");

        if (dims.Length != 3)
        {
            Debug.WriteLine($"지원하지 않는 YOLO 출력 차원: {string.Join(", ", dims)}");
            return new List<YoloDetection>();
        }

        int dim1 = dims[1];
        int dim2 = dims[2];

        /*
         * 현재 YOLO26 ONNX 출력:
         * output0: [1, 300, 38]
         *
         * 의미:
         * 300 = 최대 detection 개수
         * 38 = x1, y1, x2, y2, confidence, classIndex, mask coefficient 32개
         *
         * 따라서 dim2가 6 이상이면 최종 detection output으로 처리한다.
         */
        if (dim2 >= 6 && dim1 <= 1000)
        {
            return ParseFinalYoloDetections(outputTensor, boxesFirst: true);
        }

        /*
         * 혹시 export 방식에 따라 [1, 38, 300] 형태로 나오는 경우 대응
         */
        if (dim1 >= 6 && dim2 <= 1000)
        {
            return ParseFinalYoloDetections(outputTensor, boxesFirst: false);
        }

        Debug.WriteLine($"알 수 없는 YOLO 출력 형태: {string.Join(", ", dims)}");
        return new List<YoloDetection>();
    }

    private List<YoloDetection> ParseFinalYoloDetections(Tensor<float> outputTensor, bool boxesFirst)
    {
        var dims = outputTensor.Dimensions.ToArray();

        int boxCount = boxesFirst ? dims[1] : dims[2];

        var detections = new List<YoloDetection>();

        for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
        {
            float x1;
            float y1;
            float x2;
            float y2;
            float confidence;
            float classValue;

            if (boxesFirst)
            {
                // [1, 300, 38]
                x1 = outputTensor[0, boxIndex, 0];
                y1 = outputTensor[0, boxIndex, 1];
                x2 = outputTensor[0, boxIndex, 2];
                y2 = outputTensor[0, boxIndex, 3];
                confidence = outputTensor[0, boxIndex, 4];
                classValue = outputTensor[0, boxIndex, 5];
            }
            else
            {
                // [1, 38, 300]
                x1 = outputTensor[0, 0, boxIndex];
                y1 = outputTensor[0, 1, boxIndex];
                x2 = outputTensor[0, 2, boxIndex];
                y2 = outputTensor[0, 3, boxIndex];
                confidence = outputTensor[0, 4, boxIndex];
                classValue = outputTensor[0, 5, boxIndex];
            }

            confidence = Math.Clamp(confidence, 0.0f, 1.0f);

            if (confidence < ConfidenceThreshold)
                continue;

            int classIndex = (int)MathF.Round(classValue);

            if (classIndex < 0 || classIndex >= ClassNames.Length)
                continue;

            YoloDetection? detection = CreateDetection(
                classIndex,
                confidence,
                x1,
                y1,
                x2,
                y2
            );

            if (detection != null)
                detections.Add(detection);
        }

        Debug.WriteLine($"Final detections: {detections.Count}");

        return detections
            .OrderByDescending(d => d.Confidence)
            .Take(MaxDetectionsToDraw)
            .ToList();
    }

    private List<YoloDetection> ParseRawYoloDetections(Tensor<float> outputTensor)
    {
        var dims = outputTensor.Dimensions.ToArray();

        int dim1 = dims[1];
        int dim2 = dims[2];

        int classCount = ClassNames.Length;
        int minAttributeCount = 4 + classCount;

        // [1, attributes, boxes]
        bool attributesFirst = dim1 >= minAttributeCount && dim1 < dim2;

        // [1, boxes, attributes]
        bool boxesFirst = dim2 >= minAttributeCount && dim2 < dim1;

        if (!attributesFirst && !boxesFirst)
        {
            Console.WriteLine($"YOLO 출력 크기 불일치: {string.Join(", ", dims)} / ClassCount={classCount}");
            return new List<YoloDetection>();
        }

        int boxCount = attributesFirst ? dim2 : dim1;

        var detections = new List<YoloDetection>();

        for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
        {
            float cx = ReadRawValue(outputTensor, attributesFirst, boxIndex, 0);
            float cy = ReadRawValue(outputTensor, attributesFirst, boxIndex, 1);
            float bw = ReadRawValue(outputTensor, attributesFirst, boxIndex, 2);
            float bh = ReadRawValue(outputTensor, attributesFirst, boxIndex, 3);

            int bestClassIndex = -1;
            float bestConfidence = 0.0f;

            for (int classIndex = 0; classIndex < classCount; classIndex++)
            {
                float classScore = ReadRawValue(outputTensor, attributesFirst, boxIndex, 4 + classIndex);

                if (classScore > bestConfidence)
                {
                    bestConfidence = classScore;
                    bestClassIndex = classIndex;
                }
            }

            if (bestClassIndex < 0)
                continue;

            bestConfidence = Math.Clamp(bestConfidence, 0.0f, 1.0f);

            if (bestConfidence < ConfidenceThreshold)
                continue;

            // Raw YOLO 출력은 보통 cx, cy, w, h 형식입니다.
            var box = ConvertCxCyWhToXyxy(cx, cy, bw, bh);

            YoloDetection? detection = CreateDetection(
                bestClassIndex,
                bestConfidence,
                box.x1,
                box.y1,
                box.x2,
                box.y2
            );

            if (detection != null)
                detections.Add(detection);
        }

        return detections;
    }

    private float ReadRawValue(
        Tensor<float> outputTensor,
        bool attributesFirst,
        int boxIndex,
        int attributeIndex)
    {
        if (attributesFirst)
        {
            // [1, attributes, boxes]
            return outputTensor[0, attributeIndex, boxIndex];
        }

        // [1, boxes, attributes]
        return outputTensor[0, boxIndex, attributeIndex];
    }

    private List<YoloDetection> ParseNmsYoloDetections(Tensor<float> outputTensor, bool boxesFirst)
    {
        var dims = outputTensor.Dimensions.ToArray();

        int boxCount = boxesFirst ? dims[1] : dims[2];

        var detections = new List<YoloDetection>();

        for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
        {
            float x1;
            float y1;
            float x2;
            float y2;
            float value4;
            float value5;

            if (boxesFirst)
            {
                // [1, boxes, 6]
                x1 = outputTensor[0, boxIndex, 0];
                y1 = outputTensor[0, boxIndex, 1];
                x2 = outputTensor[0, boxIndex, 2];
                y2 = outputTensor[0, boxIndex, 3];
                value4 = outputTensor[0, boxIndex, 4];
                value5 = outputTensor[0, boxIndex, 5];
            }
            else
            {
                // [1, 6, boxes]
                x1 = outputTensor[0, 0, boxIndex];
                y1 = outputTensor[0, 1, boxIndex];
                x2 = outputTensor[0, 2, boxIndex];
                y2 = outputTensor[0, 3, boxIndex];
                value4 = outputTensor[0, 4, boxIndex];
                value5 = outputTensor[0, 5, boxIndex];
            }

            var parsed = ParseClassAndConfidence(value4, value5);

            int classIndex = parsed.classIndex;
            float confidence = parsed.confidence;

            confidence = Math.Clamp(confidence, 0.0f, 1.0f);

            if (confidence < ConfidenceThreshold)
                continue;

            YoloDetection? detection = CreateDetection(
                classIndex,
                confidence,
                x1,
                y1,
                x2,
                y2
            );

            if (detection != null)
                detections.Add(detection);
        }

        return detections;
    }

    private (int classIndex, float confidence) ParseClassAndConfidence(float value4, float value5)
    {
        /*
         * NMS 포함 ONNX 출력은 export 옵션에 따라 두 가지가 나올 수 있습니다.
         *
         * A) x1, y1, x2, y2, confidence, classIndex
         * B) x1, y1, x2, y2, classIndex, confidence
         *
         * 이 함수는 value4, value5 중 무엇이 confidence이고 classIndex인지 자동 판단합니다.
         */

        bool value4LooksLikeClass = IsClassIndexLike(value4);
        bool value5LooksLikeClass = IsClassIndexLike(value5);

        bool value4LooksLikeScore = IsScoreLike(value4);
        bool value5LooksLikeScore = IsScoreLike(value5);

        // B) classIndex, confidence
        if (value4LooksLikeClass && value5LooksLikeScore && !IsIntegerLike(value5))
        {
            return ((int)MathF.Round(value4), value5);
        }

        // A) confidence, classIndex
        if (value4LooksLikeScore && value5LooksLikeClass && !IsIntegerLike(value4))
        {
            return ((int)MathF.Round(value5), value4);
        }

        // classIndex가 5, confidence가 0.98 같은 경우
        if (value4 > 1.0f && value5LooksLikeScore)
        {
            return ((int)MathF.Round(value4), value5);
        }

        // confidence가 0.98, classIndex가 5 같은 경우
        if (value4LooksLikeScore && value5 > 1.0f)
        {
            return ((int)MathF.Round(value5), value4);
        }

        // 애매한 경우 기본값:
        // 기존 코드 호환을 위해 value4=confidence, value5=classIndex로 처리
        return ((int)MathF.Round(value5), value4);
    }

    private bool IsClassIndexLike(float value)
    {
        return value >= 0 &&
               value < ClassNames.Length &&
               IsIntegerLike(value);
    }

    private bool IsIntegerLike(float value)
    {
        return Math.Abs(value - MathF.Round(value)) < 0.001f;
    }

    private bool IsScoreLike(float value)
    {
        return value >= 0.0f && value <= 1.0f;
    }

    private (float x1, float y1, float x2, float y2) ConvertCxCyWhToXyxy(
        float cx,
        float cy,
        float width,
        float height)
    {
        // 좌표가 0~1 정규화 값이면 입력 크기 기준 픽셀 좌표로 변환합니다.
        bool normalized =
            Math.Abs(cx) <= 1.5f &&
            Math.Abs(cy) <= 1.5f &&
            Math.Abs(width) <= 1.5f &&
            Math.Abs(height) <= 1.5f;

        if (normalized)
        {
            cx *= InputWidth;
            width *= InputWidth;

            cy *= InputHeight;
            height *= InputHeight;
        }

        float x1 = cx - width / 2.0f;
        float y1 = cy - height / 2.0f;
        float x2 = cx + width / 2.0f;
        float y2 = cy + height / 2.0f;

        return (x1, y1, x2, y2);
    }

    private YoloDetection? CreateDetection(
        int classIndex,
        float confidence,
        float x1,
        float y1,
        float x2,
        float y2)
    {
        if (classIndex < 0 || classIndex >= ClassNames.Length)
            return null;

        // 좌표가 0~1 정규화 값이면 입력 크기 기준 픽셀 좌표로 변환합니다.
        bool normalized =
            Math.Abs(x1) <= 1.5f &&
            Math.Abs(y1) <= 1.5f &&
            Math.Abs(x2) <= 1.5f &&
            Math.Abs(y2) <= 1.5f;

        if (normalized)
        {
            x1 *= InputWidth;
            x2 *= InputWidth;

            y1 *= InputHeight;
            y2 *= InputHeight;
        }

        x1 = Math.Clamp(x1, 0.0f, InputWidth - 1.0f);
        y1 = Math.Clamp(y1, 0.0f, InputHeight - 1.0f);
        x2 = Math.Clamp(x2, 0.0f, InputWidth - 1.0f);
        y2 = Math.Clamp(y2, 0.0f, InputHeight - 1.0f);

        float boxWidth = x2 - x1;
        float boxHeight = y2 - y1;

        if (boxWidth <= 1 || boxHeight <= 1)
            return null;

        string label = GetClassName(classIndex);
        bool isDefect = DefectClassIndices.Contains(classIndex);

        return new YoloDetection
        {
            ClassIndex = classIndex,
            Label = label,
            Confidence = confidence,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            IsDefect = isDefect
        };
    }

    private List<YoloDetection> ApplyNms(List<YoloDetection> detections)
    {
        var ordered = detections
            .OrderByDescending(d => d.Confidence)
            .ToList();

        var selected = new List<YoloDetection>();

        foreach (var detection in ordered)
        {
            bool shouldSuppress = selected.Any(selectedDetection =>
                selectedDetection.ClassIndex == detection.ClassIndex &&
                CalculateIoU(selectedDetection, detection) > IouThreshold
            );

            if (!shouldSuppress)
                selected.Add(detection);

            if (selected.Count >= MaxDetectionsToDraw)
                break;
        }

        return selected;
    }

    private float CalculateIoU(YoloDetection a, YoloDetection b)
    {
        float interX1 = MathF.Max(a.X1, b.X1);
        float interY1 = MathF.Max(a.Y1, b.Y1);
        float interX2 = MathF.Min(a.X2, b.X2);
        float interY2 = MathF.Min(a.Y2, b.Y2);

        float interWidth = MathF.Max(0, interX2 - interX1);
        float interHeight = MathF.Max(0, interY2 - interY1);
        float interArea = interWidth * interHeight;

        float areaA = MathF.Max(0, a.X2 - a.X1) * MathF.Max(0, a.Y2 - a.Y1);
        float areaB = MathF.Max(0, b.X2 - b.X1) * MathF.Max(0, b.Y2 - b.Y1);

        float unionArea = areaA + areaB - interArea;

        if (unionArea <= 0)
            return 0;

        return interArea / unionArea;
    }

    private InspectionRecord CreateInspectionResultFromDetections(List<YoloDetection> detections)
    {
        var bestWafer = detections
            .Where(d => d.ClassIndex == WaferClassIndex)
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        if (bestWafer == null)
        {
            return CreateResult(
                InspectionStatus.Fail,
                "탐지 없음",
                0.0f
            );
        }

        var bestDefectInsideWafer = detections
            .Where(d => d.IsDefect)
            .Where(d => d.Confidence >= GetRequiredDefectConfidence(d))
            .Where(d => IsDetectionCenterInside(d, bestWafer))
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        if (bestDefectInsideWafer != null)
        {
            return CreateResult(
                InspectionStatus.Fail,
                bestDefectInsideWafer.Label,
                bestDefectInsideWafer.Confidence
            );
        }

        return CreateResult(
            InspectionStatus.Pass,
            bestWafer.Label,
            bestWafer.Confidence
        );
    }

    private float GetRequiredDefectConfidence(YoloDetection detection)
    {
        if (ThinDefectClassIndices.Contains(detection.ClassIndex))
            return ThinDefectConfidenceThreshold;

        return DefectConfidenceThreshold;
    }

    private static bool IsDetectionCenterInside(YoloDetection detection, YoloDetection container)
    {
        var centerX = (detection.X1 + detection.X2) / 2.0f;
        var centerY = (detection.Y1 + detection.Y2) / 2.0f;

        return centerX >= container.X1 &&
               centerX <= container.X2 &&
               centerY >= container.Y1 &&
               centerY <= container.Y2;
    }

    private void UpdateWaferBrightnessStats(Mat frame, List<YoloDetection> detections)
    {
        var bestWafer = detections
            .Where(d => d.ClassIndex == WaferClassIndex)
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        if (bestWafer == null)
            return;

        var x = (int)Math.Clamp(MathF.Floor(bestWafer.X1), 0, frame.Width - 1);
        var y = (int)Math.Clamp(MathF.Floor(bestWafer.Y1), 0, frame.Height - 1);
        var right = (int)Math.Clamp(MathF.Ceiling(bestWafer.X2), x + 1, frame.Width);
        var bottom = (int)Math.Clamp(MathF.Ceiling(bestWafer.Y2), y + 1, frame.Height);
        var roi = new Rect(x, y, right - x, bottom - y);

        using var wafer = new Mat(frame, roi);
        using var gray = new Mat();
        Cv2.CvtColor(wafer, gray, ColorConversionCodes.BGR2GRAY);

        LastWaferMeanBrightness = Cv2.Mean(gray).Val0;
        Cv2.MinMaxLoc(gray, out double _, out var maxValue);
        LastWaferMaxBrightness = maxValue;
    }

    private void PopulateInspectionDetails(
        InspectionRecord result,
        List<YoloDetection> detections,
        Mat frame)
    {
        result.ImageWidth = frame.Width;
        result.ImageHeight = frame.Height;
        result.Detections = CreateStandardDetections(detections, frame);
    }

    private List<InspectionDetection> CreateStandardDetections(
        List<YoloDetection> detections,
        Mat frame)
    {
        int imageWidth = Math.Max(1, frame.Width);
        int imageHeight = Math.Max(1, frame.Height);
        float scaleX = imageWidth / (float)InputWidth;
        float scaleY = imageHeight / (float)InputHeight;

        var bestWafer = detections
            .Where(d => d.ClassIndex == WaferClassIndex)
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        return detections
            .Select(detection =>
            {
                float x1 = Math.Clamp(detection.X1 * scaleX, 0.0f, imageWidth - 1.0f);
                float y1 = Math.Clamp(detection.Y1 * scaleY, 0.0f, imageHeight - 1.0f);
                float x2 = Math.Clamp(detection.X2 * scaleX, 0.0f, imageWidth - 1.0f);
                float y2 = Math.Clamp(detection.Y2 * scaleY, 0.0f, imageHeight - 1.0f);

                return new InspectionDetection
                {
                    ClassIndex = detection.ClassIndex,
                    ClassName = detection.Label,
                    Confidence = detection.Confidence,
                    IsDefect = detection.IsDefect,
                    IsAccepted = IsAcceptedDetection(detection, bestWafer),
                    Bbox = new InspectionBoundingBox
                    {
                        X = x1,
                        Y = y1,
                        Width = Math.Max(0.0f, x2 - x1),
                        Height = Math.Max(0.0f, y2 - y1)
                    }
                };
            })
            .ToList();
    }

    private bool IsAcceptedDetection(YoloDetection detection, YoloDetection? bestWafer)
    {
        if (detection.ClassIndex == WaferClassIndex)
            return true;

        return detection.IsDefect &&
               bestWafer != null &&
               detection.Confidence >= GetRequiredDefectConfidence(detection) &&
               IsDetectionCenterInside(detection, bestWafer);
    }

    private void DrawDetections(
        Mat displayFrame,
        List<YoloDetection> detections,
        InspectionRecord inspectionResult)
    {
        Scalar statusColor = inspectionResult.Status == InspectionStatus.Pass
            ? new Scalar(0, 255, 0)
            : new Scalar(0, 0, 255);

        string statusText =
            $"{inspectionResult.Judgment} | {inspectionResult.Label} | {inspectionResult.Confidence * 100.0f:0.0}%";

        int drawWidth = Math.Max(1, displayFrame.Width);
        int drawHeight = Math.Max(1, displayFrame.Height);
        float scaleX = drawWidth / (float)InputWidth;
        float scaleY = drawHeight / (float)InputHeight;

        Cv2.PutText(
            displayFrame,
            statusText,
            new Point(20, 40),
            HersheyFonts.HersheySimplex,
            1.0,
            statusColor,
            2
        );

        var bestWafer = detections
            .Where(d => d.ClassIndex == WaferClassIndex)
            .OrderByDescending(d => d.Confidence)
            .FirstOrDefault();

        foreach (var detection in detections)
        {
            var validDefect =
                detection.IsDefect &&
                bestWafer != null &&
                detection.Confidence >= GetRequiredDefectConfidence(detection) &&
                IsDetectionCenterInside(detection, bestWafer);

            Scalar color = detection.ClassIndex == WaferClassIndex
                ? new Scalar(0, 255, 0)
                : validDefect
                    ? new Scalar(0, 0, 255)
                    : new Scalar(160, 160, 160);

            int x1 = (int)Math.Clamp(detection.X1 * scaleX, 0, drawWidth - 1);
            int y1 = (int)Math.Clamp(detection.Y1 * scaleY, 0, drawHeight - 1);
            int x2 = (int)Math.Clamp(detection.X2 * scaleX, 0, drawWidth - 1);
            int y2 = (int)Math.Clamp(detection.Y2 * scaleY, 0, drawHeight - 1);

            int boxWidth = Math.Max(1, x2 - x1);
            int boxHeight = Math.Max(1, y2 - y1);

            var rect = new Rect(x1, y1, boxWidth, boxHeight);

            Cv2.Rectangle(
                displayFrame,
                rect,
                color,
                2
            );

            string labelText = detection.IsDefect && !validDefect
                ? $"ignored {detection.Label} {detection.Confidence * 100.0f:0.0}%"
                : $"{detection.Label} {detection.Confidence * 100.0f:0.0}%";

            int baseline;
            var textSize = Cv2.GetTextSize(
                labelText,
                HersheyFonts.HersheySimplex,
                0.6,
                2,
                out baseline
            );

            int textX = x1;
            int textY = Math.Max(0, y1 - textSize.Height - baseline - 4);

            var textBackground = new Rect(
                textX,
                textY,
                Math.Min(textSize.Width + 8, drawWidth - textX),
                textSize.Height + baseline + 8
            );

            Cv2.Rectangle(
                displayFrame,
                textBackground,
                color,
                -1
            );

            Cv2.PutText(
                displayFrame,
                labelText,
                new Point(textX + 4, textY + textSize.Height + 2),
                HersheyFonts.HersheySimplex,
                0.6,
                new Scalar(255, 255, 255),
                2
            );
        }
    }

    private string GetClassName(int classIndex)
    {
        if (classIndex >= 0 && classIndex < ClassNames.Length)
            return ClassNames[classIndex];

        return $"Class{classIndex}";
    }

    private InspectionRecord CreateResult(
        InspectionStatus status,
        string label,
        float confidence)
    {
        confidence = Math.Clamp(confidence, 0.0f, 1.0f);

        return new InspectionRecord
        {
            Status = status,
            Label = label,
            Confidence = confidence,
            Timestamp = DateTime.Now
        };
    }

    private InspectionOutput CreateOutput(
        InspectionRecord result,
        Mat? annotatedFrame = null,
        Mat? originalFrame = null)
    {
        return new InspectionOutput
        {
            Result = result,
            AnnotatedFrame = annotatedFrame,
            OriginalFrame = originalFrame
        };
    }

    private async Task<InspectionOutput> DemoInspectAsync(Mat frame)
    {
        await Task.Delay(600);

        bool isDefect = _rnd.NextDouble() < 0.3;

        var result = new InspectionRecord
        {
            Status = isDefect ? InspectionStatus.Fail : InspectionStatus.Pass,
            Label = isDefect ? "Demo defect" : "Demo normal",
            Confidence = (float)(_rnd.NextDouble() * 0.2 + 0.78),
            Timestamp = DateTime.Now
        };

        Mat? annotatedFrame = null;
        Mat? originalFrame = null;
        if (!frame.Empty())
        {
            result.ImageWidth = frame.Width;
            result.ImageHeight = frame.Height;
            originalFrame = frame.Clone();
            annotatedFrame = frame.Clone();
            DrawDetections(annotatedFrame, new List<YoloDetection>(), result);
        }

        return CreateOutput(result, annotatedFrame, originalFrame);
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}

public sealed class InspectionOutput : IDisposable
{
    public InspectionRecord Result { get; init; } = new();
    public Mat? OriginalFrame { get; init; }
    public Mat? AnnotatedFrame { get; init; }

    public void Dispose()
    {
        OriginalFrame?.Dispose();
        AnnotatedFrame?.Dispose();
    }
}
