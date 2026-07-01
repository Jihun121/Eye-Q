namespace ConveyorInspector.Models;

public class MotorSettings
{
    public int StepsPerRev { get; set; } = 1600;   // 8 마이크로스텝
    public int StepDelayUs { get; set; } = 2500;   // 펄스 간격 (μs)
    public int Rotations { get; set; } = 3;        // 기본 회전수
    public int PostRotationDelayMs { get; set; } = 1000;
}
