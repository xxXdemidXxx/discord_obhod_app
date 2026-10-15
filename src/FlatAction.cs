using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class FlatAction : Button {
        bool hover,down;
        public FlatAction(){DoubleBuffered=true;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;}
        protected override void OnMouseEnter(System.EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(System.EventArgs e){hover=false;down=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){down=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){down=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnPaint(PaintEventArgs e) {
            var color=Enabled?(down?ControlPaint.Dark(BackColor,.12f):hover?ControlPaint.Light(BackColor,.08f):BackColor):Theme.Surface;
            e.Graphics.Clear(Parent==null?Theme.Background:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=Theme.Round(new Rectangle(0,0,Width-1,Height-1),5))using(var b=new SolidBrush(color))e.Graphics.FillPath(b,p);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(8,0,Width-16,Height),Enabled?ForeColor:Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(4,4,Width-8,Height-8),Theme.Text,color);
        }
    }
}
