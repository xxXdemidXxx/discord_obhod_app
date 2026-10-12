using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class CreateIcon {
    // Classic DIB frames keep small icons compatible with .NET Framework's
    // Icon loader. Windows uses the PNG frame for the large Explorer view.
    static byte[] EncodeFrame(Bitmap bitmap) {
        using (var stream = new MemoryStream()) {
            if (bitmap.Width == 256) bitmap.Save(stream, ImageFormat.Png);
            else {
                int size = bitmap.Width, maskStride = ((size+31)/32)*4;
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                    writer.Write(40); writer.Write(size); writer.Write(size*2);
                    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0);
                    writer.Write(size*size*4+maskStride*size);
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    for (int y=size-1; y>=0; y--) for (int x=0; x<size; x++) {
                        var pixel=bitmap.GetPixel(x,y);
                        writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                    }
                    for (int y=size-1; y>=0; y--) {
                        var mask=new byte[maskStride];
                        for (int x=0; x<size; x++) if (bitmap.GetPixel(x,y).A==0) mask[x/8]|=(byte)(0x80>>(x%8));
                        writer.Write(mask);
                    }
                }
            }
            return stream.ToArray();
        }
    }
    static void Main(string[] args) {
        if (args.Length != 2) throw new ArgumentException("Expected source PNG and destination ICO paths");
        int[] sizes = {16,24,32,48,64,128,256}; var pictures = new List<byte[]>();
        using (var source = Image.FromFile(args[0]))
        foreach (int size in sizes) {
            using (var bitmap = new Bitmap(size,size,PixelFormat.Format32bppArgb)) using (var g = Graphics.FromImage(bitmap)) {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float scale = Math.Min((float)size/source.Width, (float)size/source.Height);
                float width = source.Width*scale, height = source.Height*scale;
                using (var attributes = new ImageAttributes()) {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(source, Rectangle.Round(new RectangleF((size-width)/2,(size-height)/2,width,height)),
                        0,0,source.Width,source.Height, GraphicsUnit.Pixel, attributes);
                }
                pictures.Add(EncodeFrame(bitmap));
            }
        }
        using (var output = new BinaryWriter(File.Create(args[1]))) {
            output.Write((ushort)0);output.Write((ushort)1);output.Write((ushort)sizes.Length);
            uint offset = (uint)(6+16*sizes.Length);
            for (int i=0;i<sizes.Length;i++) {
                output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)0);output.Write((byte)0);
                output.Write((ushort)1);output.Write((ushort)32);output.Write((uint)pictures[i].Length);output.Write(offset);offset+=(uint)pictures[i].Length;
            }
            foreach (var picture in pictures) output.Write(picture);
        }
    }
}
