using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media.Imaging;

namespace ConveyorInspector.Services;

public class CameraService : IDisposable
{
    private const int PreviewWidth = 1920;
    private const int PreviewHeight = 1080;
    private const int PreviewFps = 15;
    private const int DisplayMaxWidth = 1280;
    private const int DisplayFrameIntervalMilliseconds = 100;

    // 8MP급 USB 카메라에서 흔히 쓰는 4:3 해상도입니다.
    // 카메라가 이 해상도를 지원하지 않으면 드라이버가 가장 가까운 값으로 맞출 수 있습니다.
    private const int InspectionCaptureWidth = 3264;
    private const int InspectionCaptureHeight = 2448;
    private const int InspectionDiscardFrames = 3;
    private const int InspectionCandidateFrames = 3;
    private const double InspectionAutoExposure = 0.25;
    private const double InspectionExposure = -6;
    private const double InspectionGain = 0;
    private const double InspectionBrightness = 100;

    private readonly object _captureLock = new();
    private readonly object _frameLock = new();

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Mat? _latestFrame;
    private bool _isRunning;
    private DateTime _lastDisplayFrameTime = DateTime.MinValue;

    public event Action<BitmapSource>? FrameReady;
    public event Action<Mat>? RawFrameReady;
    public bool IsRunning => _isRunning;

    public static List<string> GetAvailableCameras()
    {
        var result = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            using var cap = new VideoCapture(i);
            if (cap.IsOpened())
                result.Add($"Camera {i}");
            else
                break;
        }

        if (result.Count == 0)
            result.Add("Camera 0 (기본)");

