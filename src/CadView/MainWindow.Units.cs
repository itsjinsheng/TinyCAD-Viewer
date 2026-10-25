using System.IO;
using System.Windows.Controls;

namespace TinyCAD;

public partial class MainWindow
{
    private bool updatingUnits;
    private void UpdateUnitSelection()
    {
        updatingUnits = true;
        try
        {
            Canvas.UnitKey = data?.Units ?? "图纸单位";
            Canvas.DisplayUnit = AppSettings.Current.DisplayUnit;
            UnitSelector.IsEnabled = data == null || MeasurementUnits.MillimetersPerUnit(data.Units) != null;
            string selectedUnit = UnitSelector.IsEnabled ? Canvas.DisplayUnit : "native";
            UnitSelector.SelectedItem = UnitSelector.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == selectedUnit);
            UnitSelector.ToolTip = L.T(UnitSelector.IsEnabled ? "显示单位" : "图纸未指定可转换的单位，保留原始数值");
            UnitsLabel.Text = L.T("单位");
        }
        finally { updatingUnits = false; }
    }
    private void DisplayUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingUnits || Canvas == null || UnitSelector.SelectedItem is not ComboBoxItem item || !MeasurementUnits.IsDisplayUnit((string)item.Tag)) return;
        AppSettings.Current.DisplayUnit = (string)item.Tag;
        UpdateUnitSelection();
        Canvas.RefreshLanguage();
        Properties.Text = Canvas.SelectionDescription;
        try
        {
            AppSettings.Current.Save();
            StatusText.Text = L.T("显示单位已切换并保存");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = L.F($"单位已切换，但无法保存设置：{ex.Message}"); }
    }
}
