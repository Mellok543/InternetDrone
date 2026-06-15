namespace OperatorStation;

internal sealed class JoystickSnapshot
{
    public required int[] Axes { get; init; }
    public required bool[] Buttons { get; init; }
    public int Pov { get; init; } = -1;
}
