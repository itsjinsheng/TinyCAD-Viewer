namespace TinyCAD;

/// <summary>Product version is defined once in the project file.</summary>
public static class AppInfo
{
    public const string Name = "TinyCAD Viewer";
    public static string Version { get; } = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(AppInfo).Assembly)!
        .InformationalVersion.Split('+')[0];
    public static string DisplayName => $"{Name} {Version}";
}
