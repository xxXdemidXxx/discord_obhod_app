using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class CreateIcon {
    static GraphicsPath Rounded(float x, float y, float width, float height, float radius) {
        var path = new GraphicsPath(); float d = radius * 2;
        path.AddArc(x,y,d,d,180,90); path.AddArc(x+width-d,y,d,d,270,90);
        path.AddArc(x+width-d,y+height-d,d,d,0,90); path.AddArc(x,y+height-d,d,d,90,90); path.CloseFigure(); return path;
    }
    static void Main(string[] args) {
        int[] sizes = {16,24,32,48,64,128,256}; var pictures = new List<byte[]>();
        foreach (int size in sizes) {
            using (var bitmap = new Bitmap(size,size,PixelFormat.Format32bppArgb)) using (var g = Graphics.FromImage(bitmap)) {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (var path = Rounded(size*.025f,size*.025f,size*.95f,size*.95f,size*.22f))
                using (var fill = new LinearGradientBrush(new Rectangle(0,0,size,size),Color.FromArgb(34,48,62),Color.FromArgb(16,20,30),45))
                using (var border = new Pen(Color.FromArgb(80,116,220,183), Math.Max(1,size*.016f))) {g.FillPath(fill,path);g.DrawPath(border,path);}
                using (var pen = new Pen(Color.FromArgb(116,220,183), size*.080f)) {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawArc(pen,size*.245f,size*.29f,size*.51f,size*.51f,-45,270);
                    g.DrawLine(pen,size*.50f,size*.21f,size*.50f,size*.49f);
                }
                using (var stream = new MemoryStream()) {bitmap.Save(stream,ImageFormat.Png);pictures.Add(stream.ToArray());}
                if (size == 256) bitmap.Save(Path.ChangeExtension(args[0], ".png"),ImageFormat.Png);
            }
        }
        using (var output = new BinaryWriter(File.Create(args[0]))) {
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
