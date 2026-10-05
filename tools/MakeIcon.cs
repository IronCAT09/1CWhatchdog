// Собирает многоразмерный assets\app.ico из PNG набора иконок.
// Запуск: tools\make-icon.cmd. Размеры до 128 хранятся как 32-битные BMP (их понимают
// и Windows, и System.Drawing), 256 — как PNG (так принято для больших иконок).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly KeyValuePair<int, string>[] Sources =
    {
        new KeyValuePair<int, string>(16, "icon_tray_16x16.png"),
        new KeyValuePair<int, string>(24, "icon_tray_24x24.png"),
        new KeyValuePair<int, string>(32, "icon_tray_32x32.png"),
        new KeyValuePair<int, string>(48, "icon_exe_48x48.png"),
        new KeyValuePair<int, string>(64, "icon_exe_64x64.png"),
        new KeyValuePair<int, string>(128, "icon_exe_128x128.png"),
        new KeyValuePair<int, string>(256, "icon_about_256x256.png"),
    };

    static int Main(string[] args)
    {
        string assets = args.Length > 0 ? args[0] : "assets";
        var images = new List<KeyValuePair<int, byte[]>>();
        foreach (var source in Sources)
        {
            using (var bmp = Load(Path.Combine(assets, source.Value), source.Key))
                images.Add(new KeyValuePair<int, byte[]>(source.Key, source.Key >= 256 ? ToPng(bmp) : ToDib(bmp)));
        }

        string output = Path.Combine(assets, "app.ico");
        using (var w = new BinaryWriter(File.Create(output)))
        {
            w.Write((short)0);               // ICONDIR
            w.Write((short)1);
            w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var image in images)    // ICONDIRENTRY
            {
                byte dim = image.Key >= 256 ? (byte)0 : (byte)image.Key;
                w.Write(dim);
                w.Write(dim);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(image.Value.Length);
                w.Write(offset);
                offset += image.Value.Length;
            }
            foreach (var image in images)
                w.Write(image.Value);
        }
        Console.WriteLine("OK: " + output + " (" + new FileInfo(output).Length + " bytes)");
        return 0;
    }

    static Bitmap Load(string file, int size)
    {
        using (var source = Image.FromFile(file))
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, size, size);
            }
            return bmp;
        }
    }

    static byte[] ToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] ToDib(Bitmap bmp)
    {
        int size = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER: высота удвоена (XOR-картинка + AND-маска)
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(0);                      // BI_RGB
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            for (int y = size - 1; y >= 0; y--) // строки снизу вверх, BGRA
            {
                for (int x = 0; x < size; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B);
                    w.Write(c.G);
                    w.Write(c.R);
                    w.Write(c.A);
                }
            }
            int maskRow = (size + 31) / 32 * 4; // AND-маска нулевая: прозрачность берётся из альфы
            w.Write(new byte[maskRow * size]);
            w.Flush();
            return ms.ToArray();
        }
    }
}
