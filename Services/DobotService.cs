using System.Text;
using DobotClientDemo.CPlusDll;
using ConveyorInspector.Models;

namespace ConveyorInspector.Services;

public sealed class DobotService
{
    // Dobot Magician은 USB 시리얼 포트(COM 포트)로 연결합니다.
    // UI에서 포트를 선택하지 않고 Connect()를 호출할 때만 COM7을 기본값으로 사용합니다.
    private const string DefaultPortName = "COM7";
    private const int Baudrate = 115200;

    // 흡착기를 켠 직후 바로 움직이면 제품이 떨어질 수 있으므로 잠깐 기다립니다.
    private const uint SuctionSettleMilliseconds = 300;

    // Pick/Place 지점으로 바로 이동하지 않고, 먼저 위쪽 안전 높이로 이동하기 위한 Z축 여유 높이입니다.
    private const double TravelLiftMillimeters = 50;

    // PTP(Point To Point) 이동 속도/가속도 설정입니다.
    // 값이 클수록 빠르지만 장비와 제품 상태에 따라 흔들림이 커질 수 있습니다.
    private const float PtpCoordinateVelocity = 200;
    private const float PtpCoordinateAcceleration = 200;
    private const float PtpCommonVelocityRatio = 80;
    private const float PtpCommonAccelerationRatio = 80;

    // 연결 여부와 현재 흡착기 상태를 UI/다른 서비스에서 확인할 수 있게 보관합니다.
    public bool IsConnected { get; private set; }

    public bool IsSuctionOn { get; private set; }

    public string? ConnectedPortName { get; private set; }

    // 기본 포트(COM7)로 연결하고 싶을 때 사용하는 편의 메서드입니다.
    public string Connect() => Connect(DefaultPortName);

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

        var firmwareType = new StringBuilder(60);
        var version = new StringBuilder(60);

        // DobotDll.ConnectDobot은 네이티브 DLL 함수입니다.
        // 성공하면 NoError, 실패하면 NotFound/Occupied 같은 코드를 반환합니다.
        var result = DobotDll.ConnectDobot(portName, Baudrate, firmwareType, version);

        if (result != (int)DobotConnect.DobotConnect_NoError)
        {
            throw new InvalidOperationException(GetConnectErrorMessage(result, portName));
        }

        // 이전에 남아 있던 큐 명령을 지우고, 새 명령을 받을 준비를 합니다.
        DobotDll.SetCmdTimeout(3000);
        DobotDll.SetQueuedCmdClear();
        DobotDll.SetQueuedCmdStartExec();

        // 연결 직후 속도/가속도 같은 기본 이동 파라미터를 한 번 설정합니다.
        SetMotionParameters();

