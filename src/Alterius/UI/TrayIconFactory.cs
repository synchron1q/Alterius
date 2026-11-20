using System.Drawing.Drawing2D;
using Alterius.Core;

namespace Alterius.UI;

/// <summary>
/// Dynamically renders crisp 32x32 system tray icons representing agent state
/// and ensures native GDI icon handles are properly freed.
/// </summary>
public static class TrayIconFactory
{
    public static Icon CreateCircleIcon(Color fillColor, Color borderColor, Color? innerDotColor = null)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var fillBrush = new SolidBrush(fillColor))
            {
                g.FillEllipse(fillBrush, 3, 3, 26, 26);
            }

            using (var borderPen = new Pen(borderColor, 2.5f))
            {
                g.DrawEllipse(borderPen, 3, 3, 26, 26);
            }

            if (innerDotColor.HasValue)
            {
                using var dotBrush = new SolidBrush(innerDotColor.Value);
                g.FillEllipse(dotBrush, 11, 11, 10, 10);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
