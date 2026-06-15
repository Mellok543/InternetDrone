namespace OperatorStation;

internal static class ControlMath
{
    public static int AxisToPwm(AxisState axis)
    {
        if (axis.Max <= axis.Min + 20) return 1500;

        double pwm = axis.Raw >= axis.Center
            ? 1500 + ((axis.Raw - axis.Center) / (double)Math.Max(1, axis.Max - axis.Center)) * 500.0
            : 1500 - ((axis.Center - axis.Raw) / (double)Math.Max(1, axis.Center - axis.Min)) * 500.0;

        if (axis.Invert) pwm = 3000 - pwm;
        return Math.Clamp((int)Math.Round(pwm), 1000, 2000);
    }
}
