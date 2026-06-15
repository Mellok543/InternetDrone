using System.IO;
using System.Text.Json;

namespace OperatorStation;

internal sealed class OsdLayoutStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MellDroneLab",
        "osd_layout.json");

    public string LayoutPath => _layoutPath;

    public OsdLayout Load()
    {
        if (!File.Exists(_layoutPath))
            return OsdDefaults.CreateLayout();

        OsdLayout? layout = JsonSerializer.Deserialize<OsdLayout>(File.ReadAllText(_layoutPath), JsonOptions);
        Normalize(layout);
        return layout ?? OsdDefaults.CreateLayout();
    }

    public void Save(OsdLayout layout)
    {
        Normalize(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(_layoutPath)!);
        File.WriteAllText(_layoutPath, JsonSerializer.Serialize(layout, JsonOptions));
    }

    public static string Serialize(OsdLayout layout)
    {
        Normalize(layout);
        return JsonSerializer.Serialize(layout, CompactJsonOptions);
    }

    private static void Normalize(OsdLayout? layout)
    {
        if (layout == null)
            return;

        layout.Cols = layout.Cols <= 0 ? 30 : layout.Cols;
        layout.Rows = layout.Rows <= 0 ? 16 : layout.Rows;
        layout.Elements ??= new List<OsdElement>();

        foreach (OsdElement element in layout.Elements)
        {
            element.Id = string.IsNullOrWhiteSpace(element.Id) ? "element" : element.Id.Trim();
            element.Template = element.Template ?? "";
            element.Row = Math.Clamp(element.Row, 0, layout.Rows - 1);
            element.Col = Math.Clamp(element.Col, 0, layout.Cols - 1);
        }
    }
}
