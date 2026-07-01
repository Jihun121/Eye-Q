using System.IO.Ports;
using ConveyorInspector.Models;

namespace ConveyorInspector.Services;

/// <summary>
/// Arduino와 시리얼 통신을 통해 스텝모터를 제어합니다.
/// Arduino 펌웨어와 JSON 명령 프로토콜을 사용합니다.
/// 
/// Arduino 펌웨어 명령 포맷:
///   {"cmd":"move","dir":0,"steps":4800}   → 정방향 3바퀴
///   {"cmd":"move","dir":1,"steps":4800}   → 역방향 3바퀴
///   {"cmd":"stop"}                        → 즉시 정지
/// Arduino 응답 포맷:
///   {"status":"done"}  or  {"status":"error","msg":"..."}
/// </summary>
public class ArduinoMotorService : IDisposable
{
    private SerialPort? _port;
    private readonly MotorSettings _settings;

    public bool IsConnected => _port?.IsOpen ?? false;

    public ArduinoMotorService(MotorSettings settings)
    {
        _settings = settings;
    }

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public bool Connect(string portName, int baudRate = 115200)
    {
        try
        {
            Disconnect();

            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 60000,
                WriteTimeout = 5000,
                NewLine = "\n"
            };

            _port.Open();

            Thread.Sleep(2500);

            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            return true;
        }
        catch
        {
            _port = null;
            return false;
        }
    }

    public void Disconnect()
    {
        if (_port?.IsOpen == true)
            _port.Close();
        _port?.Dispose();
        _port = null;
    }


    /// <summary>
    /// 정방향(앞으로) 회전
    /// </summary>
    public async Task MoveForwardAsync(int rotations = 3, IProgress<string>? progress = null)
        => await MoveAsync(direction: 0, rotations, progress);

    /// <summary>
    /// 역방향(뒤로) 회전
    /// </summary>
    public async Task MoveReverseAsync(int rotations = 3, IProgress<string>? progress = null)
        => await MoveAsync(direction: 1, rotations, progress);

    private async Task MoveAsync(int direction, int rotations, IProgress<string>? progress)
    {
        int totalSteps = _settings.StepsPerRev * rotations;
        string dirStr = direction == 0 ? "정방향" : "역방향";

        if (_port == null || !_port.IsOpen)
        {
            progress?.Report($"[시뮬] 모터 {dirStr} {rotations}바퀴 ({totalSteps} 스텝) 시작");

            int msPerRev = (_settings.StepDelayUs * 2 * _settings.StepsPerRev) / 1000;

            for (int r = 1; r <= rotations; r++)
            {
                await Task.Delay(msPerRev);
                progress?.Report($"[시뮬] {r}/{rotations}바퀴 완료");
            }

            progress?.Report($"[시뮬] 모터 {dirStr} 완료");
            return;
        }

        try
        {
            progress?.Report($"모터 {dirStr} {rotations}바퀴 시작...");

            int delayUs = 500; // 일단 강제 테스트

            int estimatedMoveMs = (int)(delayUs * 2.0 * totalSteps / 1000.0);
            _port.ReadTimeout = Math.Max(30000, estimatedMoveMs + 10000);

            _port.DiscardInBuffer();

            string cmd =
                $"{{\"cmd\":\"move\",\"dir\":{direction},\"steps\":{totalSteps},\"delayUs\":{delayUs}}}";

            progress?.Report($"Arduino 명령 전송: {cmd}");
            progress?.Report($"응답 대기 중... 타임아웃 {_port.ReadTimeout / 1000}초");

            _port.WriteLine(cmd);

            string response = await Task.Run(() => _port.ReadLine());

            progress?.Report($"모터 완료: {response}");
        }
        catch (TimeoutException)
        {
            progress?.Report("모터 오류: Arduino 응답 타임아웃. Arduino가 done/stopped/error 응답을 보내는지 확인 필요");
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report($"모터 오류: {ex.Message}");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_port?.IsOpen == true)
        {
            var previousReadTimeout = _port.ReadTimeout;
            _port.ReadTimeout = 1000;

            _port.WriteLine("{\"cmd\":\"stop\"}");

            try
            {
                await Task.Run(() => _port.ReadLine());
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                _port.ReadTimeout = previousReadTimeout;
            }
        }
    }

    public void Dispose() => Disconnect();
}
