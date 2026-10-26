using System;
using System.Drawing;
using System.Linq;
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
        public readonly DarkSelect Versions=new DarkSelect{Name="ShellVersions"};
        public readonly Button InstallVersion=Theme.Button("Установить выбранную",true),ReloadVersions=Theme.Button("Обновить список");
        readonly Label versionsTitle=Theme.Label("Версия для установки",10,true),versionsHelp=Theme.Label("Нажмите список, чтобы загрузить выпуски.",9);
        bool showVersionInstall;
        public AppUpdateCard(){BackColor=Theme.Surface;Height=S(322);version.ForeColor=detail.ForeColor=versionsHelp.ForeColor=Theme.Muted;Versions.Format=x=>Convert.ToString(x)+(Convert.ToString(x)==ShellUpdates.Current?" · текущая":"");InstallVersion.Name="InstallShellVersion";ReloadVersions.Name="ReloadShellVersions";Controls.AddRange(new Control[]{title,version,status,detail,Action,Cancel,Startup,AutoInstall,Progress,versionsTitle,Versions,InstallVersion,ReloadVersions,versionsHelp});Cancel.Visible=Progress.Visible=false;}
        public void Render(AppUpdateSession session,string blocked=null){
            Action.Text=session.ActionText;status.Text=session.Message;detail.Text=blocked??session.Detail;
            status.ForeColor=session.State==AppUpdateState.Failed?Theme.Warning:session.State==AppUpdateState.Current?Theme.Success:Theme.Text;
            Cancel.Visible=Progress.Visible=session.State==AppUpdateState.Downloading;Cancel.Enabled=Cancel.Visible;Progress.Percent=session.Percent;
            string selected=Versions.SelectedItem as string;
            if(!Versions.Items.Cast<string>().SequenceEqual(session.Versions)){Versions.Items.Clear();foreach(string item in session.Versions)Versions.Items.Add(item);Versions.SelectedItem=selected??ShellUpdates.Current;if(Versions.SelectedIndex<0 && Versions.Items.Count>0)Versions.SelectedIndex=0;}
            versionsHelp.Text=session.VersionsLoading?"Проверяем доступные установочные пакеты…":session.VersionsError.Length>0?session.VersionsError:session.Versions.Length>0?"Только проверенные пакеты. Меняется оболочка.\nЯдро и его версия сохраняются.":"Нажмите список, чтобы загрузить выпуски.";
            Versions.EmptyText=session.VersionsLoading?"Проверяем выпуски…":session.VersionsError.Length>0?"Нет доступных версий":"Загрузить список версий";
            ReloadVersions.Enabled=Versions.Enabled=!session.Working && !session.VersionsLoading;showVersionInstall=Versions.SelectedItem!=null && !session.VersionsLoading;InstallVersion.Visible=showVersionInstall;InstallVersion.Enabled=Versions.Enabled && showVersionInstall && blocked==null;LayoutCard();
        }
        protected override void OnResize(EventArgs e){base.OnResize(e);LayoutCard();}
        void LayoutCard(){if(title==null)return;int p=S(20),w=Math.Max(0,Width-2*p),right=Math.Min(S(330),(int)(w*.40)),left=w-right-S(24),x=p+left+S(24);title.SetBounds(p,p,left,S(32));version.SetBounds(p,p+S(37),left,S(24));status.SetBounds(p,p+S(78),left,S(48));detail.SetBounds(p,p+S(130),left,S(26));Progress.SetBounds(p,p+S(157),left,S(5));Action.SetBounds(p,p+S(168),Math.Min(left,S(238)),S(42));Cancel.SetBounds(Action.Right+S(12),Action.Top,Math.Max(0,Math.Min(S(100),x-Action.Right-S(24))),S(42));Startup.SetBounds(p,p+S(222),w,S(34));AutoInstall.SetBounds(p,p+S(264),w,S(34));versionsTitle.SetBounds(x,p,right,S(28));Versions.SetBounds(x,p+S(39),right,S(38));InstallVersion.SetBounds(x,p+S(89),right,S(40));int reloadY=showVersionInstall?139:89;ReloadVersions.SetBounds(x,p+S(reloadY),right,S(34));versionsHelp.SetBounds(x,p+S(reloadY+38),right,S(42));}
    }
    public class SurfacePanelBase : ResponsiveControl {
        public SurfacePanelBase(){DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(Width<2 || Height<2)return;using(var path=Theme.Round(new Rectangle(0,0,Width-1,Height-1),10))using(var pen=new Pen(Theme.Border)){e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;e.Graphics.DrawPath(pen,path);}}
    }
}
