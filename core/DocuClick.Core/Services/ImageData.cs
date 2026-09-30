namespace DocuClick.Services;

/// <summary>Image type from the file's own bytes (PNG, JPEG, WebP, GIF, BMP) — for data URIs with the right MIME type whatever the file name says.</summary>
public static class ImageData
{
    public static string MimeType(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
        [(byte)'B', (byte)'M', ..] => "image/bmp",
        _ => "image/png",
    };

    public static string DataUri(byte[] bytes) => $"data:{MimeType(bytes)};base64,{Convert.ToBase64String(bytes)}";
}
