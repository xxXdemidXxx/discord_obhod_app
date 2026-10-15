using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretDesktop {
    internal sealed class DnsDiagnosticDialog : Form {
        internal readonly Button OpenYouTube=Theme.Button("Открыть YouTube в отдельном окне",true);
        internal readonly Label Result=Theme.Label("",9);
        internal readonly TextBox Report;
        internal DnsDiagnosticDialog(string report,Func<Task> launch,Action<string> helpRequested) {
            Func<int,int> s=n=>(int)Math.Round(n*Theme.SystemScale);
            Text="DNS и YouTube";BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.None;
            StartPosition=FormStartPosition.CenterParent;Size=new Size(s(780),s(690));MinimumSize=new Size(s(600),s(480));Padding=new Padding(s(18));
            var editor=new DarkEditor{Dock=DockStyle.Fill};Report=editor.Editor;Report.Text=report;Report.ReadOnly=true;
            var actions=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=s(210),ColumnCount=1,RowCount=4,Padding=Padding.Empty};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{66,44,46,54})actions.RowStyles.Add(new RowStyle(SizeType.Absolute,s(height)));
            var help=Theme.Label("Только это окно использует DNS через HTTPS и отдельный профиль Edge/Chrome.\nНастройки Windows и основного браузера не меняются.\nДля этого окна ByPass должен работать; стратегию обхода оставьте включённой.",9);help.ForeColor=Theme.Muted;help.Dock=DockStyle.Fill;
            OpenYouTube.Dock=DockStyle.Fill;OpenYouTube.Margin=new Padding(0,s(3),0,s(3));
            var links=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=Padding.Empty};
            Button browser=Theme.Button("Помощь: DNS в браузере"),windows=Theme.Button("Помощь: DNS в Windows");browser.Name="DnsHelpBrowser";windows.Name="DnsHelpWindows";browser.Size=new Size(s(245),s(36));windows.Size=new Size(s(245),s(36));
            browser.Click+=(sender,e)=>{helpRequested("browser");Close();};windows.Click+=(sender,e)=>{helpRequested("windows");Close();};links.Controls.AddRange(new Control[]{browser,windows});
            links.WrapContents=false;links.Resize+=(sender,e)=>{int width=Math.Max(1,(links.ClientSize.Width-s(16))/2);browser.Width=windows.Width=width;};
            Result.Dock=DockStyle.Fill;Result.ForeColor=Theme.Muted;
            OpenYouTube.Click+=async(sender,e)=>{
                OpenYouTube.Enabled=false;Result.ForeColor=Theme.Muted;Result.Text="Проверяем DNS через HTTPS и открываем отдельное окно…";
                try{await launch();if(!IsDisposed){Result.ForeColor=Theme.Success;Result.Text="Окно открыто. Проверьте воспроизведение ролика.\nDNS Windows и основной профиль браузера не изменены.";}}
                catch(Exception ex){if(!IsDisposed){Result.ForeColor=Theme.Warning;Result.Text="Не удалось открыть окно. Настройки Windows не изменены.\n"+ex.GetBaseException().Message;}}
                finally{if(!IsDisposed)OpenYouTube.Enabled=true;}
            };
            actions.Controls.Add(help,0,0);actions.Controls.Add(OpenYouTube,0,1);actions.Controls.Add(links,0,2);actions.Controls.Add(Result,0,3);Controls.Add(editor);Controls.Add(actions);
            Shown+=(sender,e)=>{Report.Select(0,0);OpenYouTube.Focus();};
            Load+=(sender,e)=>{var area=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-s(32)),Math.Min(MinimumSize.Height,area.Height-s(32)));Size=new Size(Math.Min(Width,area.Width-s(32)),Math.Min(Height,area.Height-s(32)));};
        }
    }
}
