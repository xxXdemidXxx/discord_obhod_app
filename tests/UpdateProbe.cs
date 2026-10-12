using System;
using System.IO;
using System.Windows.Forms;
namespace ZapretDesktop {
    static class UpdateProbe {
        [STAThread] static void Main(string[] args) {
#if TESTING
            string fixture=Environment.GetEnvironmentVariable("BYPASS_LEGACY_TEST_FEED");
            if(fixture!=null)ShellUpdates.LegacyProofForTest=url=>System.Threading.Tasks.Task.FromResult(File.ReadAllText(Path.Combine(fixture,Path.GetFileName(new Uri(url).AbsolutePath))));
#endif
            if(args.Length>=3 && args[0]=="--apply-update")ShellUpdates.Apply(args);
            else File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"updated.txt"),"Restarted "+ShellUpdates.Current+" "+String.Join(" ",args));
        }
    }
}
