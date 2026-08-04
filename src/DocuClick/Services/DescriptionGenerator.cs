namespace DocuClick.Services;

public enum InputAction
{
    Click,
    EnterKey
}

public static class DescriptionGenerator
{
    public static string Describe(ElementInfo? element, string? fallbackWindowTitle, DateTime timestamp, InputAction action = InputAction.Click)
    {
        if (element is not null && (element.Name is not null || element.ControlType is not null))
        {
            var what = element.ControlType is not null
                ? (element.Name is not null ? $"{element.ControlType} „{element.Name}“" : element.ControlType)
                : $"Element „{element.Name}“";

            var where = element.WindowTitle is not null ? $" im Fenster „{element.WindowTitle}“" : string.Empty;
            return $"{ElementPhrase(action)} {what}{where}";
        }

        var windowPart = fallbackWindowTitle is not null ? $" im Fenster „{fallbackWindowTitle}“" : string.Empty;
        return $"{FallbackPhrase(action)} um {timestamp:HH:mm:ss}{windowPart}";
    }

    private static string ElementPhrase(InputAction action) => action switch
    {
        InputAction.EnterKey => "Eingabe (Enter) bestätigt in",
        _ => "Linksklick auf"
    };

    private static string FallbackPhrase(InputAction action) => action switch
    {
        InputAction.EnterKey => "Enter gedrückt",
        _ => "Linksklick"
    };
}
