using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZapretDesktop {
    // Quiet notices stay inside the main window; dialogs appear only on click.
    public sealed class UpdateNotice : Panel {
        readonly Panel shellRow, coreRow;
        readonly Label shellText, coreText;
        readonly Button shellAction, coreAction;
        string shellVersion, coreVersion, dismissedShell, dismissedCore;
        public event Action ShellRequested, CoreRequested;
        int S(int value) { return (int)Math.Round(value*Theme.SystemScale); }
        public UpdateNotice() {
            BackColor=Theme.Surface;Dock=DockStyle.Top;Visible=false;Height=0;
            shellRow=Row(out shellText,out shellAction);coreRow=Row(out coreText,out coreAction);
            Controls.Add(coreRow);Controls.Add(shellRow);
            shellAction.Click+=(s,e)=>{if(ShellRequested!=null)ShellRequested();};
            coreAction.Click+=(s,e)=>{if(CoreRequested!=null)CoreRequested();};
            Dismiss(shellRow,()=>{dismissedShell=shellVersion;RefreshRows();});
            Dismiss(coreRow,()=>{dismissedCore=coreVersion;RefreshRows();});
        }
        Panel Row(out Label text,out Button action) {
            var row=new Panel{Dock=DockStyle.Top,Height=S(46),BackColor=Theme.Surface};
            var label=Theme.Label("",10);var button=Theme.Button("Обновить");
            row.Controls.Add(label);row.Controls.Add(button);
            row.Resize+=(s,e)=>{label.SetBounds(S(24),0,Math.Max(0,row.Width-S(210)),S(46));label.TextAlign=ContentAlignment.MiddleLeft;button.SetBounds(row.Width-S(176),S(7),S(120),S(32));};
            text=label;action=button;return row;
        }
        void Dismiss(Panel row,Action dismiss) {
            var button=Theme.Button("×");button.BackColor=Theme.Surface;button.AccessibleName="Скрыть уведомление об обновлении";row.Controls.Add(button);
            button.Click+=(s,e)=>dismiss();row.Resize+=(s,e)=>button.SetBounds(row.Width-S(48),S(7),S(40),S(32));
        }
        public void Render(string shell,string core,bool shellEnabled,bool coreEnabled,bool stopCoreFirst) {
            shellVersion=shell;coreVersion=core;
            shellText.Text="Доступна новая версия оболочки: "+shell;
            coreText.Text="Доступна новая версия ядра: "+core;
            shellAction.Enabled=shellEnabled;coreAction.Enabled=coreEnabled;RefreshRows();
        }
        void RefreshRows() {
            bool shell=!String.IsNullOrEmpty(shellVersion) && shellVersion!=dismissedShell;
            bool core=!String.IsNullOrEmpty(coreVersion) && coreVersion!=dismissedCore;
            shellRow.Visible=shell;coreRow.Visible=core;
            Height=S(46)*((shell?1:0)+(core?1:0));Visible=shell || core;
        }
    }
}
