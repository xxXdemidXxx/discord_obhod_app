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
        static string Sign(string payload) {
            using(var key=new System.Security.Cryptography.RSACryptoServiceProvider()) {
                key.PersistKeyInCsp=false;key.FromXmlString(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture-signing","key.private.xml")));
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(payload);
                return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{signed_payload=Convert.ToBase64String(bytes),signature=Convert.ToBase64String(key.SignData(bytes,System.Security.Cryptography.CryptoConfig.MapNameToOID("SHA256")))});
            }
        }
        static string Feed(string version){return Sign(RawFeed(version));}
        static Release ParseFixture(string json) {
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();var data=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(json);
            data["schema"]=1;data["shell_files"]=new{exe=new string('a',64),config=new string('a',64)};
            return ShellUpdates.Parse(Sign(serializer.Serialize(data)));
        }
        static string Proof(string version,string exe,string config,string digest,long size) {
            return Sign(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{schema=1,tag_name="v"+version,draft=false,prerelease=false,shell_files=new{exe=Trust.Hash(exe),config=Trust.Hash(config)},assets=new[]{new{name="Obhod-"+version+"-win-x64.zip",browser_download_url="https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+version+"/Obhod-"+version+"-win-x64.zip",digest=digest,size=size}}}));
        }
        static string BuildProof(string exe,string config) {
            return Sign(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{schema=1,kind="ByPass-build",owner=OwnerIdentity.Owner,version=ShellUpdates.Current,shell_files=new{exe=Trust.Hash(exe),config=Trust.Hash(config)}}));
        }
        static void WriteProof(string source) {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(source),"release-proof.json"),Proof(ShellUpdates.Current,source,source+".config","sha256:"+new string('a',64),123));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(source),OwnerIdentity.ProofName),BuildProof(source,source+".config"));
        }
        static void SecurityTests(string root,string official) {
            var valid=Feed("99.0.0");Assert(ShellUpdates.Parse(valid).Version=="99.0.0","Offline signed release accepted");
            Reject(()=>ShellUpdates.Parse(RawFeed("99.0.0")),"Unsigned release rejected");
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();var envelope=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(valid);
            byte[] payload=Convert.FromBase64String((string)envelope["signed_payload"]);payload[0]^=1;envelope["signed_payload"]=Convert.ToBase64String(payload);
            Reject(()=>ShellUpdates.Parse(serializer.Serialize(envelope)),"Changed signed payload rejected");
            envelope=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(valid);envelope["tag_name"]="v900.0.0";
            Assert(ShellUpdates.Parse(serializer.Serialize(envelope)).Version=="99.0.0","Untrusted legacy outer fields cannot replace signed release data");
            using(var wrong=new System.Security.Cryptography.RSACryptoServiceProvider(2048)) {
                wrong.PersistKeyInCsp=false;payload=System.Text.Encoding.UTF8.GetBytes(RawFeed("99.0.0"));
                string forged=serializer.Serialize(new{signed_payload=Convert.ToBase64String(payload),signature=Convert.ToBase64String(wrong.SignData(payload,System.Security.Cryptography.CryptoConfig.MapNameToOID("SHA256")))});
                Reject(()=>ShellUpdates.Parse(forged),"Release signed by another key rejected");
            }
            Assert((string)Trust.VerifyEngine(official)["version"]=="1.10.3","Approved core catalogue matches upstream files");
            Reject(()=>Trust.Archive("1.10.3","sha256:"+new string('a',64)),"Compromised upstream digest rejected by offline catalogue");
            Reject(()=>Trust.Engine("99.0.0"),"Unreviewed future core rejected");
            var fixture=Path.Combine(root,"tests","fixture-trust-"+Guid.NewGuid().ToString("N"));Copy(official,fixture);
            try {
                File.WriteAllText(Path.Combine(fixture,"lists","list-general-user.txt"),"my.example\n");
                Assert(Trust.VerifyEngine(fixture)!=null,"User list remains editable without changing core trust");
                string binary=Path.Combine(fixture,"bin","winws.exe");File.AppendAllText(binary,"tamper");
                Reject(()=>Trust.VerifyEngine(fixture),"Modified native executable rejected before elevation");
                File.Copy(Path.Combine(official,"bin","winws.exe"),binary,true);
                File.AppendAllText(Path.Combine(fixture,"general.bat"),"\nmalicious");
                Reject(()=>Trust.VerifyEngine(fixture),"Modified strategy rejected before elevated argument parsing");
                File.Copy(Path.Combine(official,"general.bat"),Path.Combine(fixture,"general.bat"),true);
                File.WriteAllText(Path.Combine(fixture,"bin","injected.dll"),"fake");
                Reject(()=>Trust.VerifyEngine(fixture),"Extra DLL rejected even when approved hashes match");
            }finally{Directory.Delete(fixture,true);}
            using(var wire=new MemoryStream()) {
                HostWire.Write(wire,new{action="stop"});wire.Position=0;Assert((string)HostWire.Read(wire)["action"]=="stop","Framed local command round trip");
            }
            using(var wire=new MemoryStream(BitConverter.GetBytes(HostWire.Limit+1)))Reject(()=>HostWire.Read(wire),"Oversized IPC command rejected before allocation");
            using(var wire=new MemoryStream())Reject(()=>HostWire.Write(wire,new{text=new string('x',HostWire.Limit)}),"Oversized IPC reply rejected");
            string ownerConfig=Path.Combine(root,"tests","fixture-signing","test.config");File.WriteAllText(ownerConfig,"<configuration/>");
            string receipt=BuildProof(Application.ExecutablePath,ownerConfig);OwnerIdentity.Verify(receipt,Application.ExecutablePath,ownerConfig,ShellUpdates.Current);Assert(true,"Author identity receipt verifies exact executable and config");
            Reject(()=>OwnerIdentity.Verify(receipt,Application.ExecutablePath,ownerConfig,"99.0.0"),"Author receipt cannot be reused for another version");
            File.WriteAllText(ownerConfig,"changed");Reject(()=>OwnerIdentity.Verify(receipt,Application.ExecutablePath,ownerConfig,ShellUpdates.Current),"Author mark rejects modified build configuration");
            string name="ByPass-"+Guid.NewGuid().ToString("N");
            using(var server=new System.IO.Pipes.NamedPipeServerStream(name,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous))
            using(var client=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut)) {
                var pending=server.BeginWaitForConnection(null,null);client.Connect(2000);server.EndWaitForConnection(pending);
                uint peer;Assert(HostWire.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(),out peer) && peer==Process.GetCurrentProcess().Id,"Local transport identifies actual helper process");
                Assert(HostWire.GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(),out peer) && peer==Process.GetCurrentProcess().Id,"Local transport identifies actual GUI process");
                var send=Task.Run(()=>HostWire.Write(client,new{action="status"}));Assert((string)HostWire.Read(server)["action"]=="status","Real local pipe delivers bounded commands");Assert(send.Wait(2000),"Pipe writer completes with concurrent receiver");
            }
            Assert(String.Equals(HostWire.Image(Process.GetCurrentProcess().Id),Application.ExecutablePath,StringComparison.OrdinalIgnoreCase),"Process path can be verified with limited query access");
            bool readAsCaller=false;HostWire.AsCaller(Process.GetCurrentProcess().Id,()=>{readAsCaller=File.Exists(Application.ExecutablePath);});
            Assert(readAsCaller,"Helper source reads can use caller token");
            SnapshotTests(root,official);
        }
        static void SnapshotTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-snapshot-"+Guid.NewGuid().ToString("N"));
            string source=Path.Combine(folder,"source"),snapshot=Path.Combine(folder,"runtime");Copy(official,source);Directory.CreateDirectory(snapshot);
            try {
                File.WriteAllText(Path.Combine(source,"utils","game_filter.enabled"),"all");
                File.WriteAllBytes(Path.Combine(source,"bin","ACTIVE_GAME_UDP.bin"),new byte[]{1,2,3});
                EngineHost.WriteSnapshot(snapshot,EngineHost.ReadInputs(source),false);
                string driver=Path.Combine(snapshot,"bin","WinDivert64.sys");DateTime written=File.GetLastWriteTimeUtc(driver);
                using(var mapped=new FileStream(driver,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    bool locked=false;try{File.Delete(driver);}catch(IOException){locked=true;}catch(UnauthorizedAccessException){locked=true;}
                    Assert(locked,"Fixture reproduces driver that cannot be deleted after process shutdown");
                    File.WriteAllText(Path.Combine(source,"lists","list-general-user.txt"),"new.example\n");
                    File.Delete(Path.Combine(source,"utils","game_filter.enabled"));File.Delete(Path.Combine(source,"bin","ACTIVE_GAME_UDP.bin"));
                    var inputs=EngineHost.ReadInputs(source);
                    foreach(string strategy in Directory.GetFiles(source,"general*.bat"))EngineHost.WriteSnapshot(snapshot,inputs,true);
                    Assert(File.GetLastWriteTimeUtc(driver)==written && Trust.VerifyEngine(snapshot)!=null,"All strategy transitions preserve locked driver and signed immutable core");
                    Assert(File.ReadAllText(Path.Combine(snapshot,"lists","list-general-user.txt"))=="new.example\n" && !File.Exists(Path.Combine(snapshot,"utils","game_filter.enabled")) && !File.Exists(Path.Combine(snapshot,"bin","ACTIVE_GAME_UDP.bin")),"Restart refreshes lists and removes disabled game data while driver remains locked");
                    inputs["bin/WinDivert64.sys"][0]^=1;
                    Reject(()=>EngineHost.WriteSnapshot(snapshot,inputs,true),"Changed immutable input cannot replace the locked trusted driver");
                }
                File.AppendAllText(Path.Combine(snapshot,"general.bat"),"tamper");
                Reject(()=>EngineHost.WriteSnapshot(snapshot,EngineHost.ReadInputs(source),true),"Reuse cannot silently repair or trust a modified protected strategy");
            }finally{RemoveFixture(folder);}
        }
        [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string command, out int argc);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        static void RemoveFixture(string folder) {
            string path=Path.GetFullPath(folder),prefix=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture-"));
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Cleanup path is not a test fixture.");
            // A restarted probe may still be unloading its image; Defender can
            // briefly retain a file handle too. Retry cleanup without hiding errors.
            for(int attempt=0;;attempt++) {
                try{if(Directory.Exists(path))Directory.Delete(path,true);return;}
                catch(IOException){if(attempt>=30)throw;}
                catch(UnauthorizedAccessException){if(attempt>=30)throw;}
                Thread.Sleep(100);
            }
        }
        static void LegacyMigrationTest(string root) {
            string folder=Path.Combine(root,"tests","fixture-legacy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try {
                string feed=Path.Combine(folder,"feed");Directory.CreateDirectory(feed);
                string exe=Path.Combine(root,"tests","Obhod-probe.exe"),config=Path.Combine(folder,"source.config"),archive=Path.Combine(folder,"source.zip");File.WriteAllText(config,"<configuration/>");
                using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){zip.CreateEntryFromFile(exe,"Obhod/Obhod.exe");zip.CreateEntryFromFile(config,"Obhod/Obhod.exe.config");}
                var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
                string proof=Proof(ShellUpdates.Current,exe,config,"sha256:"+Trust.Hash(archive),new FileInfo(archive).Length);
                var outer=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(proof);
                var payload=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)outer["signed_payload"])));
                foreach(var item in payload)outer[item.Key]=item.Value;
                File.WriteAllText(Path.Combine(feed,"update.json"),serializer.Serialize(outer));File.WriteAllText(Path.Combine(feed,OwnerIdentity.ProofName),BuildProof(exe,config));
                string target=Path.Combine(folder,"Obhod.exe");File.WriteAllText(target,"old installation");File.WriteAllText(target+".config","<configuration/>");
                var start=new ProcessStartInfo(Path.Combine(root,"tests","LegacyBridge.exe"),Strategy.Quote(folder)+" "+Strategy.Quote(Path.Combine(feed,"update.json"))+" "+Strategy.Quote(archive)){UseShellExecute=false,CreateNoWindow=true};start.EnvironmentVariables["BYPASS_LEGACY_TEST_FEED"]=feed;
                using(var bridge=Process.Start(start)){bool finished=bridge.WaitForExit(15000);if(!finished)bridge.Kill();Assert(finished && bridge.ExitCode==0,"Actual 0.4.4 updater stages new shell and receives signed helper readiness");}
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(folder,"updated.txt")) && watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
                Assert(File.Exists(Path.Combine(folder,"updated.txt")) && Trust.Hash(target)==Trust.Hash(exe),"Legacy update installs exact owner-signed executable and restarts");
                var outcome=ShellUpdates.TakeOutcome(folder);Assert(outcome!=null && outcome.Success,"Legacy migration records successful signed update");
                Assert(OwnerIdentity.Current(target),"Legacy migration restores author receipt for visible confirmation");
                // A local attacker can forge all old hash sidecars, but cannot
                // forge the independently signed file hashes obtained by the helper.
                foreach(bool tamperExe in new[]{true,false}) {
                    string stage=Path.Combine(folder,"data","app-updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
                    string source=Path.Combine(stage,"Obhod.exe");File.Copy(exe,source);File.Copy(config,source+".config");
                    if(tamperExe)File.AppendAllText(source,"forged");else File.WriteAllText(source+".config","<configuration><!--forged--></configuration>");
                    File.WriteAllText(Path.Combine(stage,"verified.sha256"),Trust.Hash(source));File.WriteAllText(Path.Combine(stage,"config.sha256"),Trust.Hash(source+".config"));File.WriteAllText(Path.Combine(stage,"version.txt"),ShellUpdates.Current);
                    var helperStart=new ProcessStartInfo(source,"--apply-update "+Process.GetCurrentProcess().Id+" "+Strategy.Quote(target)+" --ready"){UseShellExecute=false,CreateNoWindow=true};helperStart.EnvironmentVariables["BYPASS_LEGACY_TEST_FEED"]=feed;
                    using(var helper=Process.Start(helperStart)){bool finished=helper.WaitForExit(5000);if(!finished)helper.Kill();Assert(finished && !File.Exists(Path.Combine(stage,"helper-ready.txt")),"Legacy helper rejects forged "+(tamperExe?"EXE":"config")+" and matching plain sidecars");}
                    Assert(Trust.Hash(target)==Trust.Hash(exe),"Rejected legacy update preserves installed binary");
                }
            }finally{RemoveFixture(folder);}
        }
        static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception(message); }
        public static void Copy(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
        [STAThread] static int Main(string[] args) {
            if (args.Contains("--stub") || Path.GetFileName(Application.ExecutablePath).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Test stub started"); Thread.Sleep(30000); return 0; }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try {
                var root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                var local = Path.GetFullPath(Path.Combine(root, "..", "zapret-discord-youtube-1.10.2"));
                var official = Path.Combine(root, "downloads", "official-1.10.3", "zapret-discord-youtube-1.10.3");
                local=official;SecurityTests(root,official);LegacyMigrationTest(root); SelectionTests().GetAwaiter().GetResult(); ServiceProbeTests().GetAwaiter().GetResult(); DomainTests(root); UpdateFeedTests().GetAwaiter().GetResult(); AppUpdateSessionTests(root).GetAwaiter().GetResult(); StageTests(root).GetAwaiter().GetResult(); ShellReplacementTest(root); ShellRollbackTest(root); ShellHandshakeTest(root);
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
                    var store = new Store(temp);store.Settings.Engine="1.10.3-local"; Copy(local, store.Current); var old = store.Current;
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
                    using (var runner = new DirectEngineRunner(true)) {
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
                        Assert(form.Controls.Count > 0, "Desktop UI constructed and rendered"); ProblemUiChecks(form,root); DiagnosticLogTests(store,root); InteractiveUiChecks(form,root); ResponsiveScreens(form,root); AppUpdateUiTests(store,root); AutomaticUpdateTests(root,official); AppUpdateOutcomeUiTests(store);
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
        static void ProblemUiChecks(MainForm form,string root) {
            var notice=AllControls(form).OfType<ProblemNotice>().Single();var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            string detail=@"Отказано в доступе C:\ProgramData\ByPass\runtime-fixture\bin\WinDivert64.sys";
            typeof(MainForm).GetMethod("Error",flags).Invoke(form,new object[]{new EngineSetupException(detail,new UnauthorizedAccessException(detail))});Application.DoEvents();
            Assert(notice.Visible && notice.Message.Text.Contains("Не удалось включить обход") && !notice.Message.Text.Contains("WinDivert") && !notice.Message.Text.Contains("C:"),"System exception becomes a calm inline explanation without raw paths or blocking dialog");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(root,"tests","window-problem.png"));}
            notice.Details.PerformClick();Application.DoEvents();
            Assert(FindTabs(form).SelectedIndex==3 && AllControls(form).OfType<TextBox>().Any(x=>x.Text.Contains(detail)),"Technical cause remains available through the notice's log button");
            notice.Dismiss.PerformClick();Assert(!notice.Visible,"Problem notice can be dismissed without acknowledging a modal warning");
            Assert(UserProblems.Message(new EngineSetupException("fixture",new System.ComponentModel.Win32Exception(1223))).Contains("Запуск отменён"),"Declined Windows elevation gets a clear cancellation explanation");
            FindTabs(form).SelectedIndex=5;Application.DoEvents();
            var mark=AllControls(form).OfType<Label>().Single(x=>x.Text.StartsWith("ByPass "+ShellUpdates.Current+" ·"));
            Assert(mark.Visible && mark.Cursor!=Cursors.Hand && mark.Parent!=form && !form.Controls.Contains(mark),"Author identity is a non-clickable line in Help, without a global footer");
            FindTabs(form).SelectedIndex=0;Application.DoEvents();
        }
        static void DiagnosticLogTests(Store store,string root) {
            string folder=Path.Combine(root,"tests","fixture-log-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try {
                string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),file=Path.Combine(folder,"report.txt");
                File.AppendAllText(Path.Combine(store.Root,"data","app.log"),"Fixture failure: "+profile+"\\Desktop\\ByPass\\file.sys\n");
                DiagnosticLog.Save(store,false,"general.bat","visible fallback",file);string report=File.ReadAllText(file);
                Assert(report.Contains("Оболочка: "+ShellUpdates.Current) && report.Contains("Ядро:") && report.Contains("Обход: выключен") && report.Contains("Fixture failure"),"Exported support file contains versions, connection state and actual saved errors");
                Assert(report.Contains("%USERPROFILE%") && !report.Contains(profile),"Support export masks user profile paths instead of collecting account information");
            }finally{RemoveFixture(folder);}
        }
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        static void NativeFrameChecks(MainForm form,string root){
            Assert((NativeFrame.GetWindowLong(form.Handle,-16)&NativeFrame.Caption)==0,"Native caption absent after window creation");
            SendMessage(form.Handle,0x86,IntPtr.Zero,IntPtr.Zero);SendMessage(form.Handle,0x86,new IntPtr(1),IntPtr.Zero);
            form.WindowState=FormWindowState.Maximized;Application.DoEvents();form.WindowState=FormWindowState.Normal;Application.DoEvents();
            Assert(form.ClientSize==form.Size && (NativeFrame.GetWindowLong(form.Handle,-16)&NativeFrame.Caption)==0,"Window frame stays removed after activation and maximize/restore");
            var edges=new[]{new Point(1,form.Height/2),new Point(form.Width-2,form.Height/2),new Point(form.Width/2,1),new Point(1,1),new Point(form.Width-2,1),new Point(form.Width/2,form.Height-2),new Point(1,form.Height-2),new Point(form.Width-2,form.Height-2)};
            int[] hits={10,11,12,13,14,15,16,17};
            for(int i=0;i<edges.Length;i++) {
                Point screen=form.PointToScreen(edges[i]);long packed=(ushort)screen.X|((long)(ushort)screen.Y<<16);
                Assert(form.GetChildAtPoint(edges[i])==null && SendMessage(form.Handle,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt32()==hits[i],"Window edge and corner "+hits[i]+" reaches native resize handling");
            }
            var maximize=AllControls(form).OfType<Button>().Single(x=>x.Name=="MaximizeWindow");
            maximize.PerformClick();Application.DoEvents();
            // Windows can move an off-screen test window to another monitor when maximizing.
            Rectangle work=Screen.FromControl(form).WorkingArea;
            Assert(form.Visible && form.WindowState==FormWindowState.Maximized && form.Bounds==work,"Actual maximize button keeps the window visible inside monitor working area: visible="+form.Visible+", state="+form.WindowState+", bounds="+form.Bounds+", expected="+work);
            maximize.PerformClick();Application.DoEvents();
            Assert(form.Visible && form.WindowState==FormWindowState.Normal,"Actual maximize button restores without hiding to tray");
            IntPtr data=Marshal.AllocHGlobal(40);
            try {
                for(int i=0;i<10;i++)Marshal.WriteInt32(data,i*4,0);
                NativeFrame.ConstrainMaximize(data,new Rectangle(-1920,-1080,1920,1080),new Rectangle(-1880,-1080,1880,1040),new Size(940,690));
                Assert(Marshal.ReadInt32(data,8)==1880 && Marshal.ReadInt32(data,12)==1040 && Marshal.ReadInt32(data,16)==40 && Marshal.ReadInt32(data,20)==0,"Maximize uses monitor-relative coordinates on negative-coordinate desktop and respects taskbar");
            }finally{Marshal.FreeHGlobal(data);}
            using(var picture=new Bitmap(form.Width,form.Height)){var g=Graphics.FromImage(picture);var context=g.GetHdc();try{Assert(PrintWindow(form.Handle,context,0),"Entire native window captured including non-client area");}finally{g.ReleaseHdc(context);g.Dispose();}picture.Save(Path.Combine(root,"tests","window-native.png"));}
        }
        sealed class MockHttp : System.Net.Http.HttpMessageHandler {
            public int Calls;public readonly System.Collections.Generic.List<string> Urls=new System.Collections.Generic.List<string>();
            public Func<string,System.Net.Http.HttpResponseMessage> Reply;
            public Func<string,CancellationToken,Task<System.Net.Http.HttpResponseMessage>> AsyncReply;
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token){Calls++;Urls.Add(request.RequestUri.AbsoluteUri);return AsyncReply==null?Task.FromResult(Reply(request.RequestUri.AbsoluteUri)):AsyncReply(request.RequestUri.AbsoluteUri,token);}
        }
        static string RawFeed(string version){return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{schema=1,shell_files=new{exe=new string('a',64),config=new string('a',64)},tag_name="v"+version,draft=false,prerelease=false,assets=new[]{new{name="Obhod-"+version+"-win-x64.zip",browser_download_url="https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+version+"/Obhod-"+version+"-win-x64.zip",digest="sha256:"+new string('a',64),size=123}}});}
        static System.Net.Http.HttpResponseMessage Response(int status,string text){return new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status){Content=new System.Net.Http.StringContent(text)};}
        static async Task UpdateFeedTests(){
            var http=new MockHttp{Reply=url=>Response(200,Feed("99.0.0"))};
            using(var updates=new ShellUpdates(http)){var release=await updates.Check();Assert(release.Version=="99.0.0" && http.Calls==1 && http.Urls[0].EndsWith("/releases/latest/download/update.json"),"New shell release discovered without public API calls");}
            http=new MockHttp{Reply=url=>Response(url.Contains("/download/")?404:200,Feed(ShellUpdates.Current))};
            using(var updates=new ShellUpdates(http)){bool refused=false;try{await updates.Check();}catch(InvalidDataException){refused=true;}Assert(refused && http.Calls==1,"Missing signed feed never falls back to unsigned API metadata");}
            http=new MockHttp{Reply=url=>Response(200,Sign(RawFeed("99.0.0").Replace("sha256:","sha512:")))};
            using(var updates=new ShellUpdates(http)){bool rejected=false;try{await updates.Check();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Feed without a valid hash cannot install an update");}
            http=new MockHttp{Reply=url=>{var response=Response(403,"API rate limit exceeded");response.Headers.Add("X-RateLimit-Remaining","0");response.Headers.Add("X-RateLimit-Reset",((long)(DateTimeOffset.UtcNow.AddHours(1)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds).ToString());return response;}};
            using(var client=new System.Net.Http.HttpClient(http)){for(int i=0;i<2;i++){bool friendly=false;try{await GitHubMetadata.Get(client,"https://api.github.com/repos/test-fixture/backoff/releases/latest");}catch(UpdateCheckException ex){friendly=ex.Message.Contains("Попробуйте позже") && !ex.Message.Contains("403");}Assert(friendly,"Rate limit gets a readable message");}Assert(http.Calls==1,"Repeated checks respect GitHub reset time and do not send another request");}
            http=new MockHttp{Reply=url=>Response(200,new string('a',1024*1024+1))};
            using(var client=new System.Net.Http.HttpClient(http)){bool rejected=false;try{await GitHubMetadata.Get(client,"https://github.com/test/oversized");}catch(InvalidDataException){rejected=true;}Assert(rejected,"Oversized release metadata rejected");}
            Assert(ServiceChecks.Targets.Last().Url.StartsWith("https://github.com/"),"Strategy probes check GitHub availability without consuming API quota");
        }
        sealed class ImmediateProgress : IProgress<int> {
            public Action<int> Changed;
            public void Report(int value){Changed(value);}
        }
        static async Task AppUpdateSessionTests(string root){
            string folder=Path.Combine(root,"tests","fixture-session-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"data"));
            string settings=Path.Combine(folder,"data","settings.json");File.WriteAllText(settings,"untouched");
            var http=new MockHttp{Reply=url=>url.EndsWith("update.json")?Response(200,Feed("99.0.0")):Response(200,new string('x',123))};
            try{
                using(var session=new AppUpdateSession(new ShellUpdates(http))){
                    Assert(session.ActionText=="Проверить обновления","Idle updater offers one check action");
                    await session.Check();
                    Assert(http.Calls==1 && session.State==AppUpdateState.Available && session.ActionText=="Обновить" && session.Prepared==null,"Checking discovers a release without downloading or installing");
                    Assert(!await session.Prepare(folder) && session.State==AppUpdateState.Failed && session.ActionText=="Повторить обновление","Bad archive leaves an inline retryable update state");
                    Assert(!Directory.EnumerateDirectories(Path.Combine(folder,"data","app-updates")).Any() && File.ReadAllText(settings)=="untouched","Failed download removes its staging directory and preserves settings");
                    Assert(!await session.Prepare(folder) && http.Calls==3,"Install retry reuses checked release and downloads again");
                    http.Reply=url=>{session.Cancel();return Response(200,new string('x',123));};
                    Assert(!await session.Prepare(folder) && session.State==AppUpdateState.Available && session.ActionText=="Обновить","Cancellation returns to the available update without losing it");
                    http.Reply=url=>Response(200,Feed(ShellUpdates.Current));await session.Check();
                    Assert(session.State==AppUpdateState.Current && session.Release==null && session.Prepared==null,"A fresh check clears an outdated candidate");
                    session.Complete();Assert(session.Message.Contains(ShellUpdates.Current),"Installed version is shown without another network request");
                }
                http=new MockHttp{Reply=url=>Response(500,"Unavailable")};
                using(var session=new AppUpdateSession(new ShellUpdates(http))){await session.Check();Assert(session.State==AppUpdateState.Failed && session.ActionText=="Повторить проверку","Check failure offers a distinct check retry");http.Reply=url=>Response(200,Feed(ShellUpdates.Current));await session.Check();Assert(session.State==AppUpdateState.Current,"Check retry recovers in the same view");}
            }finally{RemoveFixture(folder);}
        }
        static async Task StageTests(string root){
            foreach(string brand in new[]{"Obhod","ByPass"}) {
            string folder=Path.Combine(root,"tests","fixture-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            string archive=Path.Combine(folder,"source.zip");
            try{
                string configFixture=Path.Combine(folder,"config");File.WriteAllText(configFixture,"<configuration/>");
                using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){
                    zip.CreateEntryFromFile(Path.Combine(root,"tests","Obhod-probe.exe"),brand+"/"+brand+".exe");
                    using(var text=new StreamWriter(zip.CreateEntry(brand+"/"+brand+".exe.config").Open()))text.Write("<configuration/>");
                    using(var text=new StreamWriter(zip.CreateEntry(brand+"/"+OwnerIdentity.ProofName).Open()))text.Write(BuildProof(Path.Combine(root,"tests","Obhod-probe.exe"),configFixture));
                    byte[] bytes=new byte[250000];new Random(42).NextBytes(bytes);using(var stream=zip.CreateEntry(brand+"/unused.bin").Open())stream.Write(bytes,0,bytes.Length);
                }
                byte[] content=File.ReadAllBytes(archive);
                var release=new Release{Version=ShellUpdates.Current,Url="https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+ShellUpdates.Current+"/Obhod-"+ShellUpdates.Current+"-win-x64.zip",Size=content.Length,Digest="sha256:"+Updater.Hash(archive)};
                release.Proof=Proof(release.Version,Path.Combine(root,"tests","Obhod-probe.exe"),configFixture,release.Digest,release.Size);
                var http=new MockHttp{Reply=url=>new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(content)}};
                using(var updater=new ShellUpdates(http)){
                    int previous=0,reports=0;
                    var prepared=await updater.Stage(release,folder,new ImmediateProgress{Changed=value=>{Assert(value>=previous && value<=100,"Download progress is bounded and monotonic");previous=value;reports++;}},CancellationToken.None);
                    Assert(reports>1 && previous==100 && File.Exists(prepared) && !File.Exists(Path.Combine(Path.GetDirectoryName(prepared),"unused.bin")),"Verified update stages only the application with complete progress");
                    ShellUpdates.ValidateBinary(prepared,ShellUpdates.Current);
                    using(var cancellation=new CancellationTokenSource()){
                        cancellation.Cancel();bool cancelled=false;int calls=http.Calls;
                        try{await updater.Stage(release,folder,null,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
                        Assert(cancelled && http.Calls==calls,"Already cancelled install makes no download request");
                    }
                    int stages=Directory.GetDirectories(Path.Combine(folder,"data","app-updates")).Length;
                    using(var cancellation=new CancellationTokenSource()){
                        bool cancelled=false;try{await updater.Stage(release,folder,new ImmediateProgress{Changed=value=>cancellation.Cancel()},cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
                        Assert(cancelled && Directory.GetDirectories(Path.Combine(folder,"data","app-updates")).Length==stages,"Cancellation during download removes the incomplete stage");
                    }
                    File.AppendAllText(prepared,"tampered");
                    bool refused=false;try{await ShellUpdates.Launch(prepared,Path.Combine(folder,brand+".exe"),false);}catch(InvalidDataException){refused=true;}
                    Assert(refused,"Changed prepared binary is rejected before starting a helper or stopping the application");
                }
            }finally{RemoveFixture(folder);}
            }
        }
        static void Pump(Task task,int timeout=15000){
            var watch=Stopwatch.StartNew();while(!task.IsCompleted && watch.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(10);}Assert(task.IsCompleted,"Update action finishes without a modal dialog");task.GetAwaiter().GetResult();Application.DoEvents();
        }
        static Task UpdateClick(MainForm form){return (Task)typeof(MainForm).GetMethod("AppUpdateAction",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,null);}
        static void Capture(Form form,string path){form.PerformLayout();Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(path);}}
        static void AppUpdateUiTests(Store store,string root){
            var http=new MockHttp{Reply=url=>Response(200,Feed("99.0.0"))};
            bool accept=false;int confirmations=0;
            using(var form=new MainForm(store,false,false,false,false,new ShellUpdates(http),(title,message)=>{confirmations++;return accept;})){
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2500,-2500);form.Show();Application.DoEvents();SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());var tabs=FindTabs(form);tabs.SelectedIndex=2;
                var card=AllControls(form).OfType<AppUpdateCard>().Single();
                Assert(card.Action.Text=="Проверить обновления" && card.Action.Enabled && AllControls(card).OfType<Button>().Count(x=>x.Visible)==1,"Updates view starts with exactly one visible action");
                Capture(form,Path.Combine(root,"tests","window-updates-idle.png"));
                Pump(UpdateClick(form));Assert(card.Action.Text=="Обновить" && http.Calls==1,"Native update button changes to install without downloading");
                Assert(form.Text=="ByPass" && AllControls(form).OfType<UpdateNotice>().Single().Visible && confirmations==0,"ByPass shows a quiet update notice without an automatic confirmation dialog");
                Pump(UpdateClick(form));Assert(confirmations==1 && http.Calls==1 && card.Action.Text=="Обновить","Declining the update makes no download request and keeps the available version");
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var coreField=typeof(MainForm).GetField("release",flags);coreField.SetValue(form,new Release{Version="99.0.0"});
                typeof(MainForm).GetMethod("RefreshState",flags).Invoke(form,null);
                Assert(AllControls(form).OfType<Label>().Any(x=>x.Visible && x.Text.StartsWith("Доступна новая версия ядра")),"Core update notice is visible beside the shell notice on any tab");
                if(!((EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form)).Conflict){
                    Pump((Task)typeof(MainForm).GetMethod("Install",flags).Invoke(form,null));
                    Assert(confirmations==2 && coreField.GetValue(form)!=null,"Declining a core update keeps its candidate without starting installation");
                }
                Capture(form,Path.Combine(root,"tests","window-update-notices.png"));
                foreach(var dismiss in AllControls(form).OfType<UpdateNotice>().Single().Controls.Cast<Control>().SelectMany(x=>AllControls(x)).OfType<Button>().Where(x=>x.Text=="×").ToArray())dismiss.PerformClick();
                Assert(!AllControls(form).OfType<UpdateNotice>().Single().Visible && card.Action.Text=="Обновить","Notices can be dismissed while updating stays available in its tab");
                coreField.SetValue(form,null);typeof(MainForm).GetMethod("RefreshState",flags).Invoke(form,null);
                accept=true;
                Capture(form,Path.Combine(root,"tests","window-updates-available.png"));
                http.AsyncReply=async(url,token)=>{await Task.Delay(1000,token);return Response(200,new string('x',123));};
                var pending=UpdateClick(form);Application.DoEvents();
                Assert(card.Progress.Visible && card.Cancel.Visible && !card.Action.Enabled,"Loading displays progress and cancellation and prevents duplicate installs");
                Capture(form,Path.Combine(root,"tests","window-updates-progress.png"));card.Cancel.PerformClick();Pump(pending);
                Assert(card.Action.Text=="Обновить" && card.Action.Enabled && !card.Progress.Visible,"Cancellation leaves the same window ready to install later");
                http.AsyncReply=null;http.Reply=url=>Response(200,new string('x',123));Pump(UpdateClick(form));
                Assert(card.Action.Text=="Повторить обновление" && card.Action.Enabled && Application.OpenForms.Count==2,"Failed installation stays inline and leaves the existing window usable");
                Capture(form,Path.Combine(root,"tests","window-updates-error.png"));
                foreach(var size in new[]{new Size(940,690),new Size(1400,960)}){form.Size=size;form.PerformLayout();Application.DoEvents();Assert(card.Action.Right<=card.Width && card.AutoInstall.Bottom<=card.Height && card.Bottom<=card.Parent.Height,"Update actions fit resized window "+size);}
                Capture(form,Path.Combine(root,"tests","window-updates-compact.png"));
                var components=AllControls(form).OfType<Label>().Single(x=>x.Text=="Ядро");
                Assert(components.Visible && AllControls(components.Parent).Where(x=>x.Visible && x!=components.Parent).All(x=>x.Bottom<=components.Parent.Height),"Core card is expanded by default and every update control fits its card");
                Capture(form,Path.Combine(root,"tests","window-updates-components.png"));
                http.Reply=url=>Response(200,Feed(ShellUpdates.Current));var session=(AppUpdateSession)typeof(MainForm).GetField("appUpdates",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(form);Pump(session.Check());
                Capture(form,Path.Combine(root,"tests","window-updates-current.png"));
            }
        }
        static void AppUpdateOutcomeUiTests(Store store){
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();bool previous=store.Settings.AutoAppUpdates;
            try{
                store.Settings.AutoAppUpdates=true;
                foreach(bool success in new[]{true,false}){
                    File.WriteAllText(Path.Combine(store.Root,"data","app-update-result.json"),serializer.Serialize(new ShellUpdates.Outcome{Success=success,Version=ShellUpdates.Current,Error="fixture raw server error"}));
                    var http=new MockHttp{Reply=url=>Response(500,"Unavailable")};
                    using(var form=new MainForm(store,false,false,false,false,new ShellUpdates(http))){
                        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2500,-2500);form.Show();Application.DoEvents();
                        Assert(FindTabs(form).SelectedIndex==2 && http.Calls==0,"Restart displays the update result without another automatic check");
                        Assert(AllControls(form).OfType<Label>().Any(x=>success?x.Text=="Обновлено до версии "+ShellUpdates.Current:x.Text=="Обновление не установлено. Текущая версия сохранена.") && !AllControls(form).OfType<Label>().Any(x=>x.Text.Contains("fixture raw")),"Restart reports success or failure inline without technical error text");
                        Assert(!File.Exists(Path.Combine(store.Root,"data","app-update-result.json")),"Restart outcome is consumed once");
                    }
                }
            }finally{store.Settings.AutoAppUpdates=previous;}
        }
        static void AutomaticUpdateTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-auto-update-"+Guid.NewGuid().ToString("N"));var store=new Store(folder);Copy(official,store.Current);
            store.Settings.AutoAppUpdates=false;store.Settings.CheckUpdates=false;store.Save();
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            try {
                Assert(!new Store(folder).Settings.AutoInstallShell && !new Store(folder).Settings.AutoInstallCore,"Automatic installation is opt-in for both components, including old settings");
                // A harmless fixture representing an old installation. Never
                // execute its binaries or request elevation.
                File.WriteAllText(Path.Combine(store.Current,"service.bat"),"set LOCAL_VERSION=1.10.2\n");
                File.WriteAllText(Path.Combine(store.Current,"lists","list-general-user.txt"),"friend.example\n");
                byte[] archive=File.ReadAllBytes(Path.Combine(root,"downloads","zapret-discord-youtube-1.10.3.zip"));
                string metadata=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{tag_name="1.10.3",body="fixture",assets=new[]{new{name="zapret-discord-youtube-1.10.3.zip",browser_download_url="https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip",digest="sha256:"+Updater.Hash(Path.Combine(root,"downloads","zapret-discord-youtube-1.10.3.zip")),size=archive.Length}}});
                int downloads=0;
                var coreHttp=new MockHttp{Reply=url=>{if(url.EndsWith("/releases/latest"))return Response(200,metadata);downloads++;return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(archive)};}};
                Exception failure=null;
                // Use the real WinForms message loop on an isolated STA thread,
                // rather than mixing archive continuations with DoEvents probes.
                var worker=new Thread(()=>{
                    try {
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                        var shellHttp=new MockHttp{Reply=url=>Response(200,url.EndsWith("update.json")?Feed("99.0.0"):new string('x',123))};int confirmations=0;
                        using(var form=new MainForm(store,false,false,false,false,new ShellUpdates(shellHttp),(title,message)=>{confirmations++;return false;},new Updater(store,coreHttp))) {
                            bool externalCore=false;var fixtureRunner=(EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form);fixtureRunner.ConflictCheck=()=>externalCore;
                            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2500,-2500);
                            form.Shown+=async(s,e)=>{
                                try {
                                    FindTabs(form).SelectedIndex=2;var toggle=AllControls(form).OfType<AppUpdateCard>().Single().AutoInstall;
                                    toggle.Checked=true;var saved=new Store(folder);
                                    Assert(saved.Settings.AutoInstallShell && saved.Settings.AutoAppUpdates && !saved.Settings.AutoInstallCore,"Shell switch persists independently and enables startup checks");
                                    await (Task)typeof(MainForm).GetMethod("CheckShell",flags).Invoke(form,new object[]{true});
                                    var session=(AppUpdateSession)typeof(MainForm).GetField("appUpdates",flags).GetValue(form);
                                    Assert(shellHttp.Calls==2 && confirmations==0 && session.State==AppUpdateState.Failed && session.Prepared==null,"Opted-in shell update downloads automatically without confirmation and rejects a bad archive");
                                    toggle.Checked=false;
                                    await (Task)typeof(MainForm).GetMethod("CheckUpdates",flags).Invoke(form,new object[]{true});
                                    Assert(downloads==0,"Core startup check cannot download without the automatic-install setting");
                                    var coreToggle=(CheckBox)typeof(MainForm).GetField("autoInstallCore",flags).GetValue(form);coreToggle.Checked=true;
                                    Assert(new Store(folder).Settings.AutoInstallCore && !new Store(folder).Settings.AutoInstallShell,"Core switch persists separately from shell installation");
                                    externalCore=true;await (Task)typeof(MainForm).GetMethod("CheckUpdates",flags).Invoke(form,new object[]{true});
                                    Assert(downloads==0,"Automatic core update waits while an external core is running");externalCore=false;
                                    await (Task)typeof(MainForm).GetMethod("CheckUpdates",flags).Invoke(form,new object[]{true});
                                    Assert(downloads==1 && confirmations==0 && Updater.Version(store.Current)=="1.10.3" && Trust.VerifyEngine(store.Current)!=null,"Opted-in core update downloads and installs the pinned verified archive without a confirmation dialog");
                                    Assert(File.ReadAllText(Path.Combine(store.Current,"lists","list-general-user.txt"))=="friend.example\n" && !String.IsNullOrEmpty(store.Settings.PreviousEngine),"Automatic core installation preserves user lists and the previous version");
                                    coreToggle.Checked=false;
                                }catch(Exception ex){failure=ex;}
                                finally{typeof(MainForm).GetField("exiting",flags).SetValue(form,true);form.Close();}
                            };
                            Application.Run(form);
                        }
                    }catch(Exception ex){failure=ex;}
                });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
                Assert(worker.Join(60000),"Automatic update finishes in the real WinForms message loop");if(failure!=null)throw failure;
            }finally{RemoveFixture(folder);}
        }
        static void ShellRollbackTest(string root){
            string folder=Path.Combine(root,"tests","fixture-rollback-"+Guid.NewGuid().ToString("N")),stage=Path.Combine(folder,"data","app-updates","probe");Directory.CreateDirectory(stage);
            try{
                string source=Path.Combine(stage,"Obhod.exe"),target=Path.Combine(folder,"Obhod.exe");
                File.Copy(Path.Combine(root,"tests","Obhod-probe.exe"),source);File.WriteAllText(source+".config","<configuration/>");
                WriteProof(source);
                File.Copy(source,target);using(var stream=new FileStream(target,FileMode.Append))stream.WriteByte(42);string oldHash=Updater.Hash(target);
                File.WriteAllText(target+".config","<configuration/>");Directory.CreateDirectory(target+".config.new");
                using(var helper=Process.Start(new ProcessStartInfo(source,"--apply-update 2147483647 "+Strategy.Quote(target)+" --connect"){UseShellExecute=false,CreateNoWindow=true})){bool exited=helper.WaitForExit(10000);if(!exited)helper.Kill();Assert(exited,"Failed helper exits without opening a warning dialog");}
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(folder,"updated.txt")) && watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
                Assert(Updater.Hash(target)==oldHash && File.ReadAllText(target+".config")=="<configuration/>","Failure after EXE replacement restores the old executable and config");
                var outcome=ShellUpdates.TakeOutcome(folder);Assert(outcome!=null && !outcome.Success && !String.IsNullOrEmpty(outcome.Error),"Rollback leaves a result for inline reporting on restart");
                Assert(File.Exists(Path.Combine(folder,"updated.txt")) && File.ReadAllText(Path.Combine(folder,"updated.txt")).Contains("--connect"),"Rolled back application restarts and restores its connection");
            }finally{RemoveFixture(folder);}
        }
        static void InteractiveUiChecks(MainForm form,string root) {
            var tabs=FindTabs(form);tabs.SelectedIndex=0;var dashboard=AllControls(form).OfType<Dashboard>().Single();
            typeof(DarkSelect).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(dashboard.Profiles,new object[]{EventArgs.Empty});Application.DoEvents();
            var popup=(ToolStripDropDown)typeof(DarkSelect).GetField("popup",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(dashboard.Profiles);
            Assert(popup!=null && popup.Visible,"Custom strategy dropdown opens");
            using(var image=new Bitmap(popup.Width,popup.Height)){popup.DrawToBitmap(image,new Rectangle(Point.Empty,popup.Size));image.Save(Path.Combine(root,"tests","window-dropdown.png"));}
            dashboard.Profiles.Choose(1);Assert(dashboard.Profiles.SelectedIndex==1,"Custom strategy dropdown applies item selection");dashboard.Profiles.SelectedIndex=0;
            tabs.SelectedIndex=tabs.TabPages.FindIndex(x=>AllControls(x).OfType<DomainEditor>().Any());var editor=AllControls(form).OfType<DomainEditor>().Single();editor.TextEditor.Text=String.Join("\r\n",Enumerable.Range(1,100).Select(i=>"site-"+i+".example"));Application.DoEvents();
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
            form.Size=original;tabs.SelectedIndex=tabs.TabPages.FindIndex(x=>x.Text=="Настройки");
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
            foreach(string brand in new[]{"Obhod","ByPass"}) {
            var folder=Path.Combine(root,"tests","fixture-update-"+Guid.NewGuid().ToString("N"));
            var stage=Path.Combine(folder,"data","app-updates","probe");Directory.CreateDirectory(stage);
            try {
                var source=Path.Combine(stage,"ByPass.exe");var target=Path.Combine(folder,brand+".exe");
                File.Copy(Path.Combine(root,"tests","Obhod-probe.exe"),source);
                File.WriteAllText(source+".config","<?xml version=\"1.0\"?><configuration><startup><supportedRuntime version=\"v4.0\"/></startup></configuration>");
                WriteProof(source);
                File.WriteAllText(target,"previous executable");File.WriteAllText(target+".config","previous config");
                string settings=Path.Combine(folder,"data","settings.json");File.WriteAllText(settings,"preserved settings");
                using(var helper=Process.Start(new ProcessStartInfo(source,"--apply-update 2147483647 "+Strategy.Quote(target)+" --connect"){UseShellExecute=false,CreateNoWindow=true})) {
                    Assert(helper.WaitForExit(10000),"Shell update helper exits after replacing and restarting");
                }
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(folder,"updated.txt")) && watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
                Assert(File.Exists(Path.Combine(folder,"updated.txt")) && File.ReadAllText(Path.Combine(folder,"updated.txt")).Contains("--connect"),"Updated shell is restarted with the previous connection");
                var outcome=ShellUpdates.TakeOutcome(folder);Assert(outcome!=null && outcome.Success && outcome.Version==ShellUpdates.Current && ShellUpdates.TakeOutcome(folder)==null,"Successful update result consumed once by the main window");
                Assert(Updater.Hash(source)==Updater.Hash(target),"Exact verified executable installed");
                Assert(File.ReadAllText(target+".previous")=="previous executable" && File.ReadAllText(target+".config.previous")=="previous config","Old executable and configuration backed up");
                Assert(File.ReadAllText(settings)=="preserved settings","Shell update does not overwrite user settings");
            } finally {RemoveFixture(folder);}
            }
        }
        static void ShellHandshakeTest(string root){
            string folder=Path.Combine(root,"tests","fixture-handshake-"+Guid.NewGuid().ToString("N")),stage=Path.Combine(folder,"data","app-updates","probe");Directory.CreateDirectory(stage);
            try{
                string source=Path.Combine(stage,"Obhod.exe"),target=Path.Combine(folder,"Obhod.exe");
                File.Copy(Path.Combine(root,"tests","Obhod-probe.exe"),source);File.WriteAllText(source+".config","<configuration/>");
                WriteProof(source);
                File.WriteAllText(target,"still running");
                using(var helper=Process.Start(new ProcessStartInfo(source,"--apply-update "+Process.GetCurrentProcess().Id+" "+Strategy.Quote(target)+" --ready"){UseShellExecute=false,CreateNoWindow=true})){
                    try{var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(stage,"helper-ready.txt")) && watch.ElapsedMilliseconds<5000 && !helper.HasExited)Thread.Sleep(20);Assert(File.Exists(Path.Combine(stage,"helper-ready.txt")) && !helper.HasExited && File.ReadAllText(target)=="still running","Validated helper acknowledges readiness and waits before replacing the running application");}
                    finally{if(!helper.HasExited){helper.Kill();helper.WaitForExit(5000);}}
                }
            }finally{RemoveFixture(folder);}
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
            try{DomainLists.Write(folder,0,"first.example");string path=DomainLists.FilePath(folder,0);Reject(()=>DomainLists.Write(folder,0,"invalid url"),"Invalid list cannot overwrite saved entries");Assert(File.ReadAllText(path).Contains("first.example"),"Original list preserved on invalid input");DomainLists.Write(folder,0,"second.example");Assert(File.ReadAllText(path+".bak").Contains("first.example"),"Atomic domain replacement preserves backup");DomainLists.Write(folder,0,"");Assert(DomainLists.Read(folder,0)=="" && new FileInfo(path).Length>0,"Empty user lists remain compatible with the engine and show no placeholder");File.WriteAllText(path,"# Never leave this file empty\r\ndomain.example.abc\r\n");Assert(DomainLists.Read(folder,0)=="","Upstream technical comment is hidden from the editor");DomainLists.Write(folder,2,"");Assert(DomainLists.Read(folder,2)=="" && new FileInfo(DomainLists.FilePath(folder,2)).Length>0,"Empty IP exclusions retain the engine sentinel");}finally{RemoveFixture(folder);}
        }
        sealed class ProbeHttp : System.Net.Http.HttpMessageHandler {
            public Func<System.Net.Http.HttpRequestMessage,CancellationToken,Task<System.Net.Http.HttpResponseMessage>> Reply;
            public int Calls;
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token) { Calls++;return Reply(request,token); }
        }
        sealed class WaitingStream : MemoryStream {
            public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token) { await Task.Delay(Timeout.Infinite,token);return 0; }
        }
        sealed class ProbeSocket : System.Net.WebSockets.WebSocket {
            readonly System.Collections.Generic.Queue<string> messages;
            public string Sent;
            public bool Fragment; bool fragmented;
            public ProbeSocket(params string[] texts) { messages=new System.Collections.Generic.Queue<string>(texts); }
            public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus { get { return null; } }
            public override string CloseStatusDescription { get { return null; } }
            public override string SubProtocol { get { return null; } }
            public override System.Net.WebSockets.WebSocketState State { get { return System.Net.WebSockets.WebSocketState.Open; } }
            public override void Abort() {}
            public override void Dispose() {}
            public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus status,string description,CancellationToken token) { return Task.FromResult(0); }
            public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus status,string description,CancellationToken token) { return Task.FromResult(0); }
            public override Task SendAsync(ArraySegment<byte> buffer,System.Net.WebSockets.WebSocketMessageType type,bool end,CancellationToken token) { Sent=System.Text.Encoding.UTF8.GetString(buffer.Array,buffer.Offset,buffer.Count);return Task.FromResult(0); }
            public override async Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token) {
                if(messages.Count==0){await Task.Delay(Timeout.Infinite,token);throw new Exception("unreachable");}
                string text=messages.Peek();bool end=true;
                if(Fragment && !fragmented){text=text.Substring(0,5);fragmented=true;end=false;}else {messages.Dequeue();if(Fragment && fragmented && text.StartsWith("{\"op\":10"))text=text.Substring(5);}
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(text);Array.Copy(bytes,0,buffer.Array,buffer.Offset,bytes.Length);
                return new System.Net.WebSockets.WebSocketReceiveResult(bytes.Length,System.Net.WebSockets.WebSocketMessageType.Text,end);
            }
        }
        static System.Net.Http.HttpResponseMessage DownloadSample(int status=206,int size=1024) {
            var response=new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status) {Content=new System.Net.Http.ByteArrayContent(Enumerable.Repeat((byte)42,size).ToArray())};
            response.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            if(status==206)response.Content.Headers.ContentRange=new System.Net.Http.Headers.ContentRangeHeaderValue(0,size-1,100000);
            return response;
        }
        static async Task ServiceProbeTests() {
            var update=ServiceChecks.Targets.Single(x=>x.Kind==ProbeKind.DiscordUpdates);
            var app=ServiceChecks.Targets.Single(x=>x.Kind==ProbeKind.DiscordApp);
            var gateway=ServiceChecks.Targets.Single(x=>x.Kind==ProbeKind.DiscordGateway);
            string manifest="{\"full\":{\"url\":\"https://stable.dl2.discordapp.net/distro/app/stable/win/x64/1.0.1/full.distro\"}}";
            Func<Uri,CancellationToken,Task> noGateway=(url,token)=>{throw new Exception("Gateway must not run for this probe");};
            var http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(request.RequestUri.Host=="updates.discord.com"?Response(200,manifest):DownloadSample())};
            var result=await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway);
            Assert(result.State==1 && http.Calls==2 && result.Detail.Contains("1024"),"Updater probe requires both manifest and download bytes");
            http=new ProbeHttp {Reply=(request,token)=>{if(request.RequestUri.Host=="updates.discord.com")return Task.FromResult(Response(200,manifest));Assert(request.Headers.Range.ToString()=="bytes=0-1023","Probe requests only a small download range");throw new System.Net.Http.HttpRequestException("fixture reset",new System.Net.Sockets.SocketException(10054));}};
            result=await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway);
            Assert(result.State==0 && result.Detail.Contains("stable.dl2.discordapp.net"),"Working manifest cannot hide a reset from download CDN");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(Response(200,"<html>blocked</html>"))};
            result=await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway);
            Assert(result.State==2 && http.Calls==1,"HTTP 200 with HTML is not a successful update manifest");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(Response(200,manifest.Replace("stable.dl2.discordapp.net","foreign.example")))};
            result=await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway);
            Assert(result.State==2 && http.Calls==1,"Foreign download hosts are refused without a request");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(request.RequestUri.Host=="updates.discord.com"?Response(200,manifest):DownloadSample(206,12))};
            Assert((await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway)).State==2,"Truncated update sample is not green");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(request.RequestUri.Host=="updates.discord.com"?Response(200,manifest):Response(200,"<html>blocked</html>"))};
            Assert((await ServiceChecks.Run(update,CancellationToken.None,2,http,noGateway)).State==2,"CDN error page with HTTP 200 is not green");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StreamContent(new WaitingStream())})};
            var watch=Stopwatch.StartNew();result=await ServiceChecks.Run(update,CancellationToken.None,1,http,noGateway);
            Assert(result.State==0 && result.Failure=="timeout" && watch.ElapsedMilliseconds<3000,"Timeout covers the body after successful HTTP headers");
            using(var cancel=new CancellationTokenSource()) {
                cancel.Cancel();http=new ProbeHttp {Reply=async(request,token)=>{await Task.Delay(Timeout.Infinite,token);return Response(200,manifest);}};
                result=await ServiceChecks.Run(update,cancel.Token,2,http,noGateway);
                Assert(result.State==0 && result.Failure=="cancelled","User cancellation is distinct from a timeout");
            }
            string html="<div id=\"app-mount\"></div><script src=\"/assets/first.js\"></script><script src=\"/assets/last.js\"></script>";
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("last.js")?503:200,request.RequestUri.AbsolutePath=="/app"?html:"console.log('fixture');"))};
            result=await ServiceChecks.Run(app,CancellationToken.None,2,http,noGateway);
            Assert(result.State==2 && result.Detail.Contains("last.js"),"App HTML cannot hide a failed bootstrap script");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(Response(200,request.RequestUri.AbsolutePath=="/app"?html:"console.log('fixture');"))};
            result=await ServiceChecks.Run(app,CancellationToken.None,2,http,noGateway);
            Assert(result.State==1 && http.Calls==3,"App probe reads HTML and both sampled bootstrap files");
            http=new ProbeHttp {Reply=(request,token)=>{var response=Response(302,"");response.Headers.Location=new Uri("https://foreign.example/blocked");return Task.FromResult(response);}};
            result=await ServiceChecks.Run(app,CancellationToken.None,2,http,noGateway);
            Assert(result.State==2 && http.Calls==1,"Discord redirect to a foreign site is not green and is not followed");
            http=new ProbeHttp {Reply=(request,token)=>Task.FromResult(Response(200,"{\"url\":\"wss://gateway.discord.gg\"}"))};
            result=await ServiceChecks.Run(gateway,CancellationToken.None,1,http,async(url,token)=>{await Task.Delay(Timeout.Infinite,token);});
            Assert(result.State==0 && result.Failure=="timeout" && result.Detail.Contains("gateway.discord.gg"),"API HTTP 200 cannot hide a blocked WebSocket");
            var socket=new ProbeSocket("{\"op\":10,\"d\":{\"heartbeat_interval\":41250}}","{\"op\":11,\"d\":null}"){Fragment=true};
            await ServiceChecks.VerifyGateway(socket,CancellationToken.None);
            Assert(socket.Sent=="{\"op\":1,\"d\":null}","Gateway probe assembles fragmented Hello and requires heartbeat ACK without account credentials");
            bool rejected=false;
            try{await ServiceChecks.VerifyGateway(new ProbeSocket("{\"op\":10,\"d\":{\"heartbeat_interval\":41250}}","{\"op\":9}"),CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"A WebSocket connection alone does not satisfy the Gateway check");
            using(var cancel=new CancellationTokenSource(100)) {
                bool cancelled=false;try{await ServiceChecks.VerifyGateway(new ProbeSocket("{\"op\":10,\"d\":{\"heartbeat_interval\":41250}}"),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled,"Missing Gateway ACK remains cancellable");
            }
            using(var response=Response(200,new string('x',65))) {
                rejected=false;try{await ServiceChecks.ReadBody(response,64,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                Assert(rejected,"Body size is bounded even without a trusted Content-Length");
            }
            bool ready=false;var started=Stopwatch.StartNew();var waiting=EngineRunner.WaitForReady(()=>true,()=>ready,CancellationToken.None,1000);
            await Task.Delay(150);Assert(!waiting.IsCompleted,"Probes wait for capture readiness rather than a fixed startup delay");ready=true;await waiting;
            Assert(started.ElapsedMilliseconds>=150,"Capture readiness releases pending probes");
            rejected=false;try{await EngineRunner.WaitForReady(()=>false,()=>true,CancellationToken.None);}catch(EngineStartException){rejected=true;}
            Assert(rejected,"Exited core cannot pass readiness even with an old ready flag");
            rejected=false;try{await EngineRunner.WaitForReady(()=>true,()=>false,CancellationToken.None,100);}catch(EngineStartException){rejected=true;}
            Assert(rejected,"A live core without capture readiness cannot start network probes");
            var partial=Enumerable.Range(0,ServiceChecks.Targets.Length).Select(i=>new CheckResult{State=i==4?0:1}).ToArray();
            var selected=await StrategySelector.Run(new[]{"api-only"},(name,token)=>Task.FromResult(0),()=>{},token=>Task.FromResult(partial),CancellationToken.None,null,null,true);
            Assert(selected.Winner==null,"Strategy with website and Gateway working but updater blocked is not auto-selected");
        }
        static async Task SelectionTests() {
            string running=null; int checks=0; var started=new System.Collections.Generic.List<string>();
            Func<int,long,CheckResult[]> results=(passed,ms)=>Enumerable.Range(0,ServiceChecks.Targets.Length).Select(i=>new CheckResult{State=i<passed?1:0,Detail="fixture",Milliseconds=ms}).ToArray();
            var selected=await StrategySelector.Run(new[]{"failed","slow","fast"},
                (name,token)=>{running=name;started.Add(name);return Task.FromResult(0);},()=>running=null,
                token=>{checks++;return Task.FromResult(results(running=="failed"?3:ServiceChecks.Targets.Length,running=="fast"?10:100));},CancellationToken.None,null,null);
            Assert(selected.Candidates.Count==3 && checks==4,"All candidates tested, winner retested");
            Assert(selected.Winner.Strategy=="fast" && selected.Winner.Confirmed && running=="fast","Fast confirmed winner stays running");
            running=null;int firstCalls=0;
            selected=await StrategySelector.Run(new[]{"first","second"},
                (name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(running=="first" && ++firstCalls>1?0:ServiceChecks.Targets.Length,running=="first"?5:10)),CancellationToken.None,null,null);
            Assert(selected.Winner.Strategy=="second" && selected.Candidates[0].Error.Length>0,"Failed confirmation advances to next complete strategy");
            using(var cancel=new CancellationTokenSource()) {
                bool cancelled=false;
                try {await StrategySelector.Run(new[]{"a","b"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                    token=>{cancel.Cancel();token.ThrowIfCancellationRequested();return Task.FromResult(results(ServiceChecks.Targets.Length,10));},cancel.Token,null,null);}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled && running==null,"Cancellation stops owned candidate");
            }
            selected=await StrategySelector.Run(new[]{"broken","partial"},(name,token)=>{if(name=="broken")throw new IOException("fixture error");running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(4,10)),CancellationToken.None,null,null);
            Assert(selected.Winner==null && selected.Candidates.Count==2 && running==null,"Start errors do not interrupt full scan and partial candidates are not auto-selected");
            int attempts=0;bool setupStopped=false;
            try{await StrategySelector.Run(new[]{"a","b","c"},(name,token)=>{attempts++;throw new EngineSetupException("fixture access denied");},()=>running=null,
                token=>{throw new Exception("Network checks must not run without a core");},CancellationToken.None,null,null);}catch(EngineSetupException){setupStopped=true;}
            Assert(setupStopped && attempts==1 && running==null,"Shared preparation failure stops scan after one attempt instead of failing every row");
            attempts=0;setupStopped=false;
            try{await StrategySelector.Run(new[]{"a","b"},(name,token)=>{if(++attempts==2)throw new EngineSetupException("fixture reconnect failure");running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(ServiceChecks.Targets.Length,10)),CancellationToken.None,null,null,true);}catch(EngineSetupException){setupStopped=true;}
            Assert(setupStopped && attempts==2 && running==null,"Preparation failure during winner confirmation also stops and cleans up");
            var dns=ServiceChecks.FromException(new System.Net.Http.HttpRequestException("outer",new System.Net.WebException("DNS",System.Net.WebExceptionStatus.NameResolutionFailure)),42);
            Assert(dns.Failure=="dns" && dns.Milliseconds==42,"Typed nested DNS error classified");
            Assert(ServiceChecks.FromException(new System.Net.Http.HttpRequestException("dns text only"),0).Failure=="connection","Error message alone does not imply DNS failure");
            Assert(ServiceChecks.FromException(new System.Security.Authentication.AuthenticationException("fixture"),0).Failure=="tls","TLS failure remains distinct");
            int quickChecks=0,confirmations=0;running=null;
            selected=await StrategySelector.Run(new[]{"partial","working","unneeded"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>{quickChecks++;return Task.FromResult(results(running=="partial"?3:ServiceChecks.Targets.Length,10));},CancellationToken.None,null,null,true,
                token=>{confirmations++;return Task.FromResult(results(ServiceChecks.Targets.Length,20));});
            Assert(selected.Winner.Strategy=="working" && selected.Candidates.Count==2 && quickChecks==2 && confirmations==1,"Quick mode stops after first full result and separate confirmation");
            running=null;int confirmCalls=0;
            selected=await StrategySelector.Run(new[]{"flaky","good"},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,
                token=>Task.FromResult(results(ServiceChecks.Targets.Length,10)),CancellationToken.None,null,null,true,
                token=>{confirmCalls++;return Task.FromResult(results(running=="flaky"?4:ServiceChecks.Targets.Length,20));});
            Assert(selected.Winner.Strategy=="good" && confirmCalls==2,"Quick mode continues after failed confirmation");
            string next="99.0.0";
            string json="{\"tag_name\":\"v"+next+"\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Obhod-"+next+"-win-x64.zip\",\"browser_download_url\":\"https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+next+"/Obhod-"+next+"-win-x64.zip\",\"digest\":\"sha256:"+new string('a',64)+"\",\"size\":100}]}";
            Assert(ParseFixture(json).Version==next,"Stable newer shell release accepted");
            string stable=json.Replace("Obhod-"+next+"-win-x64.zip","ByPass-win-x64.zip");
            Assert(ParseFixture(stable).Version==next,"Permanent download filename accepted for shell updates");
            Reject(()=>ParseFixture(stable.Replace("/download/v"+next+"/","/download/v0.0.1/")),"Permanent filename must still belong to the declared release tag");
            Reject(()=>ParseFixture(stable.Replace("https://github.com/","https://example.com/")),"Foreign permanent download URL rejected");
            Reject(()=>ParseFixture(json.Replace("sha256:","sha512:")),"Missing or wrong digest prevents shell update");
            Reject(()=>ParseFixture(json.Replace("https://github.com/","https://example.com/")),"Foreign update URL rejected");
            Assert(ParseFixture(json.Replace("\"prerelease\":false","\"prerelease\":true"))==null,"Prerelease excluded from automatic updates");
            Assert(ParseFixture(json.Replace(next,ShellUpdates.Current))==null,"Current version does not update itself");
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
