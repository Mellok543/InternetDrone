using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace OperatorStation;

public class TelemetryState : INotifyPropertyChanged
{
    private bool _isArmed;
    private string _mode = "UNKNOWN";
    private double _batteryVoltage;
    private double _batteryCurrent;
    private int _gpsFix;
    private int _satellites;
    private double _altitude;
    private double _groundSpeed;
    private double _roll;
    private double _pitch;
    private double _yaw;
    private DateTime _lastUpdateUtc;

    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (_isArmed == value) return;
            _isArmed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArmedText));
        }
    }

    public string ArmedText => IsArmed ? "ARMED" : "DISARMED";

    public string Mode
    {
        get => _mode;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value;
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
        }
    }

    public double BatteryVoltage { get => _batteryVoltage; set => SetField(ref _batteryVoltage, value); }
    public double BatteryCurrent { get => _batteryCurrent; set => SetField(ref _batteryCurrent, value); }
    public int GpsFix { get => _gpsFix; set => SetField(ref _gpsFix, value); }
    public int Satellites { get => _satellites; set => SetField(ref _satellites, value); }
    public double Altitude { get => _altitude; set => SetField(ref _altitude, value); }
    public double GroundSpeed { get => _groundSpeed; set => SetField(ref _groundSpeed, value); }
    public double Roll { get => _roll; set => SetField(ref _roll, value); }
    public double Pitch { get => _pitch; set => SetField(ref _pitch, value); }
    public double Yaw { get => _yaw; set => SetField(ref _yaw, value); }

    public DateTime LastUpdateUtc
    {
        get => _lastUpdateUtc;
        set
        {
            if (_lastUpdateUtc == value) return;
            _lastUpdateUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkOnline));
        }
    }

    public bool LinkOnline => (DateTime.UtcNow - LastUpdateUtc).TotalSeconds < 2;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(TelemetryState source)
    {
        IsArmed = source.IsArmed;
        Mode = source.Mode;
        BatteryVoltage = source.BatteryVoltage;
        BatteryCurrent = source.BatteryCurrent;
        GpsFix = source.GpsFix;
        Satellites = source.Satellites;
        Altitude = source.Altitude;
        GroundSpeed = source.GroundSpeed;
        Roll = source.Roll;
        Pitch = source.Pitch;
        Yaw = source.Yaw;
        LastUpdateUtc = source.LastUpdateUtc;
    }

    public void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(LinkOnline));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class TelemetryParser
{
    public static TelemetryState ParseTelemetryPacket(string text)
    {
        var result = new TelemetryState();
        int marker = text.IndexOf(";TEL;", StringComparison.Ordinal);
        if (!text.StartsWith("TO_OPERATOR;", StringComparison.Ordinal) || marker < 0)
            return result;

        string payload = text[(marker + ";TEL;".Length)..];
        Dictionary<string, string> fields = payload
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        result.IsArmed = fields.TryGetValue("armed", out string? armed) && (armed == "1" || armed.Equals("true", StringComparison.OrdinalIgnoreCase));
        result.Mode = GetString(fields, "mode", "UNKNOWN");
        result.BatteryVoltage = GetDouble(fields, "batV");
        result.BatteryCurrent = GetDouble(fields, "batA");
        result.GpsFix = GetInt(fields, "gpsFix");
        result.Satellites = GetInt(fields, "sats");
        result.Altitude = GetDouble(fields, "alt");
        result.GroundSpeed = GetDouble(fields, "spd");
        result.Roll = GetDouble(fields, "roll");
        result.Pitch = GetDouble(fields, "pitch");
        result.Yaw = GetDouble(fields, "yaw");
        result.LastUpdateUtc = DateTime.UtcNow;

        return result;
    }

    private static string GetString(IReadOnlyDictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;

    private static double GetDouble(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : 0;
}