        return result;
    }

    public bool Start(int cameraIndex)
    {
        Stop();

        lock (_captureLock)
        {
            _capture = new VideoCapture(cameraIndex);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                _capture = null;
                return false;
            }

            SetPreviewCaptureProperties(_capture);
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;

        try
        {
            _captureTask?.Wait(500);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }

        _captureTask = null;
        _cts?.Dispose();
        _cts = null;

        lock (_captureLock)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
    }

    public Mat? GrabFrame()
    {
        lock (_frameLock)
        {
            if (_latestFrame == null || _latestFrame.Empty())
                return null;

            return _latestFrame.Clone();
        }
    }

    public async Task<bool> WaitUntilFrameStableAsync(
        TimeSpan timeout,
        TimeSpan sampleInterval,
        double motionThreshold,
        int requiredStableSamples,
        CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableSamples = 0;

        using var previousFrame = GrabFrame();
        if (previousFrame == null || previousFrame.Empty())
            return false;

        using var previousMotionFrame = CreateMotionFrame(previousFrame);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(sampleInterval, token).ConfigureAwait(false);

            using var currentFrame = GrabFrame();
            if (currentFrame == null || currentFrame.Empty())
                continue;

            using var currentMotionFrame = CreateMotionFrame(currentFrame);
            var motionScore = CalculateMotionScore(previousMotionFrame, currentMotionFrame);

            if (motionScore <= motionThreshold)
            {
                stableSamples++;
                if (stableSamples >= requiredStableSamples)
                    return true;
            }
            else
            {
                stableSamples = 0;
            }

            currentMotionFrame.CopyTo(previousMotionFrame);
        }

        return false;
    }

    public async Task<Mat?> CaptureHighQualityInspectionFrameAsync(
        int outputWidth,
        int outputHeight,
        CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            lock (_captureLock)
            {
                if (_capture == null || !_capture.IsOpened())
                    return null;

                Mat? bestFrame = null;
                var bestSharpness = double.MinValue;
                var originalProperties = CaptureCameraProperties(_capture);

                try
                {
                    // 검사 순간에만 고해상도로 전환합니다.
                    // 미리보기는 가볍게 유지하고, ONNX 검사 입력만 더 좋은 원본에서 만듭니다.
                    _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                    _capture.Set(VideoCaptureProperties.FrameWidth, InspectionCaptureWidth);
                    _capture.Set(VideoCaptureProperties.FrameHeight, InspectionCaptureHeight);
                    _capture.Set(VideoCaptureProperties.Fps, 5);
                    ApplyInspectionExposureProperties(_capture);

                    using var frame = new Mat();
                    var totalFrames = InspectionDiscardFrames + InspectionCandidateFrames;

                    for (var i = 0; i < totalFrames; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        if (!_capture.Read(frame) || frame.Empty())
                            continue;

                        // 해상도 전환 직후 몇 프레임은 노출/버퍼가 안정되지 않을 수 있어 버립니다.
                        if (i < InspectionDiscardFrames)
                            continue;

                        var sharpness = CalculateSharpness(frame);
                        if (sharpness <= bestSharpness)
                            continue;

                        bestFrame?.Dispose();
                        bestFrame = frame.Clone();
                        bestSharpness = sharpness;
                    }

                    if (bestFrame == null)
                        return null;

                    using (bestFrame)
                    {
                        return PrepareInspectionFrame(bestFrame, outputWidth, outputHeight);
                    }
                }
                finally
                {
                    bestFrame?.Dispose();

                    // 고해상도 촬영이 끝나면 다시 가벼운 미리보기 설정으로 복귀합니다.
                    SetPreviewCaptureProperties(_capture);
                    RestoreCameraProperties(_capture, originalProperties);
                }
            }
        }, token);
    }

    private async Task CaptureLoop(CancellationToken ct)
    {
        using var mat = new Mat();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool readOk;
                lock (_captureLock)
                {
                    readOk = _capture != null && _capture.Read(mat);
                }

                if (!readOk || mat.Empty())
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                    continue;
                }

                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = mat.Clone();
                }

                var rawFrameReady = RawFrameReady;
                if (rawFrameReady != null)
                {
                    using var rawFrame = mat.Clone();
                    rawFrameReady.Invoke(rawFrame);
                }

                if (ShouldPublishDisplayFrame())
                {
                    using var displayFrame = ResizeFrameForDisplay(mat);
                    var bmp = displayFrame.ToBitmapSource();
                    bmp.Freeze();
                    FrameReady?.Invoke(bmp);
                }

                await Task.Delay(1000 / PreviewFps, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldPublishDisplayFrame()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDisplayFrameTime).TotalMilliseconds < DisplayFrameIntervalMilliseconds)
            return false;

        _lastDisplayFrameTime = now;
        return true;
    }

    private static Mat ResizeFrameForDisplay(Mat source)
    {
        if (source.Width <= DisplayMaxWidth)
            return source.Clone();

        var scale = DisplayMaxWidth / (double)source.Width;
        var displayWidth = DisplayMaxWidth;
        var displayHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        var resized = new Mat();
        Cv2.Resize(
            source,
            resized,
            new OpenCvSharp.Size(displayWidth, displayHeight),
            0,
            0,
            InterpolationFlags.Area
        );

        return resized;
    }

    private static Mat CreateMotionFrame(Mat source)
    {
        const int motionWidth = 320;

        using var bgr = ConvertToBgr(source);
        var scale = motionWidth / (double)bgr.Width;
        var motionHeight = Math.Max(1, (int)Math.Round(bgr.Height * scale));

        using var resized = new Mat();
        Cv2.Resize(
            bgr,
            resized,
            new OpenCvSharp.Size(motionWidth, motionHeight),
            0,
            0,
            InterpolationFlags.Area
        );

        var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static double CalculateMotionScore(Mat previous, Mat current)
    {
        using var diff = new Mat();
        Cv2.Absdiff(previous, current, diff);
        return Cv2.Mean(diff).Val0;
    }

    private static void SetPreviewCaptureProperties(VideoCapture capture)
    {
        capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        capture.Set(VideoCaptureProperties.FrameWidth, PreviewWidth);
        capture.Set(VideoCaptureProperties.FrameHeight, PreviewHeight);
        capture.Set(VideoCaptureProperties.Fps, PreviewFps);
        ApplyInspectionExposureProperties(capture);
    }

    private static CameraPropertySnapshot CaptureCameraProperties(VideoCapture capture)
    {
        return new CameraPropertySnapshot(
            capture.Get(VideoCaptureProperties.AutoExposure),
            capture.Get(VideoCaptureProperties.Exposure),
            capture.Get(VideoCaptureProperties.Gain),
            capture.Get(VideoCaptureProperties.Brightness),
            capture.Get(VideoCaptureProperties.Contrast),
            capture.Get(VideoCaptureProperties.Saturation)
        );
    }

    private static void RestoreCameraProperties(VideoCapture capture, CameraPropertySnapshot snapshot)
    {
        TrySetCameraProperty(capture, VideoCaptureProperties.AutoExposure, snapshot.AutoExposure);
        TrySetCameraProperty(capture, VideoCaptureProperties.Exposure, snapshot.Exposure);
        TrySetCameraProperty(capture, VideoCaptureProperties.Gain, snapshot.Gain);
        TrySetCameraProperty(capture, VideoCaptureProperties.Brightness, snapshot.Brightness);
        TrySetCameraProperty(capture, VideoCaptureProperties.Contrast, snapshot.Contrast);
        TrySetCameraProperty(capture, VideoCaptureProperties.Saturation, snapshot.Saturation);
    }

    private static void ApplyInspectionExposureProperties(VideoCapture capture)
    {
        // 카메라 드라이버마다 값 범위가 다를 수 있습니다.
        // 현재 값은 "밝은 wafer가 살짝 덜 날아가게" 하는 시작점입니다.
        TrySetCameraProperty(capture, VideoCaptureProperties.AutoExposure, InspectionAutoExposure);
        TrySetCameraProperty(capture, VideoCaptureProperties.Exposure, InspectionExposure);
        TrySetCameraProperty(capture, VideoCaptureProperties.Gain, InspectionGain);
        TrySetCameraProperty(capture, VideoCaptureProperties.Brightness, InspectionBrightness);
    }

    private static void TrySetCameraProperty(VideoCapture capture, VideoCaptureProperties property, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return;

        capture.Set(property, value);
    }

    private static double CalculateSharpness(Mat frame)
    {
        using var gray = new Mat();
        using var laplacian = new Mat();

        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);

        return stddev.Val0 * stddev.Val0;
    }

    private static Mat PrepareInspectionFrame(Mat source, int outputWidth, int outputHeight)
    {
        if (source.Empty())
            throw new InvalidOperationException("검사용 카메라 프레임이 비어 있습니다.");

        if (source.Width <= 0 || source.Height <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new InvalidOperationException("검사용 이미지 크기가 올바르지 않습니다.");

        // 학습 이미지가 넓은 장면 전체를 640x640으로 만든 형태라면 crop하면 구도가 달라집니다.
        // 그래서 원본 전체 장면을 유지하되, 비율을 보존한 letterbox 방식으로 1024x1024에 맞춥니다.
        using var bgrSource = ConvertToBgr(source);
        var scale = Math.Min(outputWidth / (double)source.Width, outputHeight / (double)source.Height);
        var resizedWidth = Math.Clamp((int)Math.Floor(source.Width * scale), 1, outputWidth);
        var resizedHeight = Math.Clamp((int)Math.Floor(source.Height * scale), 1, outputHeight);
        var offsetX = Math.Max(0, (outputWidth - resizedWidth) / 2);
        var offsetY = Math.Max(0, (outputHeight - resizedHeight) / 2);

        using var resized = new Mat();
        Cv2.Resize(
            bgrSource,
            resized,
            new OpenCvSharp.Size(resizedWidth, resizedHeight),
            0,
            0,
            InterpolationFlags.Area
        );

        var letterboxed = new Mat(
            new OpenCvSharp.Size(outputWidth, outputHeight),
            MatType.CV_8UC3,
            new Scalar(114, 114, 114)
        );

        var roi = new Rect(offsetX, offsetY, resizedWidth, resizedHeight);
        using var target = new Mat(letterboxed, roi);
        resized.CopyTo(target);

        return letterboxed;
    }

    private static Mat ConvertToBgr(Mat source)
    {
        if (source.Channels() == 3 && source.Depth() == MatType.CV_8U)
            return source.Clone();

        var converted = new Mat();

        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGR);
        }
        else if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, converted, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            source.ConvertTo(converted, MatType.CV_8UC3);
        }

        return converted;
    }

    private readonly record struct CameraPropertySnapshot(
        double AutoExposure,
        double Exposure,
        double Gain,
        double Brightness,
        double Contrast,
        double Saturation
    );

    public void Dispose()
    {
        Stop();
    }
}
