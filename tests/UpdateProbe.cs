using System;
using System.IO;
using System.Windows.Forms;
namespace ZapretDesktop {
    static class UpdateProbe {
        [STAThread] static void Main(string[] args) {
            if(args.Length>=3 && args[0]=="--apply-update")ShellUpdates.Apply(args);
            else File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"updated.txt"),"Restarted "+ShellUpdates.Current);
        }
    }
}
