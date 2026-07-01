using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ConveyorInspector.Converters;

// ── BoolToVisibility ──────────────────────────────────────────────
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object param, CultureInfo c)
    {
        bool b = value is bool bv && bv;
        bool inverse = param?.ToString() == "inverse";
        return (inverse ? !b : b) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is Visibility vis && vis == Visibility.Visible;
}

// ── InverseBool ────────────────────────────────────────────────────
[ValueConversion(typeof(bool), typeof(bool))]
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => !(bool)v;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => !(bool)v;
}

// ── BoolToColor (for button text switching) ───────────────────────
[ValueConversion(typeof(bool), typeof(string))]
public class BoolToColorConverter : IValueConverter
{
    /// <summary>param = "TrueText|FalseText"</summary>
    public object Convert(object value, Type t, object param, CultureInfo c)
    {
        bool b = value is bool bv && bv;
        string[] parts = param?.ToString()?.Split('|') ?? ["True", "False"];
        return b ? (parts.Length > 0 ? parts[0] : "True")
                 : (parts.Length > 1 ? parts[1] : "False");
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => Binding.DoNothing;
}

// ── LogColor ──────────────────────────────────────────────────────
public class LogColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object param, CultureInfo c)
        => value?.ToString() switch
        {
            "Warn"  => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
            "Error" => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            _       => new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8))
        };
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => Binding.DoNothing;
}

// ── PercentageConverter (for progress bars) ───────────────────────
// Converts (double 0..1, double containerWidth) → pixel width
public class PercentageConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object param, CultureInfo c)
    {
        if (values.Length < 2) return 0d;

        double ratio = 0;
        if (values[0] is double d0)
            ratio = param?.ToString() == "div100" ? d0 / 100.0 : d0;
        else if (values[0] is float f0)
            ratio = f0;

        double width = values[1] is double dw ? dw : 100;
        return Math.Max(0, Math.Min(width, ratio * width));
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c)
        => throw new NotImplementedException();
}
