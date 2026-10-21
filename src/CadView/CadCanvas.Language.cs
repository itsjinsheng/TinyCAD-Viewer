namespace TinyCAD;

public sealed partial class CadCanvas
{
    // Source units never change when the user changes the display unit.
    public string UnitKey { get; set; } = "图纸单位";
    public string DisplayUnit { get; set; } = "mm";
    private string DisplayLabel(string source) => MeasurementUnits.MillimetersPerUnit(source) != null ? DisplayUnit : L.T(source);
    public string LengthNumber(double value) => Number(value * MeasurementUnits.Factor(UnitKey, DisplayUnit));
    private object Value(double value, int power = 1) => new MeasuredValue(this, value, power, Precision, UnitKey);
    private sealed record MeasuredValue(CadCanvas Canvas, double Raw, int Power, int Precision, string Source)
    {
        public override string ToString() => (Raw * Math.Pow(MeasurementUnits.Factor(Source, Canvas.DisplayUnit), Power)).ToString("F" + Precision, L.Culture);
    }
    public string SelectionDescription => selected == null ? L.T("未选中对象") : CreateShapeDescription(selected)();

    public void RefreshLanguage()
    {
        foreach (var result in Measurements) result.RefreshLanguage();
        labelLayouts.Clear();
        snapName = "";
        CoordinatesChanged?.Invoke($"X {LengthNumber(cursorWorld.X)}   Y {LengthNumber(cursorWorld.Y)}");
        InvalidateVisual();
    }

    private Func<string> CreateShapeDescription(CadShape shape)
    {
        bool approximate = shape.Approximate || shape.Segments.Any(s => s.IsArc && !s.IsCircle);
        object length = Value(shape.Length), area = Value(shape.Area, 2);
        string unit = UnitKey;
        bool model = IsModel;
        return () =>
        {
            string prefix = approximate ? L.T("近似 ") : "";
            string info = $"{shape.Type}   #{shape.Handle}\n" + L.T("图层：") + shape.Layer;
            if (shape.Text != null) info += "\n" + L.T("文字：") + shape.Text;
            else if (shape.Type == "HATCH") info += "\n" + L.T("填充对象：本版本不提供填充面积测量");
            else if (model)
            {
                info += "\n" + L.F($"{prefix}长度：{length} {DisplayLabel(unit)}");
                if (shape.Closed) info += "\n" + L.F($"{prefix}面积：{area} {DisplayLabel(unit)}²（简单边界）");
            }
            if (shape.PaperProjection) info += "\n" + L.T("布局投影对象；尺寸请在模型空间查询");
            return info;
        };
    }
}
