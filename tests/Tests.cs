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
                if(!Directory.Exists(local))local=official; SelectionTests().GetAwaiter().GetResult(); DomainTests(root); UpdateFeedTests().GetAwaiter().GetResult(); ShellReplacementTest(root);
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
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2500, -2500); form.Show(); Application.DoEvents(); form.PerformLayout(); NativeFrameChecks(form,root);
                        if (args.Contains("--network")) {
                            var pages = FindTabs(form); pages.SelectedIndex = 0;
                            var button = AllControls(form).OfType<Button>().First(x => x.Text == "Проверить сервисы");
                            button.PerformClick(); var watch = Stopwatch.StartNew();
                            while (!button.Enabled && watch.ElapsedMilliseconds < 18000) {Application.DoEvents(); Thread.Sleep(20);}
                            Assert(button.Enabled && AllControls(form).OfType<Label>().Any(x => x.Text.StartsWith("Доступно:")), "Parallel service checks render results in desktop window");
                            pages.SelectedIndex = 0;
                        }
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(root, "tests", "window.png")); }
                        Assert(form.Controls.Count > 0, "Desktop UI constructed and rendered"); InteractiveUiChecks(form,root); ResponsiveScreens(form,root);
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
        [DllImport("user32.dll")]static extern bool PrintWindow(IntPtr window,IntPtr context,uint flags);
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        static void NativeFrameChecks(MainForm form,string root){
            Assert((NativeFrame.GetWindowLong(form.Handle,-16)&NativeFrame.Caption)==0,"Native caption absent after window creation");
            SendMessage(form.Handle,0x86,IntPtr.Zero,IntPtr.Zero);SendMessage(form.Handle,0x86,new IntPtr(1),IntPtr.Zero);
            form.WindowState=FormWindowState.Maximized;Application.DoEvents();form.WindowState=FormWindowState.Normal;Application.DoEvents();
            Assert(form.ClientSize==form.Size && (NativeFrame.GetWindowLong(form.Handle,-16)&NativeFrame.Caption)==0,"Window frame stays removed after activation and maximize/restore");
            using(var picture=new Bitmap(form.Width,form.Height)){var g=Graphics.FromImage(picture);var context=g.GetHdc();try{Assert(PrintWindow(form.Handle,context,0),"Entire native window captured including non-client area");}finally{g.ReleaseHdc(context);g.Dispose();}picture.Save(Path.Combine(root,"tests","window-native.png"));}
        }
        sealed class MockHttp : System.Net.Http.HttpMessageHandler {
            public int Calls;public readonly System.Collections.Generic.List<string> Urls=new System.Collections.Generic.List<string>();
            public Func<string,System.Net.Http.HttpResponseMessage> Reply;
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token){Calls++;Urls.Add(request.RequestUri.AbsoluteUri);return Task.FromResult(Reply(request.RequestUri.AbsoluteUri));}
        }
        static string Feed(string version){return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{tag_name="v"+version,draft=false,prerelease=false,assets=new[]{new{name="Obhod-"+version+"-win-x64.zip",browser_download_url="https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+version+"/Obhod-"+version+"-win-x64.zip",digest="sha256:"+new string('a',64),size=123}}});}
        static System.Net.Http.HttpResponseMessage Response(int status,string text){return new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status){Content=new System.Net.Http.StringContent(text)};}
        static async Task UpdateFeedTests(){
            var http=new MockHttp{Reply=url=>Response(200,Feed("99.0.0"))};
            using(var updates=new ShellUpdates(http)){var release=await updates.Check();Assert(release.Version=="99.0.0" && http.Calls==1 && http.Urls[0].EndsWith("/releases/latest/download/update.json"),"New shell release discovered without public API calls");}
            http=new MockHttp{Reply=url=>Response(url.Contains("/download/")?404:200,Feed(ShellUpdates.Current))};
            using(var updates=new ShellUpdates(http)){Assert(await updates.Check()==null && http.Calls==2 && http.Urls[1].StartsWith("https://api.github.com/"),"Legacy releases without a feed fall back to API once");}
            http=new MockHttp{Reply=url=>Response(200,Feed("99.0.0").Replace("sha256:","sha512:"))};
            using(var updates=new ShellUpdates(http)){bool rejected=false;try{await updates.Check();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Feed without a valid hash cannot install an update");}
            http=new MockHttp{Reply=url=>{var response=Response(403,"API rate limit exceeded");response.Headers.Add("X-RateLimit-Remaining","0");response.Headers.Add("X-RateLimit-Reset",((long)(DateTimeOffset.UtcNow.AddHours(1)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds).ToString());return response;}};
            using(var client=new System.Net.Http.HttpClient(http)){for(int i=0;i<2;i++){bool friendly=false;try{await GitHubMetadata.Get(client,"https://api.github.com/repos/test-fixture/backoff/releases/latest");}catch(UpdateCheckException ex){friendly=ex.Message.Contains("Попробуйте позже") && !ex.Message.Contains("403");}Assert(friendly,"Rate limit gets a readable message");}Assert(http.Calls==1,"Repeated checks respect GitHub reset time and do not send another request");}
            http=new MockHttp{Reply=url=>Response(200,new string('a',1024*1024+1))};
            using(var client=new System.Net.Http.HttpClient(http)){bool rejected=false;try{await GitHubMetadata.Get(client,"https://github.com/test/oversized");}catch(InvalidDataException){rejected=true;}Assert(rejected,"Oversized release metadata rejected");}
            Assert(ServiceChecks.Targets.Last().Url.StartsWith("https://github.com/"),"Strategy probes check GitHub availability without consuming API quota");
        }
        static void InteractiveUiChecks(MainForm form,string root) {
            var tabs=FindTabs(form);tabs.SelectedIndex=0;var dashboard=AllControls(form).OfType<Dashboard>().Single();
            typeof(DarkSelect).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(dashboard.Profiles,new object[]{EventArgs.Empty});Application.DoEvents();
            var popup=(ToolStripDropDown)typeof(DarkSelect).GetField("popup",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(dashboard.Profiles);
            Assert(popup!=null && popup.Visible,"Custom strategy dropdown opens");
            using(var image=new Bitmap(popup.Width,popup.Height)){popup.DrawToBitmap(image,new Rectangle(Point.Empty,popup.Size));image.Save(Path.Combine(root,"tests","window-dropdown.png"));}
            dashboard.Profiles.Choose(1);Assert(dashboard.Profiles.SelectedIndex==1,"Custom strategy dropdown applies item selection");dashboard.Profiles.SelectedIndex=0;
            tabs.SelectedIndex=1;var editor=AllControls(form).OfType<DomainEditor>().Single();editor.TextEditor.Text=String.Join("\r\n",Enumerable.Range(1,100).Select(i=>"site-"+i+".example"));Application.DoEvents();
            Assert(editor.Dirty && AllControls(editor).OfType<SlimScroll>().Any(x=>x.Visible),"Editor tracks unsaved text and shows thin scrollbar for long lists");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(root,"tests","window-domain-scroll.png"));}
            editor.Discard();editor.TextEditor.Text="ui-save.example";var save=AllControls(editor).OfType<Button>().Single(x=>x.Text=="Сохранить");
            if(save.Enabled){save.PerformClick();Application.DoEvents();Assert(!editor.Dirty && editor.TextEditor.Text.Contains("ui-save.example"),"Domain editor saves through actual UI event");}else editor.Discard();
            if(save.Enabled || !editor.TextEditor.ReadOnly){
                var siteInput=AllControls(editor).OfType<TextBox>().Single(x=>x.Name=="site-input");siteInput.Text="https://added-via-ui.example/path";AllControls(editor).OfType<Button>().Single(x=>x.Text=="Добавить").PerformClick();
                Assert(editor.Dirty && editor.TextEditor.Text.Contains("added-via-ui.example"),"Pasted URL adds a site through the visible UI");editor.Discard();
                var engine=(string)typeof(DomainEditor).GetField("engine",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(editor);DomainLists.Write(engine,0,"");editor.Discard();using(var picture=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(picture,new Rectangle(Point.Empty,form.Size));picture.Save(Path.Combine(root,"tests","window-sites-empty.png"));}
            }
            tabs.SelectedIndex=0;
        }
        static void ResponsiveScreens(MainForm form,string root) {
            var tabs=FindTabs(form);tabs.SelectedIndex=0;var original=form.Size;
            foreach(var size in new[]{new Size(940,690),new Size(1400,960)}) {
                form.Size=size;form.PerformLayout();Application.DoEvents();var dashboard=AllControls(form).OfType<Dashboard>().Single();
                Assert(dashboard.History.Bottom<=dashboard.Height+1 && dashboard.Apply.Right<=dashboard.Width && dashboard.Dns.Right<=dashboard.Width,"Dashboard fits resized window "+size);
                using(var picture=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(picture,new Rectangle(Point.Empty,form.Size));picture.Save(Path.Combine(root,"tests","window-"+(size.Width<1000?"compact":"wide")+".png"));}
            }
            form.Size=original;tabs.SelectedIndex=2;
            AllControls(form).OfType<Button>().First(x=>x.Text.StartsWith("Настроить порты")).PerformClick();Application.DoEvents();
            using(var picture=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(picture,new Rectangle(Point.Empty,form.Size));picture.Save(Path.Combine(root,"tests","window-ports.png"));}
            tabs.SelectedIndex=0;
            // Windows CI limits native form sizes to its 1024x768 desktop.
            // Exercise DPI layout on an independent control canvas instead.
            using(var scaled=new Dashboard{Size=new Size(1000,600)}) {
                scaled.Scale(new SizeF(1.5f,1.5f));scaled.Size=new Size(1500,1100);scaled.PerformLayout();
                Assert(scaled.History.Bottom<=scaled.Height+1 && scaled.Categories.Right<=scaled.Width,"Dashboard layout fits independent 150 percent canvas");
                using(var picture=new Bitmap(scaled.Width,scaled.Height)){scaled.DrawToBitmap(picture,new Rectangle(Point.Empty,scaled.Size));picture.Save(Path.Combine(root,"tests","window-scale150.png"));}
            }
        }
        static void ShellReplacementTest(string root) {
            var folder=Path.Combine(root,"tests","fixture-update-"+Guid.NewGuid().ToString("N"));
            var stage=Path.Combine(folder,"data","app-updates","probe");Directory.CreateDirectory(stage);
            try {
                var source=Path.Combine(stage,"Obhod.exe");var target=Path.Combine(folder,"Obhod.exe");
                File.Copy(Path.Combine(root,"tests","Obhod-probe.exe"),source);
                File.WriteAllText(source+".config","<?xml version=\"1.0\"?><configuration><startup><supportedRuntime version=\"v4.0\"/></startup></configuration>");
                File.WriteAllText(Path.Combine(stage,"verified.sha256"),Updater.Hash(source));File.WriteAllText(Path.Combine(stage,"config.sha256"),Updater.Hash(source+".config"));
                File.WriteAllText(Path.Combine(stage,"version.txt"),ShellUpdates.Current);
                File.WriteAllText(target,"previous executable");File.WriteAllText(target+".config","previous config");
                string settings=Path.Combine(folder,"data","settings.json");File.WriteAllText(settings,"preserved settings");
                using(var helper=Process.Start(new ProcessStartInfo(source,"--apply-update 2147483647 "+Strategy.Quote(target)){UseShellExecute=false,CreateNoWindow=true})) {
                    Assert(helper.WaitForExit(10000),"Shell update helper exits after replacing and restarting");
                }
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(folder,"updated.txt")) && watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
                Assert(File.Exists(Path.Combine(folder,"updated.txt")),"Updated shell is restarted");
                Assert(Updater.Hash(source)==Updater.Hash(target),"Exact verified executable installed");
                Assert(File.ReadAllText(target+".previous")=="previous executable" && File.ReadAllText(target+".config.previous")=="previous config","Old executable and configuration backed up");
                Assert(File.ReadAllText(settings)=="preserved settings","Shell update does not overwrite user settings");
            } finally {Directory.Delete(folder,true);}
        }
        static void DomainTests(string root) {
            string normalized=DomainLists.Normalize("Example.COM\nexample.com\nпример.рф\n# comment\n",0);
            Assert(normalized.StartsWith("example.com\r\nxn--e1afmkfd.xn--p1ai") && normalized.Split(new[]{"example.com"},StringSplitOptions.None).Length==2,"Domains normalized, IDN supported, duplicates removed");
            Reject(()=>DomainLists.Normalize("https://youtube.com/watch",0),"URLs cannot be saved as domain entries");
            Reject(()=>DomainLists.Normalize("192.168.1.1",0),"IP addresses use their own list");
            Assert(DomainLists.Normalize("192.168.1.0/24\n2001:db8::/32",2).Contains("2001:db8::/32"),"IPv4 and IPv6 ranges supported");
            Reject(()=>DomainLists.Normalize("192.168.1.0/64",2),"Invalid IPv4 prefix rejected");
            Reject(()=>DomainLists.Normalize("1",2),"IPv4 shorthand rejected");
            Assert(DomainLists.FromInput("https://пример.рф/path",0)=="xn--e1afmkfd.xn--p1ai","Pasted website URL becomes a normalized domain");
            var folder=Path.Combine(root,"tests","fixture-domains-"+Guid.NewGuid().ToString("N"));
            try{DomainLists.Write(folder,0,"first.example");string path=DomainLists.FilePath(folder,0);Reject(()=>DomainLists.Write(folder,0,"invalid url"),"Invalid list cannot overwrite saved entries");Assert(File.ReadAllText(path).Contains("first.example"),"Original list preserved on invalid input");DomainLists.Write(folder,0,"second.example");Assert(File.ReadAllText(path+".bak").Contains("first.example"),"Atomic domain replacement preserves backup");DomainLists.Write(folder,0,"");Assert(DomainLists.Read(folder,0)=="" && new FileInfo(path).Length>0,"Empty user lists remain compatible with the engine and show no placeholder");File.WriteAllText(path,"# Never leave this file empty\r\ndomain.example.abc\r\n");Assert(DomainLists.Read(folder,0)=="","Upstream technical comment is hidden from the editor");DomainLists.Write(folder,2,"");Assert(DomainLists.Read(folder,2)=="" && new FileInfo(DomainLists.FilePath(folder,2)).Length>0,"Empty IP exclusions retain the engine sentinel");}finally{Directory.Delete(folder,true);}
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
            int quickChecks=0,confirmations=0;running=null;
            selected=await StrategySelector.Run(new[]{"partial","working","unneeded"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>{quickChecks++;return Task.FromResult(results(running=="partial"?3:5,10));},CancellationToken.None,null,null,true,
                token=>{confirmations++;return Task.FromResult(results(5,20));});
            Assert(selected.Winner.Strategy=="working" && selected.Candidates.Count==2 && quickChecks==2 && confirmations==1,"Quick mode stops after first full result and separate confirmation");
            running=null;int confirmCalls=0;
            selected=await StrategySelector.Run(new[]{"flaky","good"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(5,10)),CancellationToken.None,null,null,true,
                token=>{confirmCalls++;return Task.FromResult(results(running=="flaky"?4:5,20));});
            Assert(selected.Winner.Strategy=="good" && confirmCalls==2,"Quick mode continues after failed confirmation");
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
        static void TestApiCredential(System.Net.Http.HttpClient client){
            var token=Environment.GetEnvironmentVariable("OBHOD_TEST_GITHUB_TOKEN");
            if(!String.IsNullOrEmpty(token))client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",token);
        }
        static async Task Network(Store store) {
            using (var updater = new Updater(store)) {
                var apiClient=(System.Net.Http.HttpClient)typeof(Updater).GetField("client",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(updater);
                TestApiCredential(apiClient);Release release;
                try{release=await updater.Check();}finally{apiClient.DefaultRequestHeaders.Authorization=null;}
                Assert(release.Version == "1.10.3", "Latest official release fetched");
                await updater.Install(release, Console.WriteLine);
                Assert(Updater.Version(store.Current) == release.Version, "Verified official ZIP installed");
                Assert(File.ReadAllText(Path.Combine(store.Current, "lists", "list-general-user.txt")) == "my-service.example\n", "User list survives real update");
                updater.Rollback(); Assert(Updater.Version(store.Current) == "1.10.2", "Rollback after real update");
            }
            using(var shell=new ShellUpdates()) {
                Assert(await shell.Check()==null,"Installed shell version matches latest published release");
                using(var client=new System.Net.Http.HttpClient()) {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Obhod-Test/"+ShellUpdates.Current);TestApiCredential(client);
                    var json=await client.GetStringAsync("https://api.github.com/repos/"+ShellUpdates.Repository+"/releases/latest");
                    var data=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(json);
                    var asset=((System.Collections.ArrayList)data["assets"]).Cast<System.Collections.Generic.Dictionary<string,object>>().FirstOrDefault(x=>Convert.ToString(x["name"])=="Obhod-"+ShellUpdates.Current+"-win-x64.zip");
                    if(asset!=null) {var release=new Release{Version=ShellUpdates.Current,Url=Convert.ToString(asset["browser_download_url"]),Size=Convert.ToInt64(asset["size"]),Digest=Convert.ToString(asset["digest"])};
                    var settings=Path.Combine(store.Root,"data","settings.json");var before=Updater.Hash(settings);
                    var prepared=await shell.Stage(release,store.Root);
                    Assert(File.Exists(prepared) && File.Exists(prepared+".config"),"Published shell ZIP downloaded, hashed and staged");
                    ShellUpdates.ValidateBinary(prepared,ShellUpdates.Current);count++;
                    Assert(Updater.Hash(settings)==before,"Real shell download preserves existing settings");}else Console.WriteLine("Current shell release not published yet; staging probe runs after publication.");
                }
            }
            foreach (var target in ServiceChecks.Targets) {
                var result = await ServiceChecks.Run(target, CancellationToken.None);
                Console.WriteLine(target.Name + ": " + result.Detail + " (" + result.Milliseconds + " ms)");
                Assert(result.Detail.Length > 0, "HTTP check produces an actionable result");
            }
        }
    }
}
