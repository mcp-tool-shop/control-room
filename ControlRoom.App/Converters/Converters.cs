using System.Globalization;
using ControlRoom.Domain.Model;

namespace ControlRoom.App.Converters;

/// <summary>
/// Converts 0 to true (for showing empty state), non-zero to false
/// </summary>
public class IntToBoolInverterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
            return count == 0;
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Inverts a boolean value
/// </summary>
public class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts RunStatus to a background color
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RunStatus status)
        {
            return status switch
            {
                RunStatus.Running => Color.FromArgb("#2196F3"),    // Blue
                RunStatus.Succeeded => Color.FromArgb("#4CAF50"),  // Green
                RunStatus.Failed => Color.FromArgb("#F44336"),     // Red
                RunStatus.Canceled => Color.FromArgb("#FF9800"),   // Orange
                _ => Color.FromArgb("#9E9E9E")                     // Gray
            };
        }
        return Color.FromArgb("#9E9E9E");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts IsError bool to text color for log lines
/// </summary>
public class ErrorToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isError && isError)
            return Color.FromArgb("#FF6B6B"); // Red for stderr

        return Color.FromArgb("#E0E0E0"); // Light gray for stdout
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts non-empty string to true
/// </summary>
public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
            return !string.IsNullOrEmpty(s);
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to expand/collapse icon (▼/▶)
/// </summary>
public class BoolToExpandIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isExpanded)
            return isExpanded ? "▼" : "▶";
        return "▶";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts non-zero int to true
/// </summary>
public class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
            return count > 0;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts AI severity string to background color
/// </summary>
public class SeverityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string severity)
        {
            return severity.ToLowerInvariant() switch
            {
                "info" => Color.FromArgb("#3B82F6"),      // Blue
                "warning" => Color.FromArgb("#F59E0B"),   // Amber
                "error" => Color.FromArgb("#EF4444"),     // Red
                "critical" => Color.FromArgb("#DC2626"),  // Dark Red
                _ => Color.FromArgb("#6B7280")            // Gray
            };
        }
        return Color.FromArgb("#6B7280");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
