namespace ConveyorInspector.Models;

public sealed record DobotPoint(double X, double Y, double Z, double R)
{
    public override string ToString()
    {
        return $"X={X}, Y={Y}, Z={Z}, R={R}";
    }
}
