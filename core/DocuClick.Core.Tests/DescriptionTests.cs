using DocuClick.Platform;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

public sealed class DescriptionGeneratorTests
{
    private static readonly DateTime At = new(2026, 9, 25, 14, 3, 7);

    [Fact]
    public void Describes_type_name_and_window()
    {
        var element = new ElementInfo("Speichern", "Taste", "Rechnung.xlsx", null);
        Assert.Equal("Linksklick auf Taste „Speichern“ im Fenster „Rechnung.xlsx“", DescriptionGenerator.Describe(element, null, At));
    }

    [Fact]
    public void Falls_back_to_time_and_window_title_without_element()
    {
        Assert.Equal("Rechtsklick um 14:03:07 im Fenster „Finder“",
            DescriptionGenerator.Describe(null, "Finder", At, InputAction.RightClick));
    }

    [Fact]
    public void Caps_long_element_names()
    {
        var description = DescriptionGenerator.Describe(new ElementInfo(new string('x', 500), "Text", null, null), null, At);
        Assert.Contains(new string('x', DescriptionGenerator.MaxNameLength) + "…", description);
        Assert.DoesNotContain(new string('x', DescriptionGenerator.MaxNameLength + 1), description);
    }

    [Fact]
    public void Screen_rect_contains_is_half_open()
    {
        var rect = new ScreenRect(10, 10, 5, 5);
        Assert.True(rect.Contains(new ScreenPoint(10, 10)));
        Assert.False(rect.Contains(new ScreenPoint(15, 12)));
    }
}
