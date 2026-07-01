using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConveyorInspector.Models;
using ConveyorInspector.Services;
using Microsoft.Win32;
using OpenCvSharp.WpfExtensions;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows.Media.Imaging;

namespace ConveyorInspector.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    // ── Services ──────────────────────────────────────────────
    private readonly CameraService      _camera  = new();
    private readonly OnnxInspectionService _onnx = new();
    private readonly DobotProcessService _dobot = new();
    private readonly InspectionResultSaveService _resultSaveService = new();
    private readonly InspectionUploadService _uploadService = new();
    private ArduinoMotorService         _motor;
    private readonly MotorSettings      _motorSettings = new();
    private readonly Queue<InspectionResult> _inspectionQueue = new();
    private CancellationTokenSource? _autoInspectionCts;
    private const int InspectionMoveRotations = 13;
    private const int MaxInspectionHistory = 100;
    private const int ConveyorMinimumSettleMilliseconds = 900;
    private static readonly TimeSpan ConveyorStillTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ConveyorStillSampleInterval = TimeSpan.FromMilliseconds(150);
    private const double ConveyorStillMotionThreshold = 2.5;
    private const int ConveyorRequiredStableSamples = 3;
    private int _cameraFrameUpdatePending;

    // ── Observable Properties ──────────────────────────────────

    [ObservableProperty] private BitmapSource? _cameraFrame;
    [ObservableProperty] private BitmapSource? _inspectionOverlayFrame;
    [ObservableProperty] private string _lastInspectionImagePath = string.Empty;
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private bool _isInspectionOverlayVisible;
    [ObservableProperty] private bool _hasLastInspectionImage;
    [ObservableProperty] private bool _isImagePreviewOpen;
    [ObservableProperty] private string _statusText  = "준비";
    [ObservableProperty] private string _statusColor = "#89B4FA"; // Blue

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCameraRunning;
    [ObservableProperty] private bool _isMotorConnected;
    [ObservableProperty] private bool _isModelLoaded;
    [ObservableProperty] private bool _isDobotConnected;
    [ObservableProperty] private bool _isEmergencyStopped;
    [ObservableProperty] private bool _isAutoInspectionRunning;

    // 설정
    [ObservableProperty] private string _selectedCamera = "Camera 0";
    [ObservableProperty] private string _selectedPort   = "";
    [ObservableProperty] private string _selectedDobotPort = "";
    [ObservableProperty] private string _dobotStatusText = "Dobot 상태: 미연결";
    [ObservableProperty] private int    _baudRate       = 115200;
    [ObservableProperty] private string _onnxModelPath  = "(모델 미선택 - 데모 모드)";
    [ObservableProperty] private int    _rotations      = 3;

    // 검사 결과
    [ObservableProperty] private string _lastResult    = "—";
    [ObservableProperty] private string _lastResultColor = "#A6ADC8";
    [ObservableProperty] private float  _confidence;
    [ObservableProperty] private int    _totalPass;
    [ObservableProperty] private int    _totalFail;

    // 통계
    public int TotalInspected => TotalPass + TotalFail;
    public double PassRate => TotalInspected == 0 ? 0 : (double)TotalPass / TotalInspected * 100;

    // 목록
    public ObservableCollection<string>        CameraList  { get; } = [];
    public ObservableCollection<string>        PortList    { get; } = [];
    public ObservableCollection<LogEntry>      LogItems    { get; } = [];
    public ObservableCollection<string>        DobotPortList { get; } = [];
    public ObservableCollection<InspectionRecord> History { get; } = [];

    // ── 모터 방향 제어 표시 ──
    [ObservableProperty] private string _motorDirectionText = "—";
    [ObservableProperty] private bool   _isAutoMode;

    public MainViewModel()
    {
        _motor = new ArduinoMotorService(_motorSettings);
        _motorSettings.Rotations = 3;
        RefreshCameraList();
        RefreshPortList();
        RefreshDobotPorts();

        _camera.FrameReady += frame =>
        {
            if (System.Threading.Interlocked.Exchange(ref _cameraFrameUpdatePending, 1) == 1)
                return;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    CameraFrame = frame;
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _cameraFrameUpdatePending, 0);
                }
            });
        };

        AddLog("시스템 초기화 완료. ONNX 모델을 로드하거나 데모 모드로 실행합니다.", LogLevel.Info);
    }

    // ── Commands ───────────────────────────────────────────────

    [RelayCommand]
    private void RefreshCameraList()
    {
        CameraList.Clear();
        foreach (var c in CameraService.GetAvailableCameras())
            CameraList.Add(c);
        if (CameraList.Count > 0) SelectedCamera = CameraList[0];
    }

    [RelayCommand]
    private void RefreshPortList()
    {
        PortList.Clear();
        PortList.Add("(시뮬레이션)");
        foreach (var p in ArduinoMotorService.GetAvailablePorts())
            PortList.Add(p);
        SelectedPort = PortList[0];
    }

    [RelayCommand]
    private void RefreshDobotPorts()
    {
        DobotPortList.Clear();

        var ports = SerialPort.GetPortNames()
            .OrderBy(port => port)
            .ToList();

        foreach (var port in ports)
        {
            DobotPortList.Add(port);
        }

        if (ports.Contains("COM7"))
        {
            SelectedDobotPort = "COM7";
        }
        else if (ports.Count > 0)
        {
            SelectedDobotPort = ports[0];
        }
        else
        {
            SelectedDobotPort = string.Empty;
            DobotStatusText = "Dobot 상태: 사용 가능한 포트 없음";
        }
    }

    [RelayCommand]
    private void ToggleCamera()
    {
        if (IsCameraRunning)
        {
            _camera.Stop();
            IsCameraRunning = false;
            CameraFrame     = null;
            InspectionOverlayFrame = null;
            IsInspectionOverlayVisible = false;
            AddLog("카메라 중지", LogLevel.Info);
        }
        else
        {
            int idx = int.TryParse(SelectedCamera.Replace("Camera ", ""), out int i) ? i : 0;
            bool ok = _camera.Start(idx);
            IsCameraRunning = ok;
            AddLog(ok ? $"카메라 {idx} 시작" : "카메라 열기 실패", ok ? LogLevel.Info : LogLevel.Error);
        }
    }

    [RelayCommand]
    private void ConnectMotor()
    {
        if (IsMotorConnected)
        {
            _motor.Disconnect();
            IsMotorConnected = false;
            AddLog("모터 연결 해제", LogLevel.Info);
        }
        else
        {
            if (SelectedPort == "(시뮬레이션)")
            {
                IsMotorConnected = true;
                AddLog("시뮬레이션 모드로 연결됨", LogLevel.Warn);
                return;
            }
            bool ok = _motor.Connect(SelectedPort, BaudRate);
            IsMotorConnected = ok;
            AddLog(ok ? $"Arduino 연결 성공: {SelectedPort}" : $"연결 실패: {SelectedPort}", ok ? LogLevel.Info : LogLevel.Error);
        }
    }

    [RelayCommand]
    private void ConnectDobot()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedDobotPort))
            {
                DobotStatusText = "Dobot 상태: 포트를 선택하세요.";
                return;
            }

            var message = _dobot.Connect(SelectedDobotPort);
            IsDobotConnected = _dobot.IsConnected;
            DobotStatusText = $"Dobot 연결됨: {_dobot.ConnectedPortName}";
            AddLog(message, LogLevel.Info);
        }
        catch (Exception ex)
        {
            IsDobotConnected = _dobot.IsConnected;
            DobotStatusText = $"Dobot 연결 실패: {ex.Message}";
            AddLog(DobotStatusText, LogLevel.Error);
        }
    }

    [RelayCommand]
    private void LoadOnnxModel()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "ONNX 모델 선택",
            Filter = "ONNX 모델 (*.onnx)|*.onnx"
        };
        if (dlg.ShowDialog() != true) return;

        bool ok = _onnx.LoadModel(dlg.FileName);
        IsModelLoaded  = ok;
        OnnxModelPath  = ok ? System.IO.Path.GetFileName(dlg.FileName) : "(로드 실패)";
        AddLog(ok ? $"모델 로드: {OnnxModelPath}" : "모델 로드 실패", ok ? LogLevel.Info : LogLevel.Error);
    }

    [RelayCommand]
    private async Task RunInspectionAsync()
    {
        if (IsBusy) return;

        if (!ValidateInspectionReady())
            return;

        IsBusy = true;
        SetStatus("분석 시작", "#F9E2AF");

        try
        {
            await RunOneInspectionCycleAsync();
        }
        catch (OperationCanceledException ex)
        {
            AddLog(ex.Message, LogLevel.Warn);
            SetStatus("비상정지", "#F38BA8");
        }
        catch (Exception ex)
        {
            AddLog($"Error: {ex.Message}", LogLevel.Error);
            SetStatus("Error", "#F38BA8");
        }
        finally
        {
            IsBusy = false;
            MotorDirectionText = "";
        }
    }

    public async Task RunOneInspectionCycleAsync()
    {
        InspectionResult inspectionResult = InspectionResult.NoDetection;
        var progress = new Progress<string>(msg => AddLog(msg, LogLevel.Info));

        ThrowIfEmergencyStopped();
        MotorDirectionText = "정방향";
        AddLog($"카메라 위치 이동 시작: {InspectionMoveRotations}바퀴", LogLevel.Info);
        SetStatus($"카메라 위치 이동: {InspectionMoveRotations}바퀴", "#F9E2AF");
        await _motor.MoveForwardAsync(InspectionMoveRotations, progress);
        await _motor.StopAsync();
        ThrowIfEmergencyStopped();

        await WaitForConveyorStoppedBeforeCaptureAsync();

        ThrowIfEmergencyStopped();
        SetStatus("이미지 분석", "#89DCEB");
        AddLog("ONNX 분석 시작", LogLevel.Info);

        using InspectionOutput output = await InspectCurrentFrameAsync();
        LogMemorySnapshot("ONNX 후");
        InspectionRecord record = output.Result;
        await SaveInspectionOutputAsync(output);
        await ShowInspectionImagesAsync(output);
        LogMemorySnapshot("표시 후");

        AddInspectionHistory(record);
        Confidence = record.Confidence;
        inspectionResult = ConvertToInspectionResult(record.Label, record.Confidence);

        UpdateLastResult(record, inspectionResult);
        ThrowIfEmergencyStopped();

        if (inspectionResult == InspectionResult.NoDetection)
        {
            SetStatus("탐지 없음: Dobot 동작 안 함. 수동 확인 필요.", "#F9E2AF");
            AddLog("탐지 없음: Dobot 동작 안 함. 수동 확인 필요.", LogLevel.Warn);
            await _motor.StopAsync();
            return;
        }

        MotorDirectionText = "정방향";
        AddLog($"Dobot Pick 위치 이동 시작: {InspectionMoveRotations}바퀴", LogLevel.Info);
        SetStatus($"Dobot Pick 위치 이동: {InspectionMoveRotations}바퀴", "#F9E2AF");
        await _motor.MoveForwardAsync(InspectionMoveRotations, progress);
        await _motor.StopAsync();
        ThrowIfEmergencyStopped();

        SetStatus("Dobot Pick 위치 안정화", "#F9E2AF");
        await DelayWithEmergencyChecksAsync(300);
        ThrowIfEmergencyStopped();

        switch (inspectionResult)
        {
            case InspectionResult.Normal:
                SetStatus("정상 분류 중", "#A6E3A1");
                AddLog("Normal: Dobot RightPlace 분류 시작", LogLevel.Info);
                await Task.Run(() => _dobot.PickAndPlaceRight());
                ThrowIfEmergencyStopped();
                await DelayWithEmergencyChecksAsync(5000);
                TotalPass++;
                break;

            case InspectionResult.Defect:
                SetStatus("불량 분류 중", "#F38BA8");
                AddLog("Defect: Dobot LeftPlace 분류 시작", LogLevel.Error);
                await Task.Run(() => _dobot.PickAndPlaceLeft());
                ThrowIfEmergencyStopped();
                await DelayWithEmergencyChecksAsync(5000);
                TotalFail++;
                break;

            case InspectionResult.NoDetection:
                SetStatus("탐지 없음: Dobot 동작 안 함", "#F9E2AF");
                await _motor.StopAsync();
                return;
        }

        OnPropertyChanged(nameof(TotalInspected));
        OnPropertyChanged(nameof(PassRate));
        SetStatus("분류 완료", "#89B4FA");
        AddLog("분류 완료", LogLevel.Info);
    }

    [RelayCommand]
    private async Task StartAutoInspectionAsync()
    {
        if (IsAutoInspectionRunning)
            return;

        if (!ValidateInspectionReady())
            return;

        _autoInspectionCts?.Dispose();
        _autoInspectionCts = new CancellationTokenSource();
        var dobotReadyMessage = _dobot.ResetEmergencyStop();
        DobotStatusText = $"Dobot 준비: {dobotReadyMessage}";
        IsAutoInspectionRunning = true;
        IsBusy = true;
        IsEmergencyStopped = false;

        try
        {
            await RunAutoInspectionLoopAsync(_autoInspectionCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("자동 검사가 중지되었습니다.", "#F9E2AF");
            AddLog("자동 검사가 중지되었습니다.", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            SetStatus($"자동 검사 오류: {ex.Message}", "#F38BA8");
            AddLog($"자동 검사 오류: {ex.Message}", LogLevel.Error);
            await _motor.StopAsync();
        }
        finally
        {
            IsAutoInspectionRunning = false;
            IsBusy = false;
            MotorDirectionText = "";
            _autoInspectionCts?.Dispose();
            _autoInspectionCts = null;
        }
    }

    [RelayCommand]
    private async Task StopAutoInspectionAsync()
    {
        try
        {
            _autoInspectionCts?.Cancel();
            await _motor.StopAsync();
            _dobot.EmergencyStop();

            SetStatus("자동 검사 중지 요청됨. 컨베이어 정지.", "#F9E2AF");
            AddLog("자동 검사 중지 요청됨. 컨베이어 정지.", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            SetStatus($"자동 검사 중지 오류: {ex.Message}", "#F38BA8");
            AddLog($"자동 검사 중지 오류: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task RunAutoInspectionLoopAsync(CancellationToken token)
    {
        _inspectionQueue.Clear();

        // 첫 13바퀴 이동: 첫 wafer를 카메라 위치로 이동한다.
        await MoveConveyorToNextPipelinePositionAsync("초기 wafer 카메라 위치 이동 중", token);

        SetStatus("첫 wafer ONNX 검사 중", "#89DCEB");
        var firstResult = await InspectCurrentWaferAsync(token);

        if (firstResult == InspectionResult.NoDetection)
        {
            await StopForNoDetectionAsync();
            return;
        }

        _inspectionQueue.Enqueue(firstResult);

        while (!token.IsCancellationRequested)
        {
            // 이후 매 13바퀴 이동: 이전 wafer는 Pick 위치, 새 wafer는 카메라 위치에 도착한다.
            await MoveConveyorToNextPipelinePositionAsync("다음 위치로 13바퀴 이동 중", token);

            if (_inspectionQueue.Count == 0)
            {
                SetStatus("검사 Queue가 비어 있습니다. 자동 검사 중단.", "#F38BA8");
                AddLog("검사 Queue가 비어 있습니다. 자동 검사 중단.", LogLevel.Error);
                await _motor.StopAsync();
                return;
            }

            var pickResult = _inspectionQueue.Dequeue();

            // 정지 중에 Dobot 분류와 ONNX 검사를 동시에 수행한다.
            // ONNX 검사 결과는 Queue에 저장되어 다음 Pick 위치 도착 때 사용된다.
            SetStatus("Dobot 분류와 다음 wafer ONNX 검사를 동시에 실행 중", "#89DCEB");
            AddLog("Dobot 분류와 다음 wafer ONNX 검사를 동시에 실행 중", LogLevel.Info);

            var dobotTask = ClassifyPickedWaferAsync(pickResult, token);
            var inspectTask = InspectCurrentWaferAsync(token);

            await Task.WhenAll(dobotTask, inspectTask);

            var currentResult = inspectTask.Result;

            if (currentResult == InspectionResult.NoDetection)
            {
                await StopForMissingNextWaferAsync();
                return;
            }

            _inspectionQueue.Enqueue(currentResult);
        }
    }

    private async Task MoveConveyorToNextPipelinePositionAsync(string statusText, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();

        MotorDirectionText = "정방향";
        SetStatus(statusText, "#F9E2AF");
        AddLog($"{statusText}: {InspectionMoveRotations}바퀴", LogLevel.Info);

        var progress = new Progress<string>(msg => AddLog(msg, LogLevel.Info));
        await _motor.MoveForwardAsync(InspectionMoveRotations, progress);
        await _motor.StopAsync();

        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();

        await WaitForConveyorStoppedBeforeCaptureAsync(token);
    }

    private async Task<InspectionResult> InspectCurrentWaferAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();

        using InspectionOutput output = await InspectCurrentFrameAsync(token);
        token.ThrowIfCancellationRequested();
        LogMemorySnapshot("ONNX 후");

        InspectionRecord record = output.Result;
        await SaveInspectionOutputAsync(output, token);
        await ShowInspectionImagesAsync(output, token);
        LogMemorySnapshot("표시 후");

        AddInspectionHistory(record);
        Confidence = record.Confidence;

        var result = ConvertToInspectionResult(record.Label, record.Confidence);
        UpdateLastResult(record, result);
        SetStatus($"ONNX 검사 결과: {result}", result == InspectionResult.NoDetection ? "#F9E2AF" : "#89DCEB");

        return result;
    }

    private async Task ClassifyPickedWaferAsync(InspectionResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();

        switch (result)
        {
            case InspectionResult.Normal:
                SetStatus("Pick 위치 wafer 정상 분류 중", "#A6E3A1");
                AddLog("Normal: Dobot RightPlace 분류 시작", LogLevel.Info);
                await Task.Run(() => _dobot.PickAndPlaceRight(), token);
                await DelayWithCancellationChecksAsync(5000, token);
                TotalPass++;
                break;

            case InspectionResult.Defect:
                SetStatus("Pick 위치 wafer 불량 분류 중", "#F38BA8");
                AddLog("Defect: Dobot LeftPlace 분류 시작", LogLevel.Error);
                await Task.Run(() => _dobot.PickAndPlaceLeft(), token);
                await DelayWithCancellationChecksAsync(5000, token);
                TotalFail++;
                break;

            case InspectionResult.NoDetection:
                SetStatus("탐지 없음 결과가 Pick 위치에 도착했습니다. Dobot 동작 안 함.", "#F9E2AF");
                AddLog("NoDetection wafer reached pick station. Dobot 동작 안 함.", LogLevel.Error);
                await _motor.StopAsync();
                throw new OperationCanceledException("NoDetection wafer reached pick station.");
        }

        OnPropertyChanged(nameof(TotalInspected));
        OnPropertyChanged(nameof(PassRate));
    }

    private async Task StopForNoDetectionAsync()
    {
        SetStatus("탐지 없음: 자동 검사 중단, 수동 확인 필요", "#F9E2AF");
        AddLog("탐지 없음: 자동 검사 중단, 수동 확인 필요", LogLevel.Warn);
        _autoInspectionCts?.Cancel();
        await _motor.StopAsync();
    }

    private async Task StopForMissingNextWaferAsync()
    {
        SetStatus("다음 wafer 미검출: 자동 검사 중단, 공급 상태 확인 필요", "#F9E2AF");
        AddLog("다음 wafer 미검출: 이전 wafer 분류는 완료됐고, 카메라 위치의 다음 wafer가 없습니다.", LogLevel.Warn);
        AddLog("한 장만 테스트할 때는 자동 검사 시작 대신 검사 실행을 사용하세요.", LogLevel.Info);
        _autoInspectionCts?.Cancel();
        await _motor.StopAsync();
    }

    private async Task<InspectionOutput> InspectCurrentFrameAsync(CancellationToken token = default)
    {
        AddLog("검사용 현재 카메라 프레임 캡처", LogLevel.Info);

        token.ThrowIfCancellationRequested();
        OpenCvSharp.Mat? frame = _camera.GrabFrame();

        if (frame == null)
        {
            AddLog("카메라 프레임 캡처 실패", LogLevel.Warn);
        }

        if (frame != null)
        {
            try
            {
                LogMemorySnapshot("ONNX 전");
                var output = await _onnx.InspectWithResultFrameAsync(frame);
                LogWaferBrightnessStats();
                return output;
            }
            finally
            {
                frame.Dispose();
            }
        }

        using var emptyFrame = new OpenCvSharp.Mat();
        return await _onnx.InspectWithResultFrameAsync(emptyFrame);
    }

    private void LogWaferBrightnessStats()
    {
        if (_onnx.LastWaferMeanBrightness == null || _onnx.LastWaferMaxBrightness == null)
            return;

        var mean = _onnx.LastWaferMeanBrightness.Value;
        var max = _onnx.LastWaferMaxBrightness.Value;
        var level = mean > 210 || max > 245 ? LogLevel.Warn : LogLevel.Info;

        AddLog($"wafer 밝기: 평균 {mean:0.0}, 최대 {max:0.0}", level);

        if (level == LogLevel.Warn)
        {
            AddLog("wafer가 밝은 편입니다. Exposure를 더 낮추는 것을 권장합니다.", LogLevel.Warn);
        }
    }

    private async Task SaveInspectionOutputAsync(InspectionOutput output, CancellationToken token = default)
    {
        try
        {
            LogMemorySnapshot("저장 전");
            var saved = await _resultSaveService.SaveAsync(output, token);
            AddLog($"검사 결과 저장: {saved.ResultJsonPath}", LogLevel.Info);
            LogMemorySnapshot("저장 후");
            _ = UploadInspectionResultAsync(saved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"검사 결과 저장 실패: {ex.Message}", LogLevel.Warn);
        }
    }

    private async Task UploadInspectionResultAsync(InspectionSaveResult saved)
    {
        try
        {
            LogMemorySnapshot("업로드 전");
            await _uploadService.UploadAsync(saved);
            AddLog("backend 업로드 완료", LogLevel.Info);
            LogMemorySnapshot("업로드 후");
        }
        catch (Exception ex)
        {
            AddLog($"backend 업로드 실패: {ex.Message}", LogLevel.Warn);
        }
    }

    private async Task ShowInspectionImagesAsync(InspectionOutput output, CancellationToken token = default)
    {
        if (output.AnnotatedFrame == null || output.AnnotatedFrame.Empty())
            return;

        var bitmap = output.AnnotatedFrame.ToBitmapSource();
        bitmap.Freeze();

        InspectionOverlayFrame = bitmap;
        LastInspectionImagePath = output.Result.ResultImagePath;
        HasLastInspectionImage = !string.IsNullOrWhiteSpace(LastInspectionImagePath);
        IsInspectionOverlayVisible = true;

        var displayMilliseconds = token.CanBeCanceled ? 300 : 1500;

        if (token.CanBeCanceled)
        {
            await DelayWithCancellationChecksAsync(displayMilliseconds, token);
        }
        else
        {
            await DelayWithEmergencyChecksAsync(displayMilliseconds);
        }

        IsInspectionOverlayVisible = false;
        InspectionOverlayFrame = null;
    }

    private bool ValidateInspectionReady()
    {
        if (IsEmergencyStopped)
        {
            SetStatus("비상정지 상태: 장비 확인 후 비상정지 해제를 누르세요.", "#F38BA8");
            AddLog("비상정지 상태입니다. 장비 상태 확인 후 비상정지 해제를 누르세요.", LogLevel.Warn);
            return false;
        }

        if (!IsMotorConnected)
        {
            SetStatus("Arduino를 먼저 연결하세요.", "#F38BA8");
            AddLog("자동 검사 시작 실패: Arduino 미연결", LogLevel.Error);
            return false;
        }

        if (!IsDobotConnected || !_dobot.IsConnected)
        {
            SetStatus("Dobot을 먼저 연결하세요.", "#F38BA8");
            AddLog("자동 검사 시작 실패: Dobot 미연결", LogLevel.Error);
            return false;
        }

        if (!IsCameraRunning)
        {
            SetStatus("카메라를 확인하세요.", "#F38BA8");
            AddLog("자동 검사 시작 실패: 카메라 미실행", LogLevel.Error);
            return false;
        }

        if (!IsModelLoaded)
        {
            SetStatus("ONNX 모델을 먼저 로드하세요.", "#F38BA8");
            AddLog("자동 검사 시작 실패: ONNX 모델 미로드", LogLevel.Error);
            return false;
        }

        return true;
    }

    private InspectionResult ConvertToInspectionResult(string? label, double confidence)
    {
        if (string.IsNullOrWhiteSpace(label))
            return InspectionResult.NoDetection;

        if (confidence <= 0 || confidence < _onnx.ConfidenceThreshold)
            return InspectionResult.NoDetection;

        var normalizedLabel = label.Trim();

        if (normalizedLabel.Contains("탐지 없음", StringComparison.OrdinalIgnoreCase))
            return InspectionResult.NoDetection;

        var upperLabel = normalizedLabel.ToUpperInvariant();

        if (upperLabel is "NORMAL" or "PASS" or "WAFER" || normalizedLabel == "정상")
            return InspectionResult.Normal;

        if (upperLabel is "FAIL" or "DEFECT" or "SCRATCH" or "CRACK" or "CONTAMINATION" or "EDGE_CHIPPING" or "PATTERN_DEFECT"
            || normalizedLabel == "불량")
        {
            return InspectionResult.Defect;
        }

        return InspectionResult.NoDetection;
    }

    private void UpdateLastResult(InspectionRecord record, InspectionResult inspectionResult)
    {
        switch (inspectionResult)
        {
            case InspectionResult.Normal:
                LastResult = $"PASS  [{record.Label}  {record.Confidence:P1}]";
                LastResultColor = "#A6E3A1";
                AddLog($"정상 검출: {record.Label} ({record.Confidence:P1})", LogLevel.Info);
                break;

            case InspectionResult.Defect:
                LastResult = $"FAIL  [{record.Label}  {record.Confidence:P1}]";
                LastResultColor = "#F38BA8";
                AddLog($"불량 검출: {record.Label} ({record.Confidence:P1})", LogLevel.Error);
                break;

            case InspectionResult.NoDetection:
                LastResult = $"NO DETECTION  [{record.Label}  {record.Confidence:P1}]";
                LastResultColor = "#F9E2AF";
                AddLog($"탐지 없음: {record.Label} ({record.Confidence:P1})", LogLevel.Warn);
                break;
        }
    }

    private void ThrowIfEmergencyStopped()
    {
        if (IsEmergencyStopped)
            throw new OperationCanceledException("비상정지 상태입니다.");
    }

    private async Task DelayWithEmergencyChecksAsync(int milliseconds)
    {
        const int intervalMilliseconds = 100;
        var remainingMilliseconds = milliseconds;

        while (remainingMilliseconds > 0)
        {
            ThrowIfEmergencyStopped();
            var delay = Math.Min(intervalMilliseconds, remainingMilliseconds);
            await Task.Delay(delay);
            remainingMilliseconds -= delay;
        }

        ThrowIfEmergencyStopped();
    }

    private async Task DelayWithCancellationChecksAsync(int milliseconds, CancellationToken token)
    {
        const int intervalMilliseconds = 100;
        var remainingMilliseconds = milliseconds;

        while (remainingMilliseconds > 0)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfEmergencyStopped();

            var delay = Math.Min(intervalMilliseconds, remainingMilliseconds);
            await Task.Delay(delay, token);
            remainingMilliseconds -= delay;
        }

        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();
    }

    private async Task WaitForConveyorStoppedBeforeCaptureAsync(CancellationToken token = default)
    {
        SetStatus("컨베이어 정지 확인 중", "#F9E2AF");
        AddLog("컨베이어 정지 확인: 최소 안정화 대기", LogLevel.Info);

        if (token.CanBeCanceled)
        {
            await DelayWithCancellationChecksAsync(ConveyorMinimumSettleMilliseconds, token);
        }
        else
        {
            await DelayWithEmergencyChecksAsync(ConveyorMinimumSettleMilliseconds);
        }

        if (!IsCameraRunning)
            return;

        var isStable = await _camera.WaitUntilFrameStableAsync(
            ConveyorStillTimeout,
            ConveyorStillSampleInterval,
            ConveyorStillMotionThreshold,
            ConveyorRequiredStableSamples,
            token
        );

        token.ThrowIfCancellationRequested();
        ThrowIfEmergencyStopped();

        if (isStable)
        {
            AddLog("컨베이어 정지 확인 완료: 프레임 변화량 안정", LogLevel.Info);
            return;
        }

        AddLog("컨베이어 정지 확인 시간 초과: 추가 안정화 후 촬영합니다.", LogLevel.Warn);

        if (token.CanBeCanceled)
        {
            await DelayWithCancellationChecksAsync(500, token);
        }
        else
        {
            await DelayWithEmergencyChecksAsync(500);
        }
    }

    [RelayCommand]
    private void OpenImagePreview(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
            return;

        PreviewImage = LoadBitmapFromFile(imagePath);
        IsImagePreviewOpen = true;
    }

    [RelayCommand]
    private void CloseImagePreview()
    {
        IsImagePreviewOpen = false;
        PreviewImage = null;
    }

    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        IsEmergencyStopped = true;
        _autoInspectionCts?.Cancel();
        IsAutoInspectionRunning = false;
        await _motor.StopAsync();
        var dobotMessage = _dobot.EmergencyStop();
        DobotStatusText = $"비상정지: {dobotMessage}";
        IsBusy = false;
        SetStatus("비상정지", "#F38BA8");
        AddLog($"비상정지: 컨베이어 정지, Dobot 큐 정지 ({dobotMessage})", LogLevel.Error);
    }

    [RelayCommand]
    private async Task DobotEmergencyStopAsync()
    {
        try
        {
            IsEmergencyStopped = true;
            _autoInspectionCts?.Cancel();
            IsAutoInspectionRunning = false;
            await _motor.StopAsync();
            var message = _dobot.EmergencyStop();

            DobotStatusText = $"비상정지: {message}";
            IsBusy = false;
            SetStatus("비상정지: 컨베이어 정지, Dobot 큐 정지", "#F38BA8");
            AddLog($"비상정지: 컨베이어 정지, Dobot 큐 정지 ({message})", LogLevel.Error);
        }
        catch (Exception ex)
        {
            DobotStatusText = $"비상정지 오류: {ex.Message}";
            AddLog(DobotStatusText, LogLevel.Error);
        }
    }

    [RelayCommand]
    private async Task ResetEmergencyStopAsync()
    {
        if (IsAutoInspectionRunning)
        {
            SetStatus("자동 검사 중에는 비상정지를 해제할 수 없습니다.", "#F38BA8");
            return;
        }

        try
        {
            await _motor.StopAsync();
            _inspectionQueue.Clear();
            _autoInspectionCts?.Dispose();
            _autoInspectionCts = null;

            var dobotMessage = _dobot.ResetEmergencyStop();

            IsEmergencyStopped = false;
            IsBusy = false;
            MotorDirectionText = "—";
            DobotStatusText = $"비상정지 해제: {dobotMessage}";
            SetStatus("비상정지 해제 완료. 자동 검사를 다시 시작할 수 있습니다.", "#89B4FA");
            AddLog($"비상정지 해제 완료: {dobotMessage}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"비상정지 해제 오류: {ex.Message}", "#F38BA8");
            AddLog($"비상정지 해제 오류: {ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    private async Task MotorForwardAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        MotorDirectionText = "▶ 정방향";
        var p = new Progress<string>(msg => AddLog(msg, LogLevel.Info));
        try { await _motor.MoveForwardAsync(Rotations, p); }
        finally { IsBusy = false; MotorDirectionText = "—"; }
    }

    [RelayCommand]
    private async Task MotorReverseAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        MotorDirectionText = "◀ 역방향";
        var p = new Progress<string>(msg => AddLog(msg, LogLevel.Info));
        try { await _motor.MoveReverseAsync(Rotations, p); }
        finally { IsBusy = false; MotorDirectionText = "—"; }
    }

    [RelayCommand]
    private void ClearLog() => LogItems.Clear();

    [RelayCommand]
    private void ClearStats()
    {
        TotalPass = 0;
        TotalFail = 0;
        History.Clear();
        LastInspectionImagePath = string.Empty;
        InspectionOverlayFrame = null;
        PreviewImage = null;
        HasLastInspectionImage = false;
        IsInspectionOverlayVisible = false;
        IsImagePreviewOpen = false;
        OnPropertyChanged(nameof(TotalInspected));
        OnPropertyChanged(nameof(PassRate));
    }

    // ── Helpers ────────────────────────────────────────────────

    private void SetStatus(string text, string color)
    {
        StatusText  = text;
        StatusColor = color;
    }

    private void AddInspectionHistory(InspectionRecord record)
    {
        History.Insert(0, record);

        while (History.Count > MaxInspectionHistory)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    private void LogMemorySnapshot(string label)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var managedMb = GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0;
            var privateMb = process.PrivateMemorySize64 / 1024.0 / 1024.0;
            var workingMb = process.WorkingSet64 / 1024.0 / 1024.0;

            AddLog(
                $"MEM {label}: managed={managedMb:0.0}MB private={privateMb:0.0}MB working={workingMb:0.0}MB history={History.Count}",
                LogLevel.Info
            );
        }
        catch
        {
        }
    }

    private static BitmapSource LoadBitmapFromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void AddLog(string message, LogLevel level)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LogItems.Add(new LogEntry
            {
                Time    = DateTime.Now.ToString("HH:mm:ss.fff"),
                Message = message,
                Level   = level
            });
            // 최대 300줄 유지
            while (LogItems.Count > 300)
                LogItems.RemoveAt(0);
        });
    }

    public void Dispose()
    {
        _camera.Dispose();
        _motor.Dispose();
        _dobot.Dispose();
        _onnx.Dispose();
        _uploadService.Dispose();
    }
}

public enum LogLevel { Info, Warn, Error }

public class LogEntry
{
    public string   Time    { get; set; } = "";
    public string   Message { get; set; } = "";
    public LogLevel Level   { get; set; }

    public string Color => Level switch
    {
        LogLevel.Warn  => "#F9E2AF",
        LogLevel.Error => "#F38BA8",
        _              => "#A6ADC8"
    };

    public override string ToString() => $"[{Time}] {Message}";
}
