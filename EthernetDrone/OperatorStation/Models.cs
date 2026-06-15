using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OperatorStation;

public sealed class AxisState : INotifyPropertyChanged
{
    public string AxisName { get; set; } = "";
    private int _raw = 32767, _min = 0, _center = 32767, _max = 65535, _pwm = 1500;
    private bool _invert;
    public int Raw { get => _raw; set { _raw = value; OnPropertyChanged(); } }
    public int Min { get => _min; set { _min = value; OnPropertyChanged(); } }
    public int Center { get => _center; set { _center = value; OnPropertyChanged(); } }
    public int Max { get => _max; set { _max = value; OnPropertyChanged(); } }
    public bool Invert { get => _invert; set { _invert = value; OnPropertyChanged(); } }
    public int Pwm { get => _pwm; set { _pwm = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ButtonState : INotifyPropertyChanged
{
    public int Button { get; set; }
    private bool _pressed;
    public bool Pressed { get => _pressed; set { _pressed = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ChannelState : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    private int _pwm;
    public int Pwm { get => _pwm; set { _pwm = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class OperatorSettings
{
    public string VpsHost { get; set; } = "195.133.24.163";
    public int VpsPort { get; set; } = 50555;
    public string Room { get; set; } = "mell-drone";
    public string RollAxis { get; set; } = "X";
    public string PitchAxis { get; set; } = "Y";
    public string ThrottleAxis { get; set; } = "Z";
    public string YawAxis { get; set; } = "R";
    public int ArmButton { get; set; } = -1;
    public int LoiterButton { get; set; } = -1;
    public int RtlButton { get; set; } = -1;
    public int LandButton { get; set; } = -1;
    public int ManualButton { get; set; } = -1;

    public string ArmSwitch { get; set; } = "Не назначено";
    public string LoiterSwitch { get; set; } = "Не назначено";
    public string RtlSwitch { get; set; } = "Не назначено";
    public string LandSwitch { get; set; } = "Не назначено";
    public string ManualSwitch { get; set; } = "Не назначено";
    public List<AxisCalibration> Axes { get; set; } = new();
}

public sealed class AxisCalibration
{
    public string AxisName { get; set; } = "";
    public int Min { get; set; }
    public int Center { get; set; }
    public int Max { get; set; }
    public bool Invert { get; set; }
}

public sealed class PovState : INotifyPropertyChanged
{
    private string _value = "CENTER";
    public string Name { get; set; } = "POV";

    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
