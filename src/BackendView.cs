using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ZapretDesktop {
    internal sealed class DashboardSnapshot {
        internal string Summary,ScanSummary,Configuration,Context;
        internal readonly string[] Status=new string[ServiceChecks.Targets.Length],Times=new string[ServiceChecks.Targets.Length],Details=new string[ServiceChecks.Targets.Length];
        internal readonly Color[] Colors=new Color[ServiceChecks.Targets.Length];
        internal DateTime? CheckedAt;
    }
    // Paint the previous real frame while the child controls are rearranged,
    // then reveal the new frame. Never change opacity of the whole window.
    internal sealed class BackendTransition : Control {
        Bitmap frame,nextFrame;readonly Timer animation=new Timer{Interval=16};DateTime began;float alpha=1;
        internal bool Active {get{return frame!=null;}}
        internal BackendTransition(){DoubleBuffered=true;Visible=false;animation.Tick+=(s,e)=>{alpha=Math.Max(0,1-(float)(DateTime.UtcNow-began).TotalMilliseconds/160);if(alpha<=0)Finish();else Invalidate();};}
        internal void BeginCapture(Control surface){
            Finish();if(surface.Width<1 || surface.Height<1)return;
            frame=new Bitmap(surface.Width,surface.Height);surface.DrawToBitmap(frame,new Rectangle(Point.Empty,surface.Size));
            Bounds=surface.ClientRectangle;alpha=1;Visible=true;BringToFront();Invalidate();
        }
        internal void Reveal(){if(frame==null)return;Visible=false;nextFrame=new Bitmap(Parent.Width,Parent.Height);Parent.DrawToBitmap(nextFrame,new Rectangle(Point.Empty,Parent.Size));Visible=true;BringToFront();began=DateTime.UtcNow;animation.Start();}
        internal void Finish(){animation.Stop();Visible=false;if(frame!=null){frame.Dispose();frame=null;}if(nextFrame!=null){nextFrame.Dispose();nextFrame=null;}}
        protected override void OnPaintBackground(PaintEventArgs e){if(nextFrame!=null)e.Graphics.DrawImage(nextFrame,ClientRectangle);else e.Graphics.Clear(Theme.Background);}
        protected override void OnPaint(PaintEventArgs e){if(frame==null)return;using(var attributes=new ImageAttributes()){var matrix=new ColorMatrix{Matrix33=alpha};attributes.SetColorMatrix(matrix);e.Graphics.DrawImage(frame,ClientRectangle,0,0,frame.Width,frame.Height,GraphicsUnit.Pixel,attributes);}}
        protected override void Dispose(bool disposing){if(disposing){Finish();animation.Dispose();}base.Dispose(disposing);}
    }
    internal sealed class SmoothPanel : Panel {
        internal SmoothPanel(){DoubleBuffered=true;ResizeRedraw=true;}
    }
}
