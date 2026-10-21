using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TinyCAD;
public partial class MainWindow : Window
{
    private DrawingData? data;
    private CancellationTokenSource? loading;
    private readonly string recentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyCAD", "recent.json");
    private List<string> recent = [];
    private int searchPosition = -1;
    private string previousSearch = "";
    private bool updatingSpaces;
    private bool updatingLanguage;
    private CadLoadProgress? loadProgress;
    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.Attach(this);
        UpdateLanguageSelection();
        UpdateUnitSelection();
        L.LanguageChanged += RefreshLanguage;
        Canvas.Status += text => StatusText.Text = text;
        Canvas.CoordinatesChanged += text => CoordinatesText.Text = text;
        Canvas.Selection += text => { Properties.Text = text; if (Canvas.Mode == ToolMode.Select && InspectorTabs.SelectedIndex != 2) InspectorTabs.SelectedIndex = 0; };
        Canvas.Measured += result =>
        {
            InspectorTabs.SelectedIndex = 1;
            Results.Items.Add(result);
            Results.SelectedItem = result;
            MeasureCount.Text = L.F($"{Results.Items.Count} 条记录");
            Results.ScrollIntoView(result);
        };
        Canvas.ModeChanged += mode =>
        {
            if (mode is not ToolMode.Select and not ToolMode.WindowZoom and not ToolMode.PrintArea) InspectorTabs.SelectedIndex = 1;
            UpdateMeasureHint();
            foreach (var button in ToolButtons(ToolBar).Where(b => b.Tag != null))
            {
                bool active = button.Tag.ToString() == mode.ToString();
                button.Background = (Brush)FindResource(active ? "Selected" : "Panel");
                button.BorderBrush = active ? (Brush)FindResource("Accent") : Brushes.Transparent;
            }
            UpdatePrintAreaButton();
        };
        Canvas.PrintAreaChanged += UpdatePrintAreaButton;
        Canvas.SnapToggled += enabled => SnapToggle.IsChecked = enabled;
        Canvas.MeasurementLabelSelected += result =>
        {
            InspectorTabs.SelectedIndex = 1;
            Results.SelectedItem = result;
            Results.ScrollIntoView(result);
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recentPath)!);
            var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingCAD", "recent.json");
            if (!File.Exists(recentPath) && File.Exists(legacyPath)) File.Copy(legacyPath, recentPath);
            if (File.Exists(recentPath)) recent = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(recentPath)) ?? [];
        }
        catch { }
        Closed += (_, _) => { loading?.Cancel(); L.LanguageChanged -= RefreshLanguage; };
        Canvas.SetMode(ToolMode.Select);
        Canvas.GpuModeChanged += enabled => GpuToggle.IsChecked = enabled;
        Canvas.SetGpuEnabled(Environment.GetEnvironmentVariable("TINYCAD_LEGACY_RENDER") != "1");
    }
    private void GpuToggleClick(object sender, RoutedEventArgs e)
    {
        Canvas.SetGpuEnabled(GpuToggle.IsChecked == true); Canvas.Focus();
    }
    private static IEnumerable<Button> ToolButtons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            else foreach (var nested in ToolButtons(child)) yield return nested;
        }
    }
    public async void LoadFile(string path)
    {
        loading?.Cancel();
        var source = new CancellationTokenSource(); loading = source;
        LoadingOverlay.Visibility = Visibility.Visible; LanguageSelector.IsEnabled = false; CancelLoadingButton.IsEnabled = true;
        UpdateLoadProgress(new("正在解析图纸…"));
        StatusText.Text = L.T("正在读取：") + Path.GetFileName(path);
        try
        {
            var progress = new Progress<CadLoadProgress>(value =>
            {
                if (loading == source && !source.IsCancellationRequested) UpdateLoadProgress(value);
            });
            var loaded = await Task.Run(() => CadLoader.Load(path, source.Token, progress), source.Token);
            if (source.IsCancellationRequested || loading != source) return;
            UpdateLoadProgress(new("正在准备显示…"));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (source.IsCancellationRequested || loading != source) return;
            CancelLoadingButton.IsEnabled = false;
            ApplyDrawing(loaded);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            while (loading == source && Canvas.IsRendering) await Task.Delay(50, source.Token);
            if (source.IsCancellationRequested || loading != source) return;
            recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); recent.Insert(0, path); recent = recent.Take(12).ToList();
            try { Directory.CreateDirectory(Path.GetDirectoryName(recentPath)!); File.WriteAllText(recentPath, JsonSerializer.Serialize(recent)); } catch { }
        }
        catch (OperationCanceledException) { if (loading == source) StatusText.Text = L.T("读取已取消"); }
        catch (Exception ex) { if (loading == source) { StatusText.Text = L.T("读取失败，原图保持打开"); MessageBox.Show(this, ex.Message, L.T("无法打开图纸"), MessageBoxButton.OK, MessageBoxImage.Warning); } }
        finally { if (loading == source) { LoadingOverlay.Visibility = Visibility.Collapsed; LanguageSelector.IsEnabled = true; loadProgress = null; loading = null; } source.Dispose(); }
    }
    public void ApplyDrawing(DrawingData loaded)
    {
        data = loaded; Canvas.Layers = data.Layers; EmptyState.Visibility = Visibility.Collapsed;
        var fileTitle = Path.GetFileName(data.FileName); if (fileTitle.Length == 0) fileTitle = L.T("演示图纸");
        CoordinatesText.Text = "X —   Y —";
        Title = fileTitle + " — " + AppInfo.DisplayName;
        FileDetail.Text = L.F($"{data.Spaces.Values.First().Count:N0} 个显示对象 · {data.Layers.Count} 个图层 · 只读");
        UpdateUnitSelection();
        // Equal space names can preserve selection without firing SelectionChanged.
        // Refresh the scene explicitly once, after replacing the whole space list.
        updatingSpaces = true;
        try
        {
            Spaces.ItemsSource = data.Spaces.Keys.ToArray();
            Spaces.SelectedItem = "模型";
        }
        finally { updatingSpaces = false; }
        ApplySelectedSpace();
        PopulateLayers();
        WarningsButton.Content = data.Warnings.Count == 0 ? L.T("读取报告 · 无解析警告") : L.F($"读取报告 · {data.Warnings.Count} 类提示");
        WarningsButton.Foreground = data.Warnings.Count == 0 ? Brushes.LightGreen : Brushes.Orange;
        StatusText.Text = data.Warnings.Count == 0 ? L.T("已打开 · 滚轮缩放，中键拖动，点击对象查询") : L.T("已打开 · 存在显示或解析提示，请查看右侧“读取报告”");
    }
    private static string UnitName(string value) => L.T(UnitKey(value));
    private static string UnitKey(string value) => value switch { "Millimeters" => "mm", "Centimeters" => "cm", "Meters" => "m", "Inches" => "in", "Feet" => "ft", _ => value };
    private void PopulateLayers()
    {
        if (LayerList == null) return;
        LayerList.Children.Clear(); if (data == null) return;
        foreach (var layer in data.Layers.Keys.Where(l => l.Contains(LayerSearch.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(l => l))
        {
            var check = new CheckBox { Content = layer, IsChecked = data.Layers[layer], ToolTip = layer };
            check.Click += (_, _) => { data.Layers[layer] = check.IsChecked == true; Canvas.Refresh(); LayerCount.Text = L.F($"{data.Layers.Count} 个图层 · {data.Layers.Count(l => l.Value)} 个开启"); };
            LayerList.Children.Add(check);
        }
        LayerCount.Text = L.F($"{data.Layers.Count} 个图层 · {data.Layers.Count(l => l.Value)} 个开启");
    }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = L.T("CAD 图纸 (*.dwg;*.dxf)|*.dwg;*.dxf"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) LoadFile(paths[0]);
    }
    private void RecentClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var path in recent) { var item = new MenuItem { Header = path }; item.Click += (_, _) => LoadFile(path); menu.Items.Add(item); }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("暂无最近文件"), IsEnabled = false });
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void SearchNextClick(object sender, RoutedEventArgs e)
    {
        var query = SearchQuery.Text.Trim();
        if (query.Length == 0) { SearchResults.ItemsSource = null; searchPosition = -1; previousSearch = ""; SearchStatus.Text = L.T("请输入要查找的文字。"); return; }
        int next = previousSearch == query ? searchPosition + 1 : 0;
        previousSearch = query;
        var matches = Canvas.Shapes.Where(s => s.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true && Canvas.Visible(s)).ToList();
        SearchResults.ItemsSource = matches;
        if (matches.Count == 0) { searchPosition = -1; SearchStatus.Text = L.T("未找到匹配的可见文字。"); return; }
        SearchResults.SelectedIndex = next % matches.Count;
        SearchResults.ScrollIntoView(SearchResults.SelectedItem);
    }
    private void SearchResultChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not CadShape shape) return;
        if (!Canvas.Visible(shape)) { SearchStatus.Text = L.T("该文字的图层已隐藏，请重新查找。"); return; }
        searchPosition = SearchResults.SelectedIndex;
        Canvas.FocusShape(shape);
        InspectorTabs.SelectedIndex = 2;
        SearchStatus.Text = L.F($"第 {searchPosition + 1} / {SearchResults.Items.Count} 处 · 选中结果定位文字");
    }
    private void SearchQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SearchNextClick(sender, e); e.Handled = true; }
    }
    private void SpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingSpaces) ApplySelectedSpace();
    }
    private void ApplySelectedSpace()
    {
        if (data == null || Spaces.SelectedItem is not string space) return;
        Canvas.IsModel = space == "模型"; Canvas.SetMode(ToolMode.Select); Canvas.SetScene(data.Spaces[space]); Results.Items.Clear(); MeasureCount.Text = L.T("0 条记录");
        searchPosition = -1; previousSearch = ""; SearchResults.ItemsSource = null; SearchStatus.Text = L.T("在当前空间已显示的文字中查找。");
        Properties.Text = Canvas.IsModel ? L.T("点击实体查询，或选择测量工具。") : L.T("布局预览：测量请切换模型空间。非矩形或非俯视视口见读取报告。");
    }
    private void PrecisionChanged(object sender, SelectionChangedEventArgs e) { if (Canvas != null) Canvas.Precision = Math.Max(0, Decimals.SelectedIndex); }
    private void LayerSearchChanged(object sender, TextChangedEventArgs e) => PopulateLayers();
    private void AllLayersClick(object sender, RoutedEventArgs e)
    {
        if (data == null) return; bool on = ((Button)sender).Tag.ToString() == "on";
        foreach (var key in data.Layers.Keys.ToArray()) data.Layers[key] = on;
        PopulateLayers(); Canvas.Refresh();
    }
    private void ToolClick(object sender, RoutedEventArgs e) => Canvas.SetMode(Enum.Parse<ToolMode>(((Button)sender).Tag.ToString()!));
    private void FitClick(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void ZoomInClick(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.5);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.5);
    private void ViewOptionClick(object sender, RoutedEventArgs e)
    {
        Canvas.SnapEnabled = SnapToggle.IsChecked == true; Canvas.ShowGrid = GridToggle.IsChecked == true; Canvas.ShowLineWeights = WeightToggle.IsChecked == true; Canvas.LightBackground = LightToggle.IsChecked == true; Canvas.Refresh();
    }
    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Canvas.HighlightMeasurement(Results.SelectedItem as MeasureResult);
    }
    private void ResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(Results, source) is not ListBoxItem { Content: MeasureResult result }) return;
        Canvas.FocusMeasurement(result);
        e.Handled = true;
    }
    private void ClearClick(object sender, RoutedEventArgs e) { Canvas.ClearMeasurements(); Results.Items.Clear(); MeasureCount.Text = L.T("0 条记录"); }
    private void DeleteMeasurementClick(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not MeasureResult result) { StatusText.Text = L.T("请先选择要删除的测量记录"); return; }
        int index = Results.SelectedIndex;
        if (!Canvas.DeleteMeasurement(result)) return;
        Results.Items.RemoveAt(index);
        Results.SelectedIndex = Math.Min(index, Results.Items.Count - 1);
        MeasureCount.Text = L.F($"{Results.Items.Count} 条记录");
        StatusText.Text = L.T("已删除选中的测量记录");
    }
    private void CancelClick(object sender, RoutedEventArgs e) { loading?.Cancel(); StatusText.Text = L.T("已请求取消读取"); LoadingStage.Text = L.T("已请求取消读取"); }
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            InspectorTabs.SelectedIndex = 2;
            SearchQuery.Focus(); SearchQuery.SelectAll(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape)
        {
            Results.SelectedIndex = -1;
            SearchResults.SelectedIndex = -1;
            Canvas.CancelSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OpenClick(this, e); e.Handled = true; }
    }
    private void WarningsClick(object sender, RoutedEventArgs e) => ShowText(L.T("图纸读取报告"), data == null ? L.T("尚未打开图纸。") : data.FileName + "\n" + L.T("图纸单位：") + UnitName(data.Units) + "\n\n" + (data.Warnings.Count == 0 ? L.T("解析器没有报告警告。仍请在正式使用前对照原图核验。") : string.Join("\n\n", data.Warnings.Select(w => L.F($"[{w.Value} 次] {data.LocalizeWarning(w.Key)}")))));
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        string text = AppInfo.DisplayName + "\n\n" + L.T("HelpBody") + "\n";
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".txt")))
        { using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!); text += "\n──────── " + name + " ────────\n" + reader.ReadToEnd(); }
        ShowText(L.T("帮助与开源许可"), text);
    }
    private void ShowText(string title, string text)
    {
        var window = new Window { Owner = this, Title = title, Width = 760, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowTheme.Attach(window);
        window.Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) }; window.ShowDialog();
    }
}

