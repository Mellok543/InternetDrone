using System.Runtime.InteropServices;

namespace OperatorStation;

internal static class JoystickNative
{
    public const int MAXPNAMELEN = 32;
    public const int JOY_RETURNX = 0x00000001;
    public const int JOY_RETURNY = 0x00000002;
    public const int JOY_RETURNZ = 0x00000004;
    public const int JOY_RETURNR = 0x00000008;
    public const int JOY_RETURNU = 0x00000010;
    public const int JOY_RETURNV = 0x00000020;
    public const int JOY_RETURNPOV = 0x00000040;
    public const int JOY_RETURNBUTTONS = 0x00000080;
    public const int JOY_RETURNALL = JOY_RETURNX | JOY_RETURNY | JOY_RETURNZ | JOY_RETURNR | JOY_RETURNU | JOY_RETURNV | JOY_RETURNPOV | JOY_RETURNBUTTONS;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct JOYCAPS
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
        public string szPname;
        public uint wXmin; public uint wXmax; public uint wYmin; public uint wYmax; public uint wZmin; public uint wZmax;
        public uint wNumButtons; public uint wPeriodMin; public uint wPeriodMax;
        public uint wRmin; public uint wRmax; public uint wUmin; public uint wUmax; public uint wVmin; public uint wVmax;
        public uint wCaps; public uint wMaxAxes; public uint wNumAxes; public uint wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX
    {
        public uint dwSize; public uint dwFlags;
        public uint dwXpos; public uint dwYpos; public uint dwZpos; public uint dwRpos; public uint dwUpos; public uint dwVpos;
        public uint dwButtons; public uint dwButtonNumber; public uint dwPOV;
        public uint dwReserved1; public uint dwReserved2;
    }

    [DllImport("winmm.dll")] public static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Auto)] public static extern uint joyGetDevCaps(uint uJoyID, out JOYCAPS pjc, uint cbjc);
    [DllImport("winmm.dll")] public static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);
}

public sealed class JoystickDevice
{
    public JoystickDeviceSource Source { get; set; }
    public uint Id { get; set; }
    public Guid DirectInputGuid { get; set; }
    public string Name { get; set; } = "";
    public int AxisCount { get; set; }
    public int ButtonCount { get; set; }
    public override string ToString() => Name;
}

public enum JoystickDeviceSource
{
    WinMm,
    DirectInput
}
