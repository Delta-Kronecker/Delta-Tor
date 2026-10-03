// make-icon.cs - convert PNG to multi-size ICO file
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class MakeIcon
{
    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: make-icon.exe input.png output.ico"); return; }
        Bitmap src = new Bitmap(args[0]);
        int[] sizes = { 16, 32, 48, 64, 128, 256 };
        using (var fs = new FileStream(args[1], FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            // ICO header
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);

            int dataOffset = 6 + sizes.Length * 16;
            var entries = new byte[sizes.Length * 16][];
            var bitmaps = new MemoryStream[sizes.Length];

            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                Bitmap resized = new Bitmap(s, s);
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawImage(src, 0, 0, s, s);
                }
                MemoryStream ms = new MemoryStream();
                resized.Save(ms, ImageFormat.Png);
                resized.Dispose();
                bitmaps[i] = ms;
                int imgSize = (int)ms.Length;
                int bmpSize = 40 + s * s * 4 + s * (s / 8); // approximate

                entries[i] = new byte[16];
                BinaryWriter ew = new BinaryWriter(new MemoryStream(entries[i]));
                ew.Write((byte)(s >= 256 ? 0 : s)); // width
                ew.Write((byte)(s >= 256 ? 0 : s)); // height
                ew.Write((byte)0); // color palette
                ew.Write((byte)0); // reserved
                ew.Write((ushort)1); // color planes
                ew.Write((ushort)32); // bits per pixel
                ew.Write(imgSize);
                ew.Write(dataOffset);
                ew.Flush();
                dataOffset += imgSize;
            }

            // Write header
            for (int i = 0; i < sizes.Length; i++)
                fs.Write(entries[i], 0, 16);

            // Write image data
            for (int i = 0; i < sizes.Length; i++)
            {
                byte[] d = bitmaps[i].ToArray();
                fs.Write(d, 0, d.Length);
                bitmaps[i].Dispose();
            }
        }
        Console.WriteLine("wrote " + args[1] + " (" + sizes.Length + " sizes)");
    }
}
