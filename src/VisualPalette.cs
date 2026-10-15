using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
namespace ZapretDesktop {
    internal sealed class VisualPalette {
        internal readonly string Id,Name;
        internal readonly Color Background,Surface,Input,Border,Text,Muted,Accent,Button;
        internal VisualPalette(string id,string name,Color background,Color surface,Color input,Color border,Color text,Color muted,Color accent,Color button){Id=id;Name=name;Background=background;Surface=surface;Input=input;Border=border;Text=text;Muted=muted;Accent=accent;Button=button;}
    }
    internal sealed class ThemeChoice : Button {
        internal readonly VisualPalette Palette;
        internal ThemeChoice(VisualPalette palette){Palette=palette;Name="Theme-"+palette.Id;Text=palette.Name;AccessibleName="Тема: "+palette.Name;Cursor=Cursors.Hand;DoubleBuffered=true;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;}
        protected override void OnPaint(PaintEventArgs e){
            var g=e.Graphics;g.Clear(Parent.BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=Theme.SystemScale;int p=(int)(12*scale), preview=(int)(55*scale);
            using(var path=Theme.Round(new Rectangle(1,1,Width-3,Height-3),8))using(var fill=new SolidBrush(Palette.Background))using(var border=new Pen(Theme.Current==Palette.Id?Theme.Accent:Theme.Border,Theme.Current==Palette.Id?2:1)){g.FillPath(fill,path);g.DrawPath(border,path);}
            using(var b=new SolidBrush(Palette.Surface))g.FillRectangle(b,p,p,Width-2*p,preview);
            using(var b=new SolidBrush(Palette.Input))g.FillRectangle(b,p+6,p+6,Width-2*p-12,preview-12);
            using(var b=new SolidBrush(Palette.Accent))g.FillRectangle(b,p+14,p+15,(int)(45*scale),preview-30);
            TextRenderer.DrawText(g,Text,Font,new Rectangle(p,p+preview+6,Width-2*p,Height-p-preview-10),Palette.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Theme.Current==Palette.Id)TextRenderer.DrawText(g,"✓",Font,new Rectangle(Width-p-24,Height-32,24,22),Palette.Text,TextFormatFlags.Right);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(g,new Rectangle(5,5,Width-10,Height-10),Palette.Text,Palette.Background);
        }
    }
}
