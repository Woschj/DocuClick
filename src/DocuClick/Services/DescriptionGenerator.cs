namespace DocuClick.Services;

public static class DescriptionGenerator
{
    public static string Describe(ElementInfo? element, string? fallbackWindowTitle, DateTime timestamp)
    {
        if (element is not null && (element.Name is not null || element.ControlType is not null))
        {
            var what = element.ControlType is not null
                ? (element.Name is not null ? $"{element.ControlType} „{element.Name}“" : element.ControlType)
                : $"Element „{element.Name}“";

            var where = element.WindowTitle is not null ? $" im Fenster „{element.WindowTitle}“" : string.Empty;
            return $"Linksklick auf {what}{where}";
        }

        var windowPart = fallbackWindowTitle is not null ? $" im Fenster „{fallbackWindowTitle}“" : string.Empty;
        return $"Linksklick um {timestamp:HH:mm:ss}{windowPart}";
    }
}
