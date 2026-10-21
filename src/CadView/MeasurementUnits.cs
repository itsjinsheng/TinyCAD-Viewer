namespace TinyCAD;

/// <summary>Conversion affects displayed values only; CAD geometry stays in source units.</summary>
public static class MeasurementUnits
{
    public static bool IsDisplayUnit(string? value) => value is "mm" or "in";
    public static double? MillimetersPerUnit(string source) => source switch
    {
        "Millimeters" or "mm" => 1, "Centimeters" or "cm" => 10,
        "Meters" or "m" => 1000, "Kilometers" => 1e6,
        "Inches" or "in" => 25.4, "Feet" or "ft" => 304.8,
        "Yards" => 914.4, "Miles" => 1609344,
        "Microinches" => 0.0000254, "Mils" => 0.0254,
        "Angstroms" => 1e-7, "Nanometers" => 1e-6, "Microns" => 0.001,
        "Decimeters" => 100, "Decameters" => 10000, "Hectometers" => 100000,
        "Gigameters" => 1e12, "AstronomicalUnits" => 149597870700000,
        "LightYears" => 9.4607304725808e18, "Parsecs" => 3.0856775814913673e19,
        "USSurveyFeet" => 1200000d / 3937, "USSurveyInches" => 100000d / 3937,
        "USSurveyYards" => 3600000d / 3937, "USSurveyMiles" => 6336000000d / 3937,
        _ => null
    };
    public static double Factor(string source, string target) =>
        MillimetersPerUnit(source) is double mm ? mm / (target == "in" ? 25.4 : 1) : 1;
}
