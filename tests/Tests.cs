using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretDesktop {
    static class Tests {
        static int count;
        [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string command, out int argc);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception(message); }
        public static void Copy(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
        [STAThread] static int Main(string[] args) {
            if (args.Contains("--stub") || Path.GetFileName(Application.ExecutablePath).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Test stub started"); Thread.Sleep(30000); return 0; }
            try {
                var root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                var local = Path.GetFullPath(Path.Combine(root, "..", "zapret-discord-youtube-1.10.2"));
                var official = Path.Combine(root, "downloads", "official-1.10.3", "zapret-discord-youtube-1.10.3");
                if(!Directory.Exists(local))local=official; SelectionTests().GetAwaiter().GetResult();
                foreach (var engine in new[] {local, official}.Distinct()) {
                    Strategy.ValidateEngine(engine);
                    var profiles = Directory.GetFiles(engine, "general*.bat"); Assert(profiles.Length == 22, "22 upstream profiles expected");
                    foreach (var profile in profiles) {
                        var parameters = Strategy.Arguments(engine, Path.GetFileName(profile));
                        Assert(parameters.StartsWith("--wf-tcp="), "wf-tcp first");
                        Assert(!parameters.Contains("%") && !parameters.Contains("^"), "Variables expanded");
                        Assert(parameters.Contains("--filter-udp="), "UDP flags retained");
                        if (File.ReadAllText(profile).Contains("^!")) Assert(parameters.Contains("--dpi-desync-fake-tls=!"), "Literal exclamation retained");
                    }
                }
                foreach (var arg in new[] {"", "--file=C:\\some folder\\", "--file=C:\\folder\\quote\".txt", "plain", "!"}) {
                    int argc; var memory = CommandLineToArgvW("app.exe " + Strategy.Quote(arg), out argc);
                    try { Assert(argc == 2 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, IntPtr.Size)) == arg, "Windows argument round-trip: " + arg); }
                    finally { LocalFree(memory); }
                }
                Assert(!Strategy.ValidPorts("0,65536") && !Strategy.ValidPorts("200-100") && Strategy.ValidPorts("1024-1934,1936-65535"), "Port range validation");
                Assert(!Strategy.NeedsTcpTimestamps(Strategy.Arguments(official, "general.bat")), "General does not change TCP timestamp setting");
                Assert(Strategy.NeedsTcpTimestamps(Strategy.Arguments(official, "general (ALT).bat")), "Timestamp strategies request upstream prerequisite");
                var temp = Path.Combine(root, "tests", "fixture-" + Guid.NewGuid().ToString("N"));
                try {
                    var store = new Store(temp); Copy(local, store.Current); var old = store.Current;
                    Reject(() => store.EnginePath("../outside"), "Version path traversal rejected");
                    var game = Path.Combine(old, "utils", "game_filter.enabled");
                    File.WriteAllText(game, "mode=tcp\r\ntcp=1024-1934,1936-65535\r\nudp=1024-65535\r\n");
                    string tcp, udp; Strategy.GamePorts(old, out tcp, out udp); Assert(tcp == "1024-1934,1936-65535" && udp == "12", "1.10.3 custom ports");
                    File.WriteAllText(game, "udp\n"); Strategy.GamePorts(old, out tcp, out udp); Assert(tcp == "12" && udp == "1024-65535", "Legacy game ports");
                    File.Delete(game); Strategy.GamePorts(old, out tcp, out udp); Assert(tcp == "12" && udp == "12", "Disabled game ports");
                    File.WriteAllText(Path.Combine(old, "lists", "list-general-user.txt"), "my-service.example\n");
                    File.WriteAllText(Path.Combine(old, "lists", "ipset-all.txt"), "203.0.113.5/32\n");
                    var imported = store.EnginePath("1.10.3"); Copy(official, imported);
                    Updater.Preserve(old, imported); Strategy.ValidateEngine(imported);
                    Assert(File.ReadAllText(Path.Combine(imported, "lists", "list-general-user.txt")) == "my-service.example\n", "User domains preserved");
                    Assert(File.ReadAllText(Path.Combine(imported, "lists", "ipset-all.txt")) == "203.0.113.5/32\n", "Custom IPSet preserved");
                    Assert(!File.Exists(Path.Combine(imported, "utils", "game_filter.enabled")), "Disabled GameFilter preserved");
                    store.Settings.PreviousEngine = store.Settings.Engine; store.Settings.Engine = "1.10.3"; store.Save();
                    using (var updater = new Updater(store)) updater.Rollback();
                    Assert(Updater.Version(store.Current) == Updater.Version(old), "Rollback activates previous version");
                    Assert(new Store(temp).Settings.Engine == store.Settings.Engine, "Atomic settings persisted");
                    var zip = Path.Combine(temp, "bad.zip");
                    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) writer.Write("bad");
                    Reject(() => Updater.Extract(zip, Path.Combine(temp, "extracted")), "ZIP traversal rejected");
                    Assert(!File.Exists(Path.Combine(temp, "outside.txt")), "No file escapes staging");
                    // Exercise process ownership and hidden startup using a harmless stub, never the real driver.
                    var fake = store.EnginePath("test-process"); Copy(local, fake); File.Copy(Application.ExecutablePath, Path.Combine(fake, "bin", "winws.exe"), true);
                    using (var runner = new EngineRunner()) {
                        if (!runner.Conflict) { runner.Start(fake, "general.bat"); Assert(runner.Running, "Owned process started"); runner.Stop(); Assert(!runner.Running, "Owned process stopped"); }
                        else {
                            var before = Process.GetProcessesByName("winws").Select(p => p.Id).OrderBy(x => x).ToArray();
                            bool rejected = false; try {runner.Start(fake, "general.bat");} catch (InvalidOperationException) {rejected = true;}
                            runner.Stop();
                            Assert(rejected && !runner.Running, "External process conflict rejected");
                            Assert(before.SequenceEqual(Process.GetProcessesByName("winws").Select(p => p.Id).OrderBy(x => x)), "External winws processes untouched");
                            Console.WriteLine("External process ownership checks passed; did not launch driver.");
                        }
                    }
                    using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--stub") {UseShellExecute=false, CreateNoWindow=true})) {
                        using (var job = new OwnedJob(child)) Assert(!child.HasExited, "Owned job holds stub process");
                        Assert(child.WaitForExit(5000), "Closing job terminates only owned child");
                    }
                    if (args.Contains("--network")) Network(store).GetAwaiter().GetResult();
                    store.Settings.CheckUpdates = false; store.Settings.AutoAppUpdates=false; store.Save();
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm(store, false)) {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2500, -2500); form.Show(); Application.DoEvents(); form.PerformLayout();
                        if (args.Contains("--network")) {
                            var pages = FindTabs(form); pages.SelectedIndex = 0;
                            var button = AllControls(form).OfType<Button>().First(x => x.Text == "Проверить сервисы");
                            button.PerformClick(); var watch = Stopwatch.StartNew();
                            while (!button.Enabled && watch.ElapsedMilliseconds < 18000) {Application.DoEvents(); Thread.Sleep(20);}
                            Assert(button.Enabled && AllControls(form).OfType<Label>().Any(x => x.Text.StartsWith("Успешных ответов:")), "Parallel service checks render results in desktop window");
                            pages.SelectedIndex = 0;
                        }
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(root, "tests", "window.png")); }
                        Assert(form.Controls.Count > 0, "Desktop UI constructed and rendered");
                        var tabs = FindTabs(form);
                        for (int i = 1; i < tabs.TabPages.Count; i++) {
                            tabs.SelectedIndex = i; form.PerformLayout(); Application.DoEvents();
                            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(root, "tests", "window-" + i + ".png")); }
                        }
                    }
                } finally { Directory.Delete(temp, true); }
                Console.WriteLine("PASS: " + count + " checks"); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        static async Task SelectionTests() {
            string running=null; int checks=0; var started=new System.Collections.Generic.List<string>();
            Func<int,long,CheckResult[]> results=(passed,ms)=>Enumerable.Range(0,5).Select(i=>new CheckResult{State=i<passed?1:0,Detail="fixture",Milliseconds=ms}).ToArray();
            var selected=await StrategySelector.Run(new[]{"failed","slow","fast"},
                (name,token)=>{running=name;started.Add(name);return Task.FromResult(0);},()=>running=null,
                token=>{checks++;return Task.FromResult(results(running=="failed"?3:5,running=="fast"?10:100));},CancellationToken.None,null,null);
            Assert(selected.Candidates.Count==3 && checks==4,"All candidates tested, winner retested");
            Assert(selected.Winner.Strategy=="fast" && selected.Winner.Confirmed && running=="fast","Fast confirmed winner stays running");
            running=null;int firstCalls=0;
            selected=await StrategySelector.Run(new[]{"first","second"},
                (name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(running=="first" && ++firstCalls>1?0:5,running=="first"?5:10)),CancellationToken.None,null,null);
            Assert(selected.Winner.Strategy=="second" && selected.Candidates[0].Error.Length>0,"Failed confirmation advances to next complete strategy");
            using(var cancel=new CancellationTokenSource()) {
                bool cancelled=false;
                try {await StrategySelector.Run(new[]{"a","b"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                    token=>{cancel.Cancel();token.ThrowIfCancellationRequested();return Task.FromResult(results(5,10));},cancel.Token,null,null);}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled && running==null,"Cancellation stops owned candidate");
            }
            selected=await StrategySelector.Run(new[]{"broken","partial"},(name,token)=>{if(name=="broken")throw new IOException("fixture error");running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(4,10)),CancellationToken.None,null,null);
            Assert(selected.Winner==null && selected.Candidates.Count==2 && running==null,"Start errors do not interrupt full scan and partial candidates are not auto-selected");
            var dns=ServiceChecks.FromException(new System.Net.Http.HttpRequestException("outer",new System.Net.WebException("DNS",System.Net.WebExceptionStatus.NameResolutionFailure)),42);
            Assert(dns.Failure=="dns" && dns.Milliseconds==42,"Typed nested DNS error classified");
            Assert(ServiceChecks.FromException(new System.Net.Http.HttpRequestException("dns text only"),0).Failure=="connection","Error message alone does not imply DNS failure");
            Assert(ServiceChecks.FromException(new System.Security.Authentication.AuthenticationException("fixture"),0).Failure=="tls","TLS failure remains distinct");
            string next="99.0.0";
            string json="{\"tag_name\":\"v"+next+"\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Obhod-"+next+"-win-x64.zip\",\"browser_download_url\":\"https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+next+"/Obhod-"+next+"-win-x64.zip\",\"digest\":\"sha256:"+new string('a',64)+"\",\"size\":100}]}";
            Assert(ShellUpdates.Parse(json).Version==next,"Stable newer shell release accepted");
            Reject(()=>ShellUpdates.Parse(json.Replace("sha256:","sha512:")),"Missing or wrong digest prevents shell update");
            Reject(()=>ShellUpdates.Parse(json.Replace("https://github.com/","https://example.com/")),"Foreign update URL rejected");
            Assert(ShellUpdates.Parse(json.Replace("\"prerelease\":false","\"prerelease\":true"))==null,"Prerelease excluded from automatic updates");
            Assert(ShellUpdates.Parse(json.Replace(next,ShellUpdates.Current))==null,"Current version does not update itself");
        }

        static DarkTabs FindTabs(Control root) {
            if (root is DarkTabs) return (DarkTabs)root;
            foreach (Control child in root.Controls) { var found = FindTabs(child); if (found != null) return found; }
            return null;
        }
        static System.Collections.Generic.IEnumerable<Control> AllControls(Control root) {
            yield return root;
            foreach (Control child in root.Controls) foreach (var item in AllControls(child)) yield return item;
        }
        static async Task Network(Store store) {
            using (var updater = new Updater(store)) {
                var release = await updater.Check(); Assert(release.Version == "1.10.3", "Latest official release fetched");
                await updater.Install(release, Console.WriteLine);
                Assert(Updater.Version(store.Current) == release.Version, "Verified official ZIP installed");
                Assert(File.ReadAllText(Path.Combine(store.Current, "lists", "list-general-user.txt")) == "my-service.example\n", "User list survives real update");
                updater.Rollback(); Assert(Updater.Version(store.Current) == "1.10.2", "Rollback after real update");
            }
            foreach (var target in ServiceChecks.Targets) {
                var result = await ServiceChecks.Run(target, CancellationToken.None);
                Console.WriteLine(target.Name + ": " + result.Detail + " (" + result.Milliseconds + " ms)");
                Assert(result.Detail.Length > 0, "HTTP check produces an actionable result");
            }
        }
    }
}
