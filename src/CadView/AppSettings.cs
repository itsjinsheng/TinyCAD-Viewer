using System.IO;
using System.Text.Json;

namespace TinyCAD;

public sealed class AppSettings
{
    public static AppSettings Current { get; set; } = new();
    public string DisplayUnit { get; set; } = "mm";
    public string Language { get; set; } = "en-US";
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyCAD", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath));
            if (settings != null)
            {
                if (!L.IsSupported(settings.Language)) settings.Language = "en-US";
                if (!MeasurementUnits.IsDisplayUnit(settings.DisplayUnit)) settings.DisplayUnit = "mm";
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save(string? path = null)
    {
        if (!MeasurementUnits.IsDisplayUnit(DisplayUnit)) throw new ArgumentException("Unsupported display unit");
        if (!L.IsSupported(Language)) throw new ArgumentException("Unsupported language");
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
