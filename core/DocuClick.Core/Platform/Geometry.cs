namespace DocuClick.Platform;

/// <summary>
/// A point in global screen coordinates, origin top-left of the primary
/// screen. Units are whatever the platform's input events report: physical
/// pixels on Windows (the app is PerMonitorV2 DPI-aware), points on macOS.
/// <see cref="CapturedFrame.Scale"/> converts these units into the captured
/// bitmap's pixels.
/// </summary>
public readonly record struct ScreenPoint(double X, double Y)
{
    public override string ToString() => $"({X:0}, {Y:0})";
}

/// <summary>A rectangle in the same global screen coordinate space as <see cref="ScreenPoint"/>.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(ScreenPoint point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
}
