using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FaceFlow.App.Mvvm;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var b = v is bool bb && bb;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var has = v switch
        {
            null => false,
            string str => str.Length > 0,
            _ => true
        };
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        long n = v is null ? 0 : System.Convert.ToInt64(v);
        var show = n > 0;
        if (Invert) show = !show;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class PercentToWidth : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        double pct = v is null ? 0 : System.Convert.ToDouble(v);
        double max = p is null ? 100 : System.Convert.ToDouble(p, CultureInfo.InvariantCulture);
        return Math.Max(0, Math.Min(max, max * pct / 100.0));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
