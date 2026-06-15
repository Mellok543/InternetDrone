using System.IO;
using System.Text.Json;

namespace OperatorStation;

internal sealed class SettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MellDroneLab",
        "operator-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool Exists => File.Exists(_settingsPath);

    public OperatorSettings? Load()
    {
        if (!Exists) return null;

        var settings = JsonSerializer.Deserialize<OperatorSettings>(File.ReadAllText(_settingsPath));
        if (settings == null) return null;

        Normalize(settings);
        return settings;
    }

    public void Save(OperatorSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void Normalize(OperatorSettings settings)
    {
        settings.VpsHost = string.IsNullOrWhiteSpace(settings.VpsHost)
            ? "YOUR_VPS_IP"
            : settings.VpsHost.Trim();

        settings.VpsPort = settings.VpsPort is >= 1 and <= 65535 ? settings.VpsPort : 50555;
        settings.Room = string.IsNullOrWhiteSpace(settings.Room) ? "mell-drone" : settings.Room.Trim();

        settings.RollAxis = NormalizeAxis(settings.RollAxis, "X");
        settings.PitchAxis = NormalizeAxis(settings.PitchAxis, "Y");
        settings.ThrottleAxis = NormalizeAxis(settings.ThrottleAxis, "Z");
        settings.YawAxis = NormalizeAxis(settings.YawAxis, "R");

        settings.ArmSwitch = NormalizeSwitch(settings.ArmSwitch, settings.ArmButton);
        settings.LoiterSwitch = NormalizeSwitch(settings.LoiterSwitch, settings.LoiterButton);
        settings.RtlSwitch = NormalizeSwitch(settings.RtlSwitch, settings.RtlButton);
        settings.LandSwitch = NormalizeSwitch(settings.LandSwitch, settings.LandButton);
        settings.ManualSwitch = NormalizeSwitch(settings.ManualSwitch, settings.ManualButton);
    }

    private static string NormalizeAxis(string? axis, string fallback) =>
        OperatorConstants.AxisNames.Contains(axis) ? axis! : fallback;

    private static string NormalizeSwitch(string? value, int legacyButton)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return legacyButton > 0 ? $"Button {legacyButton}" : OperatorConstants.Unassigned;
    }
}
