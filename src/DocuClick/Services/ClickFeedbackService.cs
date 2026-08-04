using System.Media;

namespace DocuClick.Services;

/// <summary>
/// Audible confirmation that a click was actually captured — the capture
/// itself is intentionally invisible (no screen flash), so without this
/// there is no way to tell whether the tool is doing anything at all.
/// SystemSounds.Play() is fire-and-forget and safe to call off the UI thread.
/// </summary>
public static class ClickFeedbackService
{
    public static void PlayCaptured() => SystemSounds.Beep.Play();

    public static void PlaySkipped() => SystemSounds.Asterisk.Play();

    public static void PlayError() => SystemSounds.Hand.Play();
}