        IsConnected = true;
        ConnectedPortName = portName;
        return $"Dobot connected: {portName}, FW={firmwareType}, Version={version}";
    }

    public string MoveHome()
    {
        EnsureConnected();

        // Home 명령도 즉시 동작이라기보다 Dobot 큐에 들어가는 명령입니다.
        var queuedCommandIndex = QueueHome();
        return $"Home move queued: QueueIndex={queuedCommandIndex}";
    }

    public DobotPoint GetCurrentPose()
    {
        EnsureConnected();

        // 현재 Dobot 끝단의 X/Y/Z/R 좌표를 읽습니다.
        var pose = new Pose
        {
            jointAngle = new float[4]
        };
        var result = DobotDll.GetPose(ref pose);

        EnsureCommandSucceeded(result, "Get current pose");
        return new DobotPoint(pose.x, pose.y, pose.z, pose.rHead);
    }

    public string SetSuction(bool on)
    {
        EnsureConnected();

        // true면 흡착 ON, false면 흡착 OFF 명령을 큐에 넣습니다.
        var queuedCommandIndex = QueueSuction(on, on ? "Suction ON" : "Suction OFF");
        return on
            ? $"Suction ON queued: QueueIndex={queuedCommandIndex}"
            : $"Suction OFF queued: QueueIndex={queuedCommandIndex}";
    }

    public string PickAndPlaceRight()
    {
        EnsureConnected();

        // 정상 제품은 오른쪽 위치(RightPlace)로 분류합니다.
        var result = QueuePickAndPlace(DobotPositions.RightPlace);
        return BuildPickAndPlaceMessage("Right place", DobotPositions.RightPlace, result);
    }

    public string PickAndPlaceLeft()
    {
        EnsureConnected();

        // 불량 제품은 왼쪽 위치(LeftPlace)로 분류합니다.
        var result = QueuePickAndPlace(DobotPositions.LeftPlace);
        return BuildPickAndPlaceMessage("Left place", DobotPositions.LeftPlace, result);
    }

    public string EmergencyStop()
    {
        if (!IsConnected)
        {
            return "Dobot is not connected.";
        }

        // 비상정지는 현재 큐 실행을 멈추고, 남은 큐 명령을 모두 지웁니다.
        // 제품을 들고 있을 수 있으므로 흡착 OFF는 자동으로 하지 않습니다.
        DobotDll.SetQueuedCmdStopExec();
        DobotDll.SetQueuedCmdClear();

        return "Dobot emergency stop executed.";
    }

    public string ResetEmergencyStop()
    {
        if (!IsConnected)
        {
            return "Dobot is not connected.";
        }

        // 비상정지 때 SetQueuedCmdStopExec()를 호출했기 때문에,
        // 다시 명령을 실행하려면 큐를 비우고 StartExec를 다시 호출해야 합니다.
        // 흡착기는 안전을 위해 여기서 자동으로 끄지 않습니다.
        DobotDll.SetQueuedCmdClear();
        DobotDll.SetQueuedCmdStartExec();

        return "Dobot queue execution restarted.";
    }

    private PickAndPlaceQueueResult QueuePickAndPlace(DobotPoint place)
    {
        // Pick 위치와 Place 위치 중 더 높은 Z를 기준으로 50mm 위를 안전 이동 높이로 사용합니다.
        // 이렇게 하면 낮은 위치끼리 직선 이동하다가 제품/지그와 부딪힐 가능성을 줄일 수 있습니다.
        var travelZ = Math.Max(DobotPositions.Pick.Z, place.Z) + TravelLiftMillimeters;
        var pickTravelPoint = WithZ(DobotPositions.Pick, travelZ);
        var placeTravelPoint = WithZ(place, travelZ);

        // 아래 명령들은 Dobot 내부 큐에 순서대로 쌓입니다.
        // 큐에 쌓인 순서대로 실행되므로 Pick -> 흡착 -> 이동 -> Place 흐름이 유지됩니다.
        var pickApproachQueueIndex = QueueMove(pickTravelPoint, "Move above pick");
        var pickQueueIndex = QueueMove(DobotPositions.Pick, "Pick move");
        var suctionOnQueueIndex = QueueSuction(true, "Suction ON");
        var waitQueueIndex = QueueWait(SuctionSettleMilliseconds);
        var liftQueueIndex = QueueMove(pickTravelPoint, "Lift after pick");
        var travelQueueIndex = QueueMove(placeTravelPoint, "Travel above place");
        var placeQueueIndex = QueueMove(place, "Place move");
        var suctionOffQueueIndex = QueueSuction(false, "Suction OFF");
        var retreatQueueIndex = QueueMove(placeTravelPoint, "Lift after place");
        var homeQueueIndex = QueueHome();

        return new PickAndPlaceQueueResult(
            pickApproachQueueIndex,
            pickQueueIndex,
            suctionOnQueueIndex,
            waitQueueIndex,
            liftQueueIndex,
            travelQueueIndex,
            placeQueueIndex,
            suctionOffQueueIndex,
            retreatQueueIndex,
            homeQueueIndex
        );
    }

    private static DobotPoint WithZ(DobotPoint point, double z)
    {
        // X/Y/R은 그대로 두고 Z 높이만 바꾼 새 좌표를 만듭니다.
        return point with { Z = z };
    }

    private ulong QueueMove(DobotPoint point, string commandName)
    {
        // PTPCmd는 Dobot에게 "이 좌표로 이동해라"라고 전달하는 구조체입니다.
        // PTPMOVLXYZMode는 XYZ 좌표 기준으로 직선 이동하는 모드입니다.
        var command = new PTPCmd
        {
            ptpMode = (byte)PTPMode.PTPMOVLXYZMode,
            x = (float)point.X,
            y = (float)point.Y,
            z = (float)point.Z,
            rHead = (float)point.R
        };

        ulong queuedCommandIndex = 0;

        // 세 번째 인자 true는 "큐에 넣어서 실행"하겠다는 뜻입니다.
        // queuedCommandIndex에는 Dobot이 부여한 큐 번호가 들어옵니다.
        var result = DobotDll.SetPTPCmd(ref command, true, ref queuedCommandIndex);

        EnsureCommandSucceeded(result, commandName);
        return queuedCommandIndex;
    }

    private ulong QueueSuction(bool on, string commandName)
    {
        ulong queuedCommandIndex = 0;

        // enableCtrl=true: 흡착기 제어를 활성화합니다.
        // on=true/false: 흡착을 켜거나 끕니다.
        var result = DobotDll.SetEndEffectorSuctionCup(true, on, true, ref queuedCommandIndex);

        EnsureCommandSucceeded(result, commandName);
        IsSuctionOn = on;
        return queuedCommandIndex;
    }

    private static ulong QueueHome()
    {
        // Dobot에 설정된 Home 위치로 이동하는 명령입니다.
        var homeCommand = new HOMECmd { temp = 0 };
        ulong queuedCommandIndex = 0;
        var result = DobotDll.SetHOMECmd(ref homeCommand, true, ref queuedCommandIndex);

        EnsureCommandSucceeded(result, "Home move");
        return queuedCommandIndex;
    }

    private static ulong QueueWait(uint milliseconds)
    {
        // Dobot 큐 안에 대기 시간을 넣습니다.
        // 여기서는 흡착 안정화 같은 짧은 대기 용도로 사용합니다.
        var command = new WAITCmd
        {
            timeout = milliseconds
        };

        ulong queuedCommandIndex = 0;
        var result = DobotDll.SetWAITCmd(ref command, true, ref queuedCommandIndex);

        EnsureCommandSucceeded(result, "Wait");
        return queuedCommandIndex;
    }

    private static void SetMotionParameters()
    {
        ulong queuedCommandIndex = 0;

        // 관절별 속도/가속도입니다. 4개 값은 Dobot의 4개 축에 대응합니다.
        var jointParams = new PTPJointParams
        {
            velocity = [200, 200, 200, 200],
            acceleration = [200, 200, 200, 200]
        };
        EnsureCommandSucceeded(
            DobotDll.SetPTPJointParams(ref jointParams, false, ref queuedCommandIndex),
            "PTP joint parameter setup"
        );

        // XYZ 좌표 이동 시 사용할 속도/가속도입니다.
        var coordinateParams = new PTPCoordinateParams
        {
            xyzVelocity = PtpCoordinateVelocity,
            rVelocity = PtpCoordinateVelocity,
            xyzAcceleration = PtpCoordinateAcceleration,
            rAcceleration = PtpCoordinateAcceleration
        };
        EnsureCommandSucceeded(
            DobotDll.SetPTPCoordinateParams(ref coordinateParams, false, ref queuedCommandIndex),
            "PTP coordinate parameter setup"
        );

        // JUMP 이동 모드에서 쓰는 파라미터입니다.
        // 현재 이동은 MOVL 모드지만, 기본값을 같이 설정해 둡니다.
        var jumpParams = new PTPJumpParams
        {
            jumpHeight = 20,
            zLimit = 100
        };
        EnsureCommandSucceeded(
            DobotDll.SetPTPJumpParams(ref jumpParams, false, ref queuedCommandIndex),
            "PTP jump parameter setup"
        );

        // 전체 이동 속도 비율입니다. 100에 가까울수록 설정 속도에 가깝게 움직입니다.
        var commonParams = new PTPCommonParams
        {
            velocityRatio = PtpCommonVelocityRatio,
            accelerationRatio = PtpCommonAccelerationRatio
        };
        EnsureCommandSucceeded(
            DobotDll.SetPTPCommonParams(ref commonParams, false, ref queuedCommandIndex),
            "PTP common parameter setup"
        );
    }

    private static string BuildPickAndPlaceMessage(
        string name,
        DobotPoint place,
        PickAndPlaceQueueResult result
    )
    {
        // 각 단계의 큐 번호를 로그로 남기기 위한 메시지입니다.
        // 문제가 생겼을 때 어느 명령까지 들어갔는지 확인하는 데 도움이 됩니다.
        return $"Pick({DobotPositions.Pick}) -> {name}({place}) queued. "
            + $"PickApproach={result.PickApproachQueueIndex}, "
            + $"Pick={result.PickQueueIndex}, "
            + $"SuctionOn={result.SuctionOnQueueIndex}, "
            + $"Wait={result.WaitQueueIndex}, "
            + $"Lift={result.LiftQueueIndex}, "
            + $"Travel={result.TravelQueueIndex}, "
            + $"Place={result.PlaceQueueIndex}, "
            + $"SuctionOff={result.SuctionOffQueueIndex}, "
            + $"Retreat={result.RetreatQueueIndex}, "
            + $"Home={result.HomeQueueIndex}";
    }

    private void EnsureConnected()
    {
        // Dobot 연결 전에 이동 명령을 보내면 네이티브 DLL 호출이 실패할 수 있으므로 먼저 막습니다.
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connect Dobot first.");
        }
    }

    private static void EnsureCommandSucceeded(int result, string commandName)
    {
        // Dobot DLL은 예외를 던지지 않고 숫자 결과 코드를 반환합니다.
        // 여기서 실패 코드를 사람이 읽을 수 있는 예외 메시지로 바꿉니다.
        if (result == (int)DobotCommunicate.DobotCommunicate_NoError)
        {
            return;
        }

        var message = result switch
        {
            (int)DobotCommunicate.DobotCommunicate_BufferFull => "Command buffer is full.",
            (int)DobotCommunicate.DobotCommunicate_Timeout => "Command timed out.",
            (int)DobotCommunicate.DobotCommunicate_InvalidParams => "Command parameters are invalid.",
            _ => $"Unknown result code: {result}"
        };

        throw new InvalidOperationException($"{commandName} failed: {message}");
    }

    private static string GetConnectErrorMessage(int result, string portName)
    {
        // 연결 실패 원인을 사용자가 바로 알 수 있게 메시지로 변환합니다.
        return result switch
        {
            (int)DobotConnect.DobotConnect_NotFound => $"{portName} did not find a Dobot. Check USB, driver, and COM port.",
            (int)DobotConnect.DobotConnect_Occupied => $"{portName} is already in use. Close Dobot Studio or another serial program.",
            _ => $"Dobot connect failed: Result={result}"
        };
    }

    private sealed record PickAndPlaceQueueResult(
        // Pick & Place 한 번에 들어간 각 큐 명령 번호를 묶어서 보관합니다.
        ulong PickApproachQueueIndex,
        ulong PickQueueIndex,
        ulong SuctionOnQueueIndex,
        ulong WaitQueueIndex,
        ulong LiftQueueIndex,
        ulong TravelQueueIndex,
        ulong PlaceQueueIndex,
        ulong SuctionOffQueueIndex,
        ulong RetreatQueueIndex,
        ulong HomeQueueIndex
    );
}
