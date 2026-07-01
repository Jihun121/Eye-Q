using ConveyorInspector.Services;

var dobot = new DobotService();

while (Console.ReadLine() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        WriteOk("Ignored empty command.");
        continue;
    }

    try
    {
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToUpperInvariant();
        var argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "CONNECT":
                WriteOk(dobot.Connect(argument));
                break;

            case "RIGHT":
                WriteOk(dobot.PickAndPlaceRight());
                break;

            case "LEFT":
                WriteOk(dobot.PickAndPlaceLeft());
                break;

            case "ESTOP":
                WriteOk(dobot.EmergencyStop());
                break;

            case "RESET":
                WriteOk(dobot.ResetEmergencyStop());
                break;

            case "EXIT":
                WriteOk("Dobot bridge exiting.");
                return;

            default:
                WriteError($"Unknown command: {command}");
                break;
        }
    }
    catch (Exception ex)
    {
        WriteError(ex.Message);
    }
}

static void WriteOk(string message)
{
    Console.WriteLine($"OK {message}");
    Console.Out.Flush();
}

static void WriteError(string message)
{
    Console.WriteLine($"ERR {message}");
    Console.Out.Flush();
}
