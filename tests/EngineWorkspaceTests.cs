using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretDesktop {
    static partial class Tests {
        static void HeaderLayoutTests(string root,string official){
            string folder=Path.Combine(root,"tests","fixture-header-"+Guid.NewGuid().ToString("N"));var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var previous=NetworkFootprint.TestRead;NetworkFootprint.TestRead=()=>{var result=new NetworkFootprint();result.Items.Add("Тест: внешняя служба WinDivert");result.Items.Add("Тест: внешний winws");return result;};
            try{
                var store=new Store(folder);Copy(official,store.Current);store.Settings.AutoAppUpdates=store.Settings.CheckUpdates=false;
                foreach(string backendName in new[]{"byedpi","zapret"}){
                    store.Settings.Backend=backendName;store.Save();using(var form=new MainForm(store,false)){
                        form.Show();Application.DoEvents();var choice=AllControls(form).Single(x=>x.Name=="BypassBackend");var stop=AllControls(form).Single(x=>x.Name=="StopAllBypasses");var main=AllControls(form).Single(x=>x.Name=="DiscordMessages");var state=(Control)typeof(MainForm).GetField("state",flags).GetValue(form);var subtitle=(Control)typeof(MainForm).GetField("subtitle",flags).GetValue(form);var header=choice.Parent;
                        Action<string> verify=phase=>{Assert(header.ClientRectangle.Contains(choice.Bounds) && header.ClientRectangle.Contains(stop.Bounds),backendName+" header actions fit on "+phase);Assert(!choice.Bounds.IntersectsWith(state.Bounds) && !choice.Bounds.IntersectsWith(subtitle.Bounds) && !stop.Bounds.IntersectsWith(main.Bounds),backendName+" header never puts the engine picker under its status text on "+phase);Assert(choice.Left>state.Left && stop.Left>main.Left && choice.Width>200*Theme.SystemScale,backendName+" header actions remain in the right column on "+phase);};
                        Capture(form,Path.Combine(root,"tests","window-header-"+backendName+"-first.png"));verify("first display before resize");
                        foreach(var size in new[]{new Size(940,690),new Size(1100,830),new Size(1400,960)}){form.ClientSize=new Size((int)(size.Width*Theme.SystemScale),(int)(size.Height*Theme.SystemScale));Application.DoEvents();verify(size.ToString());}
                        form.WindowState=FormWindowState.Maximized;Application.DoEvents();verify("maximized");Capture(form,Path.Combine(root,"tests","window-header-"+backendName+"-maximized.png"));form.WindowState=FormWindowState.Normal;Application.DoEvents();verify("restored");
                        typeof(MainForm).GetMethod("Exit",flags).Invoke(form,null);
                    }
                }
            }finally{NetworkFootprint.TestRead=previous;RemoveFixture(folder);}
        }
        static void ScopeTests(string root,string official){
            string folder=Path.Combine(root,"tests","fixture-scope-"+Guid.NewGuid().ToString("N"));
            try {
                Copy(official,folder);
                if(Updater.Version(folder)=="1.10.4"){
                    string archive=Path.Combine(root,"downloads","zapret-discord-youtube-1.10.4.zip"),hash=Trust.Hash(archive);long size=new FileInfo(archive).Length;
                    Assert(hash=="291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34","Scope 1.10.4 fixture uses the pinned official ZIP");
                    UpstreamTrust.ParseRelease(CoreMetadata("1.10.4",hash,size));File.Copy(archive,Path.Combine(folder,UpstreamTrust.ArchiveName));File.WriteAllText(Path.Combine(folder,UpstreamTrust.ReceiptName),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{version="1.10.4",archive_sha256=hash,archive_size=size}));
                }
                string baseline=Strategy.Arguments(folder,"general.bat");
                ServiceScope.Write(folder,new ScopeOptions());Trust.VerifyEngine(folder);
                foreach(string file in Directory.GetFiles(folder,"general*.bat")){
                    var args=Strategy.Split(Strategy.Arguments(folder,Path.GetFileName(file)));
                    Assert(!args.Any(x=>x.StartsWith("--ipset=")),"Scoped profile removes IPSet: "+file);
                    Assert(!args.Any(x=>x.StartsWith("--filter-l7=discord")),"Voice is opt-in: "+file);
                    Assert(!Strategy.NeedsTcpTimestamps(String.Join(" ",args)),"Scoped strategy never requests global timestamps: "+file);
                    Assert(!args.Any(x=>x.StartsWith("--hostlist=")),"Upstream general/Google/user domain lists do not broaden scope: "+file);
                    Assert(args.Any(x=>x.Contains("youtube.com")) && args.Any(x=>x.Contains("discord.com")),"Both selected services have profiles: "+file);
                }
                var onlyYoutube=new ScopeOptions{Discord=false};ServiceScope.Write(folder,onlyYoutube);string youtube=Strategy.Arguments(folder,"general.bat");
                Assert(!youtube.Contains("--hostlist-domains=discord.media") && !youtube.Contains("discord.com"),"YouTube-only excludes Discord domain profiles");
                Assert(ServiceScope.Allows("r1.googlevideo.com",onlyYoutube) && !ServiceScope.Allows("youtube.com.evil.example",onlyYoutube) && !ServiceScope.Allows("notyoutube.com",onlyYoutube) && !ServiceScope.Allows("fonts.googleapis.com",onlyYoutube),"Host matching respects suffix boundaries and excludes unrelated Google hosts");
                ServiceScope.Write(folder,new ScopeOptions{YouTube=false,Voice=true});var discord=Strategy.Arguments(folder,"general.bat");Assert(discord.Contains("--filter-l7=discord,stun") && !discord.Contains("youtube.com"),"Discord voice opt-in keeps only requested service scope");
                var inputs=EngineHost.ReadInputs(folder);Assert(inputs.ContainsKey(ServiceScope.RelativePath),"Scope crosses protected helper boundary");string snapshot=Path.Combine(root,"tests","fixture-scope-snapshot-"+Guid.NewGuid().ToString("N"));try{Directory.CreateDirectory(snapshot);EngineHost.WriteSnapshot(snapshot,inputs,false);Assert(ServiceScope.Read(snapshot).Voice,"Protected snapshot retains voice choice");}finally{RemoveFixture(snapshot);}
                string update=Path.Combine(folder,"update");Copy(official,update);Updater.Preserve(folder,update);Assert(ServiceScope.Read(update).Voice,"Core update preserves selected scope");Directory.Delete(update,true);
                ServiceScope.Write(folder,new ScopeOptions{Strict=false});Assert(Strategy.Arguments(folder,"general.bat")==baseline,"Explicit unrestricted mode restores original strategy arguments");
                Reject(()=>ServiceScope.Write(folder,new ScopeOptions{YouTube=false,Discord=false}),"Empty scope must be rejected");Reject(()=>ProxyBypass.Arguments("--daemon",12345,new ScopeOptions()),"Unknown proxy profile is rejected");
                foreach(string profile in ProxyBypass.Profiles){string args=ProxyBypass.Arguments(profile,12345,new ScopeOptions());Assert(args.Contains("--ip 127.0.0.1") && args.Contains("--no-udp") && args.Contains("--hosts") && !args.Contains("--daemon"),"ByeDPI remains local with a bounded host scope: "+profile);}
            }finally{RemoveFixture(folder);}
        }
        static void WorkspaceTests(string root,string official,bool native){
            string folder=Path.Combine(root,"tests","fixture-workspace-"+Guid.NewGuid().ToString("N"));var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            string registration=null;StartupRegistration.TestRead=()=>registration;StartupRegistration.TestWrite=value=>registration=value;StartupState startup=new StartupState();StartupService.TestRead=_=>startup;
            var previousFootprintReader=NetworkFootprint.TestRead;var inspected=new NetworkFootprint();NetworkFootprint.TestRead=()=>{var copy=new NetworkFootprint{Verified=inspected.Verified};copy.Items.AddRange(inspected.Items);return copy;};
            try{
                var store=new Store(folder);Copy(official,store.Current);store.Settings.CheckUpdates=store.Settings.AutoAppUpdates=false;store.Save();
                using(var form=new MainForm(store,false)){
                    form.Show();Application.DoEvents();var runner=(EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form);runner.ConflictCheck=()=>false;runner.TestStop=()=>{};runner.TestTelegram=(e,on)=>{};runner.TestStart=(e,s)=>{};
                    int releases=0;bool releaseFails=false;runner.TestRelease=()=>{releases++;if(releaseFails)throw new EngineStopException("Тест: драйвер используется другой программой.");};runner.TestStartupChange=(enabled,e,s)=>{startup=new StartupState();};
                    var backend=AllControls(form).OfType<DarkSelect>().Single(x=>x.Name=="BypassBackend");var telegram=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="TelegramMessages");var main=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="DiscordMessages");var tabs=(DarkTabs)typeof(MainForm).GetField("tabs",flags).GetValue(form);
                    Assert(telegram.Parent.Name=="TelegramConnectionCard" && !telegram.Visible,"Telegram is separate from the main engine header");
                    Action wait=()=>{var watch=Stopwatch.StartNew();while(((bool)typeof(MainForm).GetField("busy",flags).GetValue(form) || (bool)typeof(MainForm).GetField("starting",flags).GetValue(form) || ((BackendTransition)typeof(MainForm).GetField("backendTransition",flags).GetValue(form)).Active) && watch.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(10);}Assert(watch.ElapsedMilliseconds<10000,"Engine selection and its transition finish without blocking UI");};
                    Action<int> choose=index=>{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());backend.SelectedIndex=index;wait();};
                    Func<string,object[],Task> invoke=(name,args)=>{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());return (Task)typeof(MainForm).GetMethod(name,flags).Invoke(form,args);};
                    var dashboard=(Dashboard)typeof(MainForm).GetField("dashboard",flags).GetValue(form);dashboard.SetResult(0,new CheckResult{State=1,Detail="Тест: отдельный результат zapret",Milliseconds=17});dashboard.ScanSummary.Text="Тест: подбор zapret";
                    runner.TestStop=()=>Thread.Sleep(180);runner.Start(store.Current,"general.bat");releaseFails=true;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());backend.SelectedIndex=1;Assert(backend.SelectedIndex==1,"Requested selection stays visible while old engine stops asynchronously");int pulses=0;using(var heartbeat=new System.Windows.Forms.Timer{Interval=20}){heartbeat.Tick+=(s,e)=>pulses++;heartbeat.Start();wait();}Assert(pulses>=3,"Message loop remains responsive during owned-engine stop and transition");runner.TestStop=()=>{};Assert(store.Settings.Backend=="zapret" && backend.SelectedIndex==0,"Unconfirmed shutdown of an owned engine blocks switching");
                    dashboard.SetResult(0,new CheckResult{State=1,Detail="Тест: отдельный результат zapret",Milliseconds=17});dashboard.ScanSummary.Text="Тест: подбор zapret";releaseFails=false;choose(1);Assert(store.Settings.Backend=="byedpi" && !runner.Running && releases>=1,"Selecting another engine first stops the previous one and stays off");Assert(!telegram.Enabled && !AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="WindowsStartup").Enabled,"No-driver mode cannot silently enable Telegram or the system startup service");
                    Assert(dashboard.Statuses[0].Text=="Не проверено" && !dashboard.Statuses[5].Parent.Visible && !dashboard.Statuses[6].Parent.Visible,"A new backend gets empty results and no Telegram/GitHub rows");
                    var scopeDiscord=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="ScopeDiscord");string nativeScopeHash=Trust.Hash(Path.Combine(store.Current,ServiceScope.RelativePath));scopeDiscord.Checked=false;wait();Assert(!store.Settings.ProxyScopeDiscord && store.Settings.ScopeDiscord && Trust.Hash(Path.Combine(store.Current,ServiceScope.RelativePath))==nativeScopeHash,"ByeDPI service choices cannot change zapret settings or its engine scope file");
                    Assert(dashboard.Statuses[0].Parent.Visible && !dashboard.Statuses[2].Parent.Visible,"ByeDPI availability contains only its selected services");Capture(form,Path.Combine(root,"tests","window-engines-byedpi-youtube-only.png"));var proxyProfile=AllControls(form).OfType<DarkSelect>().Single(x=>x.Name=="ByeDpiProfile");dashboard.SetResult(0,new CheckResult{State=1,Detail="Тест: старый профиль",Milliseconds=11});proxyProfile.SelectedIndex=1;Assert(store.Settings.ProxyStrategy=="disorder" && dashboard.Statuses[0].Text=="Не проверено","Changing ByeDPI profile saves it and invalidates only its prior measurements");proxyProfile.SelectedIndex=0;
                    dashboard.SetResult(0,new CheckResult{State=0,Detail="Тест: отдельный результат ByeDPI",Milliseconds=29});dashboard.ResultContext="ByeDPI выключен · прямое соединение";choose(0);Assert(scopeDiscord.Checked && dashboard.Statuses[0].Tag.ToString().Contains("zapret") && dashboard.ScanSummary.Text=="Тест: подбор zapret" && dashboard.Summary.Text.Contains("Последняя проверка"),"Returning to zapret restores its own scope and marks cached measurements as historical");
                    string foreign=StartupRegistration.Command("C:\\test-only\\another-copy\\Obhod.exe");registration=foreign;choose(1);Assert(registration==foreign && !scopeDiscord.Checked && dashboard.Statuses[0].Tag.ToString().Contains("ByeDPI") && dashboard.Summary.Text.Contains("прямое соединение"),"Switching an idle preview preserves foreign startup and restores only ByeDPI data");
                    Pump(invoke("StopEverything",null));Assert(registration==foreign && !runner.Running,"Explicit full stop also leaves another copy's startup untouched without blocking");registration=null;
                    var proxyPane=AllControls(form).Single(x=>x.Name=="ProxyWorkspace");int visibilityChanges=0;proxyPane.VisibleChanged+=(s,e)=>visibilityChanges++;for(int i=0;i<8;i++)typeof(MainForm).GetMethod("RefreshState",flags).Invoke(form,null);Assert(visibilityChanges==0,"Periodic status updates never hide/re-show the selected workspace");
                    tabs.SelectedIndex=1;Application.DoEvents();Assert(!AllControls(form).Single(x=>x.Name=="StrictServiceScope").Visible && !AllControls(form).Single(x=>x.Name=="ScopeDiscordVoice").Visible && !((Control)typeof(MainForm).GetField("startupBackendCard",flags).GetValue(form)).Visible,"ByeDPI settings hide native filtering, UDP voice and service startup controls");tabs.SelectedIndex=2;Application.DoEvents();Assert(!((Control)typeof(MainForm).GetField("coreBackendCard",flags).GetValue(form)).Visible,"ByeDPI updates hide the unrelated zapret core card");tabs.SelectedIndex=4;Assert(tabs.SelectedIndex!=4,"Unavailable zapret advanced page cannot be selected in ByeDPI");tabs.SelectedIndex=0;scopeDiscord.Checked=true;wait();
                    foreach(var size in new[]{new Size(1100,830),new Size(940,690)}){form.ClientSize=new Size((int)(size.Width*Theme.SystemScale),(int)(size.Height*Theme.SystemScale));Application.DoEvents();var pane=AllControls(form).Single(x=>x.Name=="ProxyWorkspace");Assert(pane.Visible && pane.Parent.ClientRectangle.Contains(pane.Bounds),"Proxy workspace fits "+size);Capture(form,Path.Combine(root,"tests","window-engines-byedpi-"+size.Width+".png"));}
                    tabs.SelectedIndex=6;Application.DoEvents();Assert(telegram.Visible && telegram.Parent.ClientRectangle.Contains(telegram.Bounds),"Telegram has its own reachable page");Capture(form,Path.Combine(root,"tests","window-engines-telegram.png"));
                    tabs.SelectedIndex=1;Application.DoEvents();Capture(form,Path.Combine(root,"tests","window-engines-scope.png"));
                    inspected=new NetworkFootprint();inspected.Items.Add("Тест: служба WinDivert · Running · C:\\test-only\\WinDivert64.sys");Pump(invoke("InspectFootprint",null));Assert(((Label)typeof(MainForm).GetField("footprintText",flags).GetValue(form)).ForeColor==Theme.Warning,"Residual components are not reported as a clean shutdown");Capture(form,Path.Combine(root,"tests","window-engines-residual.png"));
                    inspected=new NetworkFootprint();startup=new StartupState{Installed=true,Owned=true,Running=true,Strategy=store.Settings.Strategy,Engine=store.Settings.Engine};registration=StartupRegistration.Command(Application.ExecutablePath);store.Settings.StartWithWindows=true;runner.RefreshStartup();Pump(invoke("StopEverything",null));Assert(!runner.Running && !runner.TelegramRunning && !startup.Owned && registration==null && !store.Settings.StartWithWindows,"Global stop removes an installed own service and logon startup as well as bypasses");
                    if(native){
                        string image=ProxyBypass.Image(folder);Directory.CreateDirectory(Path.GetDirectoryName(image));File.Copy(Path.Combine(root,"downloads","alternatives","byedpi-0.17.3","ciadpi.exe"),image);Assert(Trust.Hash(image)==ProxyBypass.ImageHash,"Native ByeDPI hash matches the pinned official artifact");
                        ServiceChecks.TestRun=(target,token)=>Task.FromResult(new CheckResult{State=1,Detail="Тестовая проба сервиса; настоящий локальный ByeDPI запущен",Milliseconds=1});
                        tabs.SelectedIndex=0;Pump(invoke("SetProxyEnabled",new object[]{true}));Assert(runner.ProxyRunning && main.Checked,"Real ByeDPI starts through the app without a helper or driver");Capture(form,Path.Combine(root,"tests","window-engines-byedpi-running.png"));
                        Pump(invoke("SetProxyEnabled",new object[]{false}));Assert(!runner.ProxyRunning && runner.IdleReleased,"Real ByeDPI fully stops through its switch: proxy="+runner.ProxyRunning+", released="+runner.IdleReleased+", notice="+String.Join(" | ",AllControls(form).OfType<ProblemNotice>().Select(x=>x.Message.Text)));ServiceChecks.TestRun=null;
                        foreach(string profile in ProxyBypass.Profiles)using(var proxy=new ProxyBypass()){
                            proxy.Start(folder,profile,new ScopeOptions(),null);int port=proxy.RawPort,front=proxy.Port,check=proxy.CheckPort;Assert(proxy.Running,"Real profile starts: "+profile);
                            var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();int destination=((IPEndPoint)echo.LocalEndpoint).Port;
                            var server=Task.Run(async()=>{using(var peer=await echo.AcceptTcpClientAsync()){var stream=peer.GetStream();int value=stream.ReadByte();stream.WriteByte((byte)value);}});
                            try{using(var client=ProxyBypass.Socks(port,IPAddress.Loopback,destination).GetAwaiter().GetResult()){client.ReceiveTimeout=2000;client.GetStream().WriteByte(71);Assert(client.GetStream().ReadByte()==71,"Real SOCKS exchange preserves payload: "+profile);}server.GetAwaiter().GetResult();}finally{echo.Stop();}
                            proxy.Stop();foreach(int stopped in new[]{port,front,check}){bool closed=false;try{using(var client=new TcpClient())client.Connect(IPAddress.Loopback,stopped);}catch(SocketException){closed=true;}Assert(closed,"Listener closed after stop: "+profile+"/"+stopped);}
                        }
                        using(var crash=new EngineRunner()){
                            crash.ConflictCheck=()=>false;crash.StartProxy(folder,"split",new ScopeOptions());var owned=(ProxyBypass)typeof(EngineRunner).GetField("proxy",flags).GetValue(crash);int port=owned.Port;
                            var child=(Process)typeof(ProxyBypass).GetField("process",flags).GetValue(owned);child.Kill();Assert(child.WaitForExit(3000),"Owned crash fixture exits");crash.RefreshStartup();
                            Assert(!owned.HasResources && !crash.Running && !String.IsNullOrEmpty(crash.StopWarning),"Unexpected engine exit closes its listeners and reports an unconfirmed session");bool closed=false;try{using(var client=new TcpClient())client.Connect(IPAddress.Loopback,port);}catch(SocketException){closed=true;}Assert(closed,"Crash cleanup does not leave the browser proxy listening");
                        }
                        File.AppendAllText(image,"tampered");bool rejected=false;try{ProxyBypass.Ensure(folder,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Changed proxy executable is refused before execution");
                    }
                    choose(0);Assert(store.Settings.Backend=="zapret" && !runner.Running,"Switching back leaves only the selected engine available, off");tabs.SelectedIndex=0;Capture(form,Path.Combine(root,"tests","window-engines-zapret-cached.png"));tabs.SelectedIndex=1;Capture(form,Path.Combine(root,"tests","window-engines-zapret-settings.png"));
                    typeof(MainForm).GetMethod("Exit",flags).Invoke(form,null);
                }
            }finally{ServiceChecks.TestRun=null;NetworkFootprint.TestRead=previousFootprintReader;StartupService.TestRead=null;StartupRegistration.TestRead=null;StartupRegistration.TestWrite=null;RemoveFixture(folder);}
        }
        static async Task ShellVersionTests(){
            var http=new MockHttp{Reply=url=>url.Contains("releases?")?Response(200,"[{\"tag_name\":\"v0.6.3\",\"draft\":false,\"prerelease\":false},{\"tag_name\":\"v0.5.0\",\"draft\":false,\"prerelease\":false},{\"tag_name\":\"v99.0.0\",\"draft\":false,\"prerelease\":true},{\"tag_name\":\"../../bad\",\"draft\":false,\"prerelease\":false}]"):url.Contains("v0.5.0/")?Response(200,Feed("0.5.0")):Response(404,"")};
            using(var session=new AppUpdateSession(new ShellUpdates(http))){
                http.Reply=url=>url.Contains("releases?")?Response(200,"["+String.Join(",",new[]{"0.6.3","0.5.0","0.4.0"}.Select(RawFeed))+"]"):url.Contains("v0.4.0/")?Response(404,""):url.EndsWith("build-proof.json")?Response(200,OwnerManifest(url.Contains("v0.5.0/")?"0.5.0":"0.6.3")):Response(200,Feed(url.Contains("v0.5.0/")?"0.5.0":"0.6.3"));
                await session.LoadVersions();Assert(session.Versions.SequenceEqual(new[]{"0.6.3","0.5.0"}),"Version picker hides releases without verified installation data");
                Assert(await session.SelectVersion("0.5.0") && session.Release.Version=="0.5.0" && session.ManualVersion,"Signed older release is selectable for explicit downgrade");
                Assert(session.ActionText=="Проверить обновления","Manual version selection does not turn the latest-update action into an older-version installer");
                Assert(!await session.SelectVersion("0.4.0") && session.Release==null && session.State==AppUpdateState.Failed,"Unsigned or missing historical manifest cannot be installed");
                using(var card=new AppUpdateCard()){card.Width=(int)(830*Theme.SystemScale);card.Render(session);Assert(card.ClientRectangle.Contains(card.Versions.Bounds) && card.ClientRectangle.Contains(card.InstallVersion.Bounds),"Version picker and install button fit the card at minimum window width");}
            }
            http=new MockHttp{Reply=url=>url.Contains("releases?")?Response(200,"["+RawFeed("0.6.3")+","+RawFeed("0.5.0")+"]"):url.Contains("v0.5.0/")?Response(404,""):url.EndsWith("build-proof.json")?Response(200,OwnerManifest("0.6.3")):Response(200,Feed("0.6.3"))};
            using(var session=new AppUpdateSession(new ShellUpdates(http)))using(var card=new AppUpdateCard()){
                await session.LoadVersions();card.Render(session);Assert(session.Versions.SequenceEqual(new[]{"0.6.3"}) && card.InstallVersion.Visible,"Only a published package with both verified proofs exposes installation");
                http.Reply=url=>url.Contains("releases?")?Response(200,"["+RawFeed("0.5.0")+"]"):url.EndsWith("build-proof.json")?Response(200,OwnerManifest("0.5.0")):Response(200,Feed("0.5.0"));await session.LoadVersions();card.Render(session);Assert(card.Versions.Items.Count==1 && card.Versions.Items[0].ToString()=="0.5.0","Refreshing an equally-sized version list still replaces removed releases");
                http.Reply=url=>url.Contains("releases?")?Response(200,"["+RawFeed("0.5.0")+"]"):url.EndsWith("build-proof.json")?Response(404,""):Response(200,Feed("0.5.0"));await session.LoadVersions();card.Render(session);Assert(session.Versions.Length==0 && card.Versions.Items.Count==0 && !card.InstallVersion.Visible,"Missing author build proof removes the version and all installation actions");
                http.Reply=url=>url.Contains("releases?")?Response(200,"["+RawFeed("0.6.3").Replace("\"size\":1","\"size\":2")+"]"):url.EndsWith("build-proof.json")?Response(200,OwnerManifest("0.6.3")):Response(200,Feed("0.6.3"));await session.LoadVersions();Assert(session.Versions.Length==0,"A removed or changed published package cannot be offered even with valid signed metadata");
            }
            string compatibility=Path.Combine(Environment.CurrentDirectory,"tests","fixture-shell-compat-"+Guid.NewGuid().ToString("N"));try{
                string engine=Path.Combine(compatibility,"core"),stage=Path.Combine(compatibility,"stage");Directory.CreateDirectory(stage);Directory.CreateDirectory(engine);File.WriteAllText(Path.Combine(engine,"version.txt"),"1.10.4");File.WriteAllText(Path.Combine(engine,"winws.exe"),"Тестовые неизменяемые байты ядра");ServiceScope.Write(engine,new ScopeOptions{Discord=false});string scope=Path.Combine(engine,ServiceScope.RelativePath),scopeHash=Trust.Hash(scope),coreHash=Trust.Hash(Path.Combine(engine,"winws.exe"));
                string modern=Path.Combine(stage,"modern.exe"),legacy=Path.Combine(stage,"old-metadata.exe");File.Copy(Application.ExecutablePath,modern);File.Copy(typeof(object).Assembly.Location,legacy);
                Assert(ShellUpdates.PrepareRuntimeCompatibility(modern,engine)==null && Trust.Hash(scope)==scopeHash,"A current shell preserves its scope settings in place");string backup=ShellUpdates.PrepareRuntimeCompatibility(legacy,engine);Assert(backup!=null && !File.Exists(scope) && Trust.Hash(backup)==scopeHash,"Legacy shell compatibility preserves unsupported shell scope as a backup");Assert(File.ReadAllText(Path.Combine(engine,"version.txt"))=="1.10.4" && Trust.Hash(Path.Combine(engine,"winws.exe"))==coreHash,"Shell downgrade compatibility never changes the installed core version or binary");ShellUpdates.RestoreRuntimeCompatibility(backup,engine);Assert(Trust.Hash(scope)==scopeHash,"A failed shell handoff restores the original scope exactly");
            }finally{RemoveFixture(compatibility);}
            http=new MockHttp{Reply=url=>Response(200,Feed("0.6.3"))};using(var updates=new ShellUpdates(http)){bool rejected=false;try{await updates.Exact("0.5.0");}catch(InvalidDataException){rejected=true;}Assert(rejected,"A signed manifest for another version is not accepted for the selected release");}
            http=new MockHttp{Reply=url=>{if(url.Contains("releases?"))return Response(200,"["+String.Join(",",(url.EndsWith("page=1")?Enumerable.Range(0,30).Select(i=>"0.5."+i):new[]{"0.4.3"}).Select(RawFeed))+"]");string version=System.Text.RegularExpressions.Regex.Match(url,@"/v(\d+\.\d+\.\d+)/").Groups[1].Value;return Response(200,url.EndsWith("build-proof.json")?OwnerManifest(version):Feed(version));}};
            using(var updates=new ShellUpdates(http)){var versions=await updates.Versions();Assert(versions.Length==31 && http.Urls.Count(x=>x.Contains("releases?"))==2 && versions.Last()=="0.4.3","History pagination includes verified older releases without an oversized metadata response");}
        }
        static string OwnerManifest(string version){return Sign(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{schema=1,kind="ByPass-build",owner=OwnerIdentity.Owner,version=version,shell_files=new{exe=new string('a',64),config=new string('a',64)}}));}
    }
}
