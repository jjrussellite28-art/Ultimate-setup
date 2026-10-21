using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

/// <summary>An exe's icon in high resolution (the shell's 256 px image, as Explorer shows it), as png bytes.</summary>
public static class ExeIcons
{
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int w, int h)
    {
        public int Width = w;
        public int Height = h;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPixel;
        public IntPtr Bits;
        // BITMAPINFOHEADER of a DIB section: a negative height means the rows run top-down.
        public int HeaderSize, HeaderWidth, HeaderHeight;
        public short HeaderPlanes, HeaderBitCount;
        public int Compression, SizeImage, XPels, YPels, ColorsUsed, ColorsImportant;
        public int MaskR, MaskG, MaskB;
        public IntPtr Section;
        public int Offset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    /// <summary>Png of the icon, or null if the file has none or the shell refuses.</summary>
    public static byte[]? Render(string exePath, int size = 256)
    {
        IntPtr hbitmap = IntPtr.Zero;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = ImageFactoryId;
            SHCreateItemFromParsingName(exePath, IntPtr.Zero, ref iid, out factory);
            // ICONONLY (0x04): the icon itself, never a thumbnail of the file's content.
            if (factory.GetImage(new SIZE(size, size), 0x04, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.Bits == IntPtr.Zero || bm.BitsPixel != 32) return null;

            // The shell hands out a 32-bit DIB with alpha; copy it out before the handle goes away.
            using var view = new System.Drawing.Bitmap(bm.Width, bm.Height, bm.WidthBytes, PixelFormat.Format32bppPArgb, bm.Bits);
            if (bm.HeaderHeight > 0) view.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY); // bottom-up rows
            using var copy = new System.Drawing.Bitmap(view.Width, view.Height, PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(copy)) g.DrawImage(view, 0, 0, view.Width, view.Height);
            using var stream = new MemoryStream();
            copy.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ExternalException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
