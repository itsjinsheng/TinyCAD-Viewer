using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TinyCAD;

public partial class MainWindow
{
    private void UpdateLanguageSelection()
    {
        updatingLanguage = true;
        try { LanguageSelector.SelectedItem = LanguageSelector.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == L.Language); }
        finally { updatingLanguage = false; }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingLanguage || LanguageSelector.SelectedItem is not ComboBoxItem item) return;
        L.SetLanguage((string)item.Tag);
        try
        {
            AppSettings.Current.Language = L.Language;
            AppSettings.Current.Save();
            StatusText.Text = L.T("语言已切换并保存");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = L.F($"语言已切换，但无法保存设置：{ex.Message}"); }
    }

    private void RefreshLanguage()
    {
        UpdateLanguageSelection();
        Title = data == null ? AppInfo.DisplayName : Path.GetFileName(data.FileName) + " — " + AppInfo.DisplayName;
        UpdateUnitSelection();
        FileDetail.Text = data == null ? L.T("DWG / DXF · 本地只读")
            : L.F($"{data.Spaces.Values.First().Count:N0} 个显示对象 · {data.Layers.Count} 个图层 · 只读");
        MeasureCount.Text = L.F($"{Results.Items.Count} 条记录");
        UpdateMeasureHint();
        Properties.Text = Canvas.SelectionDescription;
        SearchStatus.Text = SearchResults.SelectedIndex >= 0
            ? L.F($"第 {SearchResults.SelectedIndex + 1} / {SearchResults.Items.Count} 处 · 选中结果定位文字")
            : L.T("在当前空间已显示的文字中查找。");
        PopulateLayers();
        if (data == null) LayerCount.Text = L.T("尚无图层");
        WarningsButton.Content = data == null ? L.T("读取报告") : data.Warnings.Count == 0
            ? L.T("读取报告 · 无解析警告") : L.F($"读取报告 · {data.Warnings.Count} 类提示");
        Canvas.RefreshLanguage();
        if (loadProgress != null) UpdateLoadProgress(loadProgress);
        else StatusText.Text = Canvas.FocusedMeasurement?.CurrentText ?? L.T(data == null ? "Ctrl+O 打开图纸 · 滚轮缩放 · 中键平移 · Esc 返回选择" : "已打开 · 滚轮缩放，中键拖动，点击对象查询");
    }

    private void UpdateMeasureHint()
    {
        MeasureHint.Text = Canvas.Mode switch
            {
                ToolMode.Distance => L.T("依次点击两点。按住 Shift 可正交取点。"),
                ToolMode.Chain => L.T("连续取点，双击或 Enter 完成；捕捉开启可吸附起点。"),
                ToolMode.Angle => L.T("依次点击：顶点 → 第一边 → 第二边。"),
                ToolMode.Area => L.T("沿边界取点，点击起点或双击闭合；右键取消。"),
                ToolMode.Radius => L.T("点击圆或圆弧，读取半径与直径。"),
                ToolMode.Entity => L.T("点击实体，读取长度、周长和封闭面积。"),
                _ => L.T("选择上方测量工具后，在图中取点。")
            };
    }

    private void UpdateLoadProgress(CadLoadProgress value)
    {
        loadProgress = value;
        LoadingStage.Text = L.T(value.Stage);
        LoadingDetail.Text = value.Objects > 0 ? L.F($"已处理 {value.Objects:N0} 个对象") : L.T("大图纸可能需要一些时间");
    }
}
