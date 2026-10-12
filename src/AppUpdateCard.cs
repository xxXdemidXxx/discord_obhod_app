using System;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class UpdateProgress : Control {
        int percent;
        public int Percent {get{return percent;}set{percent=Math.Max(0,Math.Min(100,value));Invalidate();}}
        public UpdateProgress(){DoubleBuffered=true;BackColor=Theme.Surface;}
        protected override void OnPaint(PaintEventArgs e){using(var track=new SolidBrush(Theme.Border))e.Graphics.FillRectangle(track,ClientRectangle);using(var fill=new SolidBrush(Theme.Accent))e.Graphics.FillRectangle(fill,0,0,Width*percent/100,Height);}
    }
    public sealed class AppUpdateCard : SurfacePanelBase {
        readonly Label title=Theme.Label("Приложение",18,true),version=Theme.Label("Версия "+ShellUpdates.Current,10),status=Theme.Label("",12,true),detail=Theme.Label("",10);
        public readonly Button Action=Theme.Button("Проверить обновления",true),Cancel=Theme.Button("Отмена");
        public readonly ToggleOption Startup=new ToggleOption{Text="Проверять при запуске"};
        public readonly ToggleOption AutoInstall=new ToggleOption{Text="Скачивать и устанавливать автоматически"};
        public readonly UpdateProgress Progress=new UpdateProgress();
        public AppUpdateCard(){BackColor=Theme.Surface;Height=S(322);version.ForeColor=detail.ForeColor=Theme.Muted;Controls.AddRange(new Control[]{title,version,status,detail,Action,Cancel,Startup,AutoInstall,Progress});Cancel.Visible=Progress.Visible=false;}
        public void Render(AppUpdateSession session,string blocked=null){
            Action.Text=session.ActionText;status.Text=session.Message;detail.Text=blocked??session.Detail;
            status.ForeColor=session.State==AppUpdateState.Failed?Theme.Warning:session.State==AppUpdateState.Current?Theme.Success:Theme.Text;
            Cancel.Visible=Progress.Visible=session.State==AppUpdateState.Downloading;Cancel.Enabled=Cancel.Visible;Progress.Percent=session.Percent;LayoutCard();
        }
        protected override void OnResize(EventArgs e){base.OnResize(e);LayoutCard();}
        void LayoutCard(){if(title==null)return;int p=S(20),w=Math.Max(0,Width-2*p);title.SetBounds(p,p,w,S(32));version.SetBounds(p,p+S(37),w,S(24));status.SetBounds(p,p+S(78),w,S(28));detail.SetBounds(p,p+S(110),w,S(32));Progress.SetBounds(p,p+S(149),w,S(5));Action.SetBounds(p,p+S(168),Math.Min(w,S(238)),S(42));Cancel.SetBounds(Action.Right+S(12),Action.Top,S(100),S(42));Startup.SetBounds(p,p+S(222),w,S(34));AutoInstall.SetBounds(p,p+S(264),w,S(34));}
    }
    public class SurfacePanelBase : ResponsiveControl {
        public SurfacePanelBase(){DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(Width<2 || Height<2)return;using(var path=Theme.Round(new Rectangle(0,0,Width-1,Height-1),10))using(var pen=new Pen(Theme.Border)){e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;e.Graphics.DrawPath(pen,path);}}
    }
}
