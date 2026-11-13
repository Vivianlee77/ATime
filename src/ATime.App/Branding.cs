using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ATime.App;

internal static class Branding
{
    private const string IconResource = "ATime.Branding.AppIcon.ico";
    private static readonly Lazy<ImageSource> Image = new(LoadImage);

    public static ImageSource WindowIcon => Image.Value;

    public static Icon CreateTrayIcon()
    {
        using var stream = OpenIcon();
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        // NotifyIcon borrows this handle; App owns the independent copy until shutdown.
        return (Icon)icon.Clone();
    }

    private static ImageSource LoadImage()
    {
        using var stream = OpenIcon();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(image => image.PixelWidth).First();
        frame.Freeze();
        return frame;
    }

    private static Stream OpenIcon() => typeof(Branding).Assembly.GetManifestResourceStream(IconResource)
        ?? throw new InvalidOperationException("ATime 内嵌图标资源缺失，请重新下载完整运行包。");
}
