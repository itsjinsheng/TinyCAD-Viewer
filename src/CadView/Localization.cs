using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace TinyCAD;

public sealed class L : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string[]> messages = LoadMessages();
    public static L Current { get; } = new();
    public static string Language { get; private set; } = "zh-CN";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static IReadOnlyDictionary<string, string[]> Messages => messages;
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? LanguageChanged;

    private static Dictionary<string, string[]> LoadMessages()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("TinyCAD.Strings.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }

    public static bool IsSupported(string? language) => language is "zh-CN" or "en-US" or "ko-KR";
    public static void SetLanguage(string language)
    {
        if (!IsSupported(language)) throw new ArgumentException("Unsupported language", nameof(language));
        if (Language == language) return;
        Language = language;
        Current.Revision++;
        Current.PropertyChanged?.Invoke(Current, new(nameof(Revision)));
        LanguageChanged?.Invoke();
    }

    public static string T(string key) => messages.TryGetValue(key, out var values)
        ? values[Language == "en-US" ? 1 : Language == "ko-KR" ? 2 : 0] : key;
    public static string Format(string key, params object?[] args) => string.Format(Culture, T(key), args);
    public static string F(FormattableString text) => Format(text.Format, text.GetArguments());
}

// Each binding observes the language revision, including bindings inside inactive tabs/templates.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(L.Revision))
    {
        Source = L.Current,
        Mode = BindingMode.OneWay,
        Converter = new TranslationConverter(key)
    }.ProvideValue(serviceProvider);

    private sealed class TranslationConverter(string key) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T(key);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class SpaceNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is string name ? name == "模型" ? L.T("模型") : name : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
