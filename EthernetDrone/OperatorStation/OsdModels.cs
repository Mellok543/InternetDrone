using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OperatorStation;

public sealed class OsdLayout
{
    public int Cols { get; set; } = 30;
    public int Rows { get; set; } = 16;
    public List<OsdElement> Elements { get; set; } = new();
}

public sealed class OsdElement : INotifyPropertyChanged
{
    private string _id = "";
    private bool _enabled = true;
    private int _row;
    private int _col;
    private string _template = "";

    public string Id
    {
        get => _id;
        set
        {
            if (_id == value) return;
            _id = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    public int Row
    {
        get => _row;
        set
        {
            if (_row == value) return;
            _row = value;
            OnPropertyChanged();
        }
    }

    public int Col
    {
        get => _col;
        set
        {
            if (_col == value) return;
            _col = value;
            OnPropertyChanged();
        }
    }

    public string Template
    {
        get => _template;
        set
        {
            if (_template == value) return;
            _template = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class OsdDefaults
{
    public static IReadOnlyDictionary<string, string> TestValues { get; } = new Dictionary<string, string>
    {
        ["mode"] = "GUIDED",
        ["alt"] = "35",
        ["bat"] = "15.6",
        ["gps"] = "12",
        ["link"] = "OK",
        ["ping"] = "65"
    };

    public static OsdLayout CreateLayout() => new()
    {
        Cols = 30,
        Rows = 16,
        Elements =
        [
            new OsdElement { Id = "mode", Enabled = true, Row = 0, Col = 0, Template = "MODE:{mode}" },
            new OsdElement { Id = "alt", Enabled = true, Row = 1, Col = 0, Template = "ALT:{alt}m" },
            new OsdElement { Id = "bat", Enabled = true, Row = 0, Col = 20, Template = "BAT:{bat}V" },
            new OsdElement { Id = "gps", Enabled = true, Row = 14, Col = 0, Template = "GPS:{gps}" },
            new OsdElement { Id = "link", Enabled = true, Row = 14, Col = 20, Template = "LINK:{link}" },
            new OsdElement { Id = "ping", Enabled = true, Row = 15, Col = 20, Template = "PING:{ping}ms" }
        ]
    };
}
