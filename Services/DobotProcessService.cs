using System.Diagnostics;
using System.IO;

namespace ConveyorInspector.Services;

public sealed class DobotProcessService : IDisposable
{
    private Process? _process;
    private readonly object _sync = new();

    public bool IsConnected { get; private set; }

    public string? ConnectedPortName { get; private set; }

    public string Connect(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Dobot port name is required.", nameof(portName));
        }

        if (IsConnected)
        {
            return $"Already connected: {ConnectedPortName}";
        }

        var message = SendCommand($"CONNECT {portName}");
        IsConnected = true;
        ConnectedPortName = portName;
        return message;
    }

    public string PickAndPlaceRight()
    {
        EnsureConnected();
        return SendCommand("RIGHT");
    }

    public string PickAndPlaceLeft()
    {
        EnsureConnected();
        return SendCommand("LEFT");
    }

    public string EmergencyStop()
    {
        if (_process == null || _process.HasExited)
        {
            return "Dobot is not connected.";
        }

        return SendCommand("ESTOP");
    }

    public string ResetEmergencyStop()
    {
        if (_process == null || _process.HasExited)
        {
            return "Dobot is not connected.";
        }

        return SendCommand("RESET");
    }

    private string SendCommand(string command)
    {
        lock (_sync)
        {
            EnsureProcessStarted();

            if (_process?.StandardInput == null || _process.StandardOutput == null)
            {
                throw new InvalidOperationException("Dobot bridge is not ready.");
            }

            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();

            var response = _process.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new InvalidOperationException("Dobot bridge did not respond.");
            }

            if (response.StartsWith("OK ", StringComparison.Ordinal))
            {
                return response[3..];
            }

            if (response.StartsWith("ERR ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(response[4..]);
            }

            throw new InvalidOperationException($"Unknown Dobot bridge response: {response}");
        }
    }

    private void EnsureProcessStarted()
    {
        if (_process != null && !_process.HasExited)
        {
            return;
        }

        var bridgePath = Path.Combine(AppContext.BaseDirectory, "DobotBridge.exe");
        if (!File.Exists(bridgePath))
        {
            throw new FileNotFoundException("DobotBridge.exe was not found in the application folder.", bridgePath);
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = bridgePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            },
            EnableRaisingEvents = true
        };

        _process.Exited += (_, _) =>
        {
            IsConnected = false;
            ConnectedPortName = null;
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start Dobot bridge.");
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connect Dobot first.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.StandardInput.WriteLine("EXIT");
                    _process.StandardInput.Flush();
                    _process.WaitForExit(1000);
                }
            }
            catch
            {
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }
    }
}
