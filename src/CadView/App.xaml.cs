using System.IO;
using System.Windows;

namespace TinyCAD;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppSettings.Current = AppSettings.Load();
        L.SetLanguage(AppSettings.Current.Language);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        if (e.Args.Length == 3 && e.Args[0] == "--check")
        {
            try
            {
                var data = CadLoader.Load(e.Args[1]);
                File.WriteAllText(e.Args[2], System.Text.Json.JsonSerializer.Serialize(new { success = true, runtime = Environment.Version.ToString(), spaces = data.Spaces.ToDictionary(s => s.Key, s => s.Value.Count), units = data.Units, warnings = data.Warnings }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[2], System.Text.Json.JsonSerializer.Serialize(new { success = false, error = ex.ToString() })); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, AppInfo.Name + " · " + L.T("操作未完成"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0 && File.Exists(e.Args[0])) window.LoadFile(e.Args[0]);
    }
}
