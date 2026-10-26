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
    static partial class Tests {
        static int count;
        static string Sign(string payload) {
            using(var key=new System.Security.Cryptography.RSACryptoServiceProvider()) {
                key.PersistKeyInCsp=false;key.FromXmlString(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture-signing","key.private.xml")));
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(payload);
                return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{signed_payload=Convert.ToBase64String(bytes),signature=Convert.ToBase64String(key.SignData(bytes,System.Security.Cryptography.CryptoConfig.MapNameToOID("SHA256")))});
            }
        }
        static void TelegramTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-telegram-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new Store(folder);Copy(official,store.Current);
                var baseline=Strategy.Arguments(store.Current,"general.bat");
                Assert(!TelegramFilter.Enabled(store.Current),"Telegram defaults off");
                TelegramFilter.Set(store.Current,true);Trust.VerifyEngine(store.Current);
                var inputs=EngineHost.ReadInputs(store.Current);Assert(inputs.ContainsKey(TelegramFilter.RelativePath),"Telegram flag crosses authenticated helper boundary");
                string snapshot=Path.Combine(folder,"snapshot");Directory.CreateDirectory(snapshot);EngineHost.WriteSnapshot(snapshot,inputs,false);
                foreach(string name in store.Strategies()) {
                    var tokens=Strategy.Split(Strategy.Arguments(store.Current,name));int first=tokens.FindIndex(x=>x.StartsWith("--filter-"));
                    Assert(tokens[first]=="--filter-tcp=80,443,5222" && tokens[first+1]=="--ipset-ip="+TelegramFilter.Networks,"Telegram profile precedes broad upstream filters: "+name);
                    Assert(tokens[first+2]=="--new","Telegram transport bypasses winws desync: "+name);
                    Assert(tokens.Any(x=>x.StartsWith("--wf-tcp=") && x.EndsWith(",80,443,5222")),"Capture includes Telegram ports: "+name);
                }
                string target=Path.Combine(folder,"target");Copy(official,target);Updater.Preserve(store.Current,target);Assert(TelegramFilter.Enabled(target),"Core update preserves Telegram mode");
                TelegramFilter.Set(store.Current,false);Updater.Preserve(store.Current,target);Assert(!TelegramFilter.Enabled(target),"Core update preserves disabled Telegram mode");
                EngineHost.WriteSnapshot(snapshot,EngineHost.ReadInputs(store.Current),true);Assert(!TelegramFilter.Enabled(snapshot),"Reused protected snapshot removes disabled Telegram flag");
                Assert(Strategy.Arguments(store.Current,"general.bat")==baseline,"Disabling Telegram exactly restores upstream arguments");
                store.Settings.CheckUpdates=store.Settings.AutoAppUpdates=false;store.Save();StartupService.TestRead=_=>new StartupState();
                Application.EnableVisualStyles();
                using(var form=new MainForm(store,false)) {
                    form.Show();Application.DoEvents();var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                    var runner=(EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form);runner.ConflictCheck=()=>false;
                    int starts=0,tgStarts=0;bool deny=false,denyBase=false,slowBase=false,slowTelegram=false;
                    runner.TestStart=(e,s)=>{if(slowBase)Thread.Sleep(600);if(denyBase){denyBase=false;throw new EngineStartException("Тестовый отказ Discord");}starts++;};runner.TestTelegram=(e,on)=>{if(slowTelegram)Thread.Sleep(600);if(on){if(deny){deny=false;throw new EngineStartException("Тестовый отказ Telegram");}tgStarts++;}};runner.TestStop=()=>{};runner.TestSync=(e,s)=>{};
                    var toggle=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="TelegramMessages");
                    Action<ToggleOption,bool> change=(control,value)=>{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());control.Checked=value;};
                    Action wait=()=>{var watch=Stopwatch.StartNew();while((bool)typeof(MainForm).GetField("starting",flags).GetValue(form) && watch.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(10);}Assert(!(bool)typeof(MainForm).GetField("starting",flags).GetValue(form),"Telegram change finishes asynchronously");};
                    var telegramTabs=(DarkTabs)typeof(MainForm).GetField("tabs",flags).GetValue(form);telegramTabs.SelectedIndex=6;
                    foreach(var size in new[]{new Size(1100,830),new Size(940,690)}){form.ClientSize=new Size((int)(size.Width*Theme.SystemScale),(int)(size.Height*Theme.SystemScale));Application.DoEvents();Assert(toggle.Visible && toggle.Parent.ClientRectangle.Contains(toggle.Bounds),"Telegram switch fits its separate page "+size);Capture(form,Path.Combine(root,"tests","window-telegram-off-"+size.Width+".png"));}telegramTabs.SelectedIndex=0;
                    form.ClientSize=new Size((int)(1100*Theme.SystemScale),(int)(830*Theme.SystemScale));SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=true;wait();
                    Assert(!runner.Running && runner.TelegramRunning && toggle.Checked && starts==0,"Telegram starts independently with the main engine off");Capture(form,Path.Combine(root,"tests","window-telegram-only.png"));
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=false;wait();Assert(!runner.Running && !runner.TelegramRunning && starts==0,"Telegram off does not start or stop the main engine");
                    deny=true;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=true;wait();Assert(!runner.TelegramRunning && !toggle.Checked && !TelegramFilter.Enabled(store.Current),"Failed Telegram start restores its own state only");Capture(form,Path.Combine(root,"tests","window-telegram-error.png"));
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=true;wait();int tgConnections=tgStarts;
                    var discord=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="DiscordMessages");
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());discord.Checked=true;wait();Assert(runner.Running && runner.TelegramRunning && tgStarts==tgConnections,"Base bypass starts without reconnecting Telegram");Capture(form,Path.Combine(root,"tests","window-telegram-on.png"));
                    var dashboard=(Dashboard)typeof(MainForm).GetField("dashboard",flags).GetValue(form);int telegramIndex=Array.FindIndex(ServiceChecks.Targets,x=>x.Kind==ProbeKind.Telegram);dashboard.SetResult(telegramIndex,new CheckResult{State=2,Informational=true,AvailableEndpoints=2,TotalEndpoints=5,Milliseconds=5853,Detail="Тест: доступны 2/5 DC; сообщения могут работать, часть DC недоступна."});
                    Assert(dashboard.Statuses[telegramIndex].Text=="Частично · 2/5 DC" && dashboard.Statuses[telegramIndex].ForeColor==Theme.Warning,"Partial Telegram status explains unavailable DCs and remains yellow");Capture(form,Path.Combine(root,"tests","window-telegram-partial.png"));
                    dashboard.SetResult(telegramIndex,new CheckResult{State=2,Informational=true,AvailableEndpoints=5,TotalEndpoints=5,MediaAvailableEndpoints=2,MediaTotalEndpoints=3,Detail="Тест: основные DC отвечают, CDN файлов недоступен."});
                    Assert(dashboard.Statuses[telegramIndex].Text=="DC 5/5 · файлы 2/3" && dashboard.Statuses[telegramIndex].ForeColor==Theme.Warning,"Telegram UI distinguishes file/CDN availability from healthy main DCs");Capture(form,Path.Combine(root,"tests","window-telegram-media-status.png"));
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());discord.Checked=false;wait();Assert(!runner.Running && runner.TelegramRunning && toggle.Checked && tgStarts==tgConnections,"Base bypass off keeps Telegram alive");
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());discord.Checked=true;wait();
                    int priorStarts=starts;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=false;wait();Assert(runner.Running && !runner.TelegramRunning && starts==priorStarts,"Telegram off does not reconnect Discord/YouTube");
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());toggle.Checked=true;wait();tgConnections=tgStarts;runner.Stop();runner.Start(store.Current,"general.bat");runner.Stop();Assert(runner.TelegramRunning && tgStarts==tgConnections,"Strategy candidate start/stop operations preserve Telegram session");
                    change(toggle,false);wait();
                    int releases=0;bool failRelease=false;runner.TestRelease=()=>{releases++;if(failRelease)throw new EngineStopException("Тест: WinDivert используется другой программой.");};
                    change(discord,true);wait();change(toggle,true);wait();change(discord,false);wait();
                    Assert(releases==0 && runner.TelegramRunning,"Explicit main stop retains the driver while Telegram needs it");
                    change(toggle,false);wait();Assert(releases==1 && runner.IdleReleased,"Last Telegram off releases idle components");
                    Capture(form,Path.Combine(root,"tests","window-driver-released.png"));
                    change(discord,true);wait();Assert(!runner.IdleReleased,"Restart clears previously confirmed unloaded state");
                    runner.Stop();Assert(releases==1,"Transient strategy stop does not release helper or driver");
                    runner.Start(store.Current,"general.bat");change(toggle,true);wait();change(toggle,false);wait();
                    Assert(releases==1 && runner.Running,"Telegram off retains the driver while Discord needs it");
                    failRelease=true;priorStarts=starts;change(discord,false);wait();
                    Assert(releases==2 && !runner.Running && !discord.Checked && starts==priorStarts && !runner.IdleReleased && !String.IsNullOrEmpty(runner.StopWarning),"Unload failure keeps bypass off, reports warning and never restarts it");
                    Assert(!DiscordFilter.Enabled(store.Current),"Unload failure preserves the explicitly disabled main mode");
                    Capture(form,Path.Combine(root,"tests","window-driver-release-warning.png"));
                    change(toggle,true);wait();int priorTelegramStarts=tgStarts;change(toggle,false);wait();
                    Assert(releases==3 && !runner.TelegramRunning && !toggle.Checked && tgStarts==priorTelegramStarts && !TelegramFilter.Enabled(store.Current),"Last Telegram unload failure also stays off without reconnecting");
                    failRelease=false;runner.TestRelease=null;
                    using(var heartbeat=new System.Windows.Forms.Timer{Interval=25}){
                        int beats=0;heartbeat.Tick+=(s,e)=>beats++;heartbeat.Start();
                        slowBase=true;var click=Stopwatch.StartNew();change(discord,true);
                        Assert(click.ElapsedMilliseconds<300 && toggle.Enabled,"Slow Discord start returns to UI and leaves Telegram switch usable");
                        change(toggle,true);Capture(form,Path.Combine(root,"tests","window-services-pending.png"));wait();slowBase=false;
                        Assert(beats>=3 && runner.Running && runner.TelegramRunning && discord.Checked && toggle.Checked,"Queued dual start keeps message loop responsive and enables both services");
                        Capture(form,Path.Combine(root,"tests","window-services-both.png"));
                        change(discord,false);wait();change(toggle,false);wait();
                        slowTelegram=true;beats=0;change(toggle,true);
                        Assert(discord.Enabled,"Slow Telegram start leaves Discord switch usable");change(discord,true);wait();slowTelegram=false;
                        Assert(beats>=3 && runner.Running && runner.TelegramRunning,"Reverse queued dual start also preserves both connections");
                        deny=true;change(toggle,false);wait();change(toggle,true);wait();
                        Assert(runner.Running && !runner.TelegramRunning && !toggle.Checked,"Failed Telegram start cannot stop already running Discord");
                        change(toggle,true);wait();change(discord,false);wait();
                        denyBase=true;change(discord,true);wait();Assert(!runner.Running && runner.TelegramRunning,"Failed Discord start cannot stop already running Telegram");
                        int uiThread=Thread.CurrentThread.ManagedThreadId,blockedStops=0;beats=0;
                        var selection=StrategySelector.Run(new[]{"fixture"},(name,token)=>Task.FromResult(0),()=>{if(Thread.CurrentThread.ManagedThreadId==uiThread)Interlocked.Increment(ref blockedStops);Thread.Sleep(120);},token=>Task.FromResult(new CheckResult[0]),CancellationToken.None,null,null);
                        var selectionWatch=Stopwatch.StartNew();while(!selection.IsCompleted && selectionWatch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}selection.GetAwaiter().GetResult();
                        Assert(blockedStops==0 && beats>=3 && runner.TelegramRunning,"Strategy stop and cleanup run off UI thread and preserve Telegram");
                        change(toggle,false);wait();int startedBeforeProbe=starts,tgBeforeProbe=tgStarts;var probed=new System.Collections.Generic.HashSet<string>();
                        ServiceChecks.TestRun=(probeTarget,token)=>{probed.Add(probeTarget.Name);return Task.FromResult(new CheckResult{State=1,Informational=probeTarget.Kind==ProbeKind.Telegram,AvailableEndpoints=5,TotalEndpoints=5,Detail="Тестовая сетевая проба без включения обхода",Milliseconds=1});};
                        try{
                            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());var manual=(Task)typeof(MainForm).GetMethod("RunChecks",flags).Invoke(form,null);Pump(manual);
                            Assert(probed.Count==5 && !runner.Running && !runner.TelegramRunning && starts==startedBeforeProbe && tgStarts==tgBeforeProbe,"Manual check probes only five selected engine targets without enabling a bypass");
                            Assert(dashboard.Statuses.Take(5).All(x=>x.Text!="Выключен"),"Manual check renders selected engine probe results");Capture(form,Path.Combine(root,"tests","window-services-probed-off.png"));
                        }finally{ServiceChecks.TestRun=null;}
                        change(toggle,true);wait();
                    }
                    bool external=true;runner.ConflictCheck=()=>external;typeof(MainForm).GetMethod("RefreshState",flags).Invoke(form,null);Assert(toggle.Enabled && !discord.Enabled,"External bypass does not disable independent Telegram controls");
                    Assert(!AllControls(form).OfType<Button>().Any(x=>x.Text=="Включить" || x.Text=="Выключить"),"Global connect/disconnect button removed");
                    var other=AllControls(form).OfType<Button>().Single(x=>x.Name=="StopExternalBypass");var tabs=(DarkTabs)typeof(MainForm).GetField("tabs",flags).GetValue(form);tabs.SelectedIndex=1;Application.DoEvents();Assert(other.Enabled && other.Parent.Name=="ExternalBypassCard","External bypass action is a button in Settings");Capture(form,Path.Combine(root,"tests","window-external-bypass.png"));
                    var relayOption=AllControls(form).OfType<ToggleOption>().Single(x=>x.Name=="TelegramRelay");Assert(!relayOption.Checked && !TelegramRelay.Enabled(store.Current),"External Telegram relay defaults off, requiring an explicit settings choice");
                    int baseBeforeRelay=starts,tgBeforeRelay=tgStarts;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());relayOption.Checked=true;
                    var relayWatch=Stopwatch.StartNew();while((bool)typeof(MainForm).GetField("busy",flags).GetValue(form) && relayWatch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}
                    Assert(relayOption.Checked && TelegramRelay.Enabled(store.Current) && runner.TelegramRunning && tgStarts==tgBeforeRelay+1 && starts==baseBeforeRelay,"Relay choice reconnects only Telegram and preserves base bypass state");
                    tabs.SelectedIndex=6;var settingsPage=tabs.TabPages[6];settingsPage.ScrollControlIntoView(relayOption.Parent);Application.DoEvents();Capture(form,Path.Combine(root,"tests","window-telegram-media-settings.png"));tabs.SelectedIndex=1;tabs.TabPages[1].ScrollControlIntoView(other.Parent);
                    runner.TestStopExternal=()=>{external=false;};SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());other.PerformClick();
                    var stopWatch=Stopwatch.StartNew();while((bool)typeof(MainForm).GetField("busy",flags).GetValue(form) && stopWatch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}Assert(!external && !other.Enabled && toggle.Enabled && runner.TelegramRunning,"Stopping external bypass leaves Telegram alive");
                    runner.ConflictCheck=()=>false;typeof(MainForm).GetMethod("Exit",flags).Invoke(form,null);
                }
            }finally{StartupService.TestRead=null;RemoveFixture(folder);}
        }
        static byte[] Hex(string value){return Enumerable.Range(0,value.Length/2).Select(i=>Convert.ToByte(value.Substring(i*2,2),16)).ToArray();}
        static async Task TelegramCancellationTests(){
            var failures=new System.Collections.Generic.List<string>();int failed=await TelegramWire.Warm(CancellationToken.None,failures.Add,(dc,token)=>Task.FromResult("DC"+dc+": fixture unavailable"));
            Assert(failed==0 && failures.Any(x=>x.Contains("основная стратегия продолжает работать")),"All unavailable Telegram WSS routes are diagnostic and cannot throw a core setup failure");
            using(var timeout=new CancellationTokenSource(80)){
                int stalled=await TelegramWire.Warm(timeout.Token,null,(dc,token)=>new TaskCompletionSource<string>().Task);
                Assert(stalled==0,"Unresponsive Telegram background probes obey deadline and do not fail core startup");
            }
            foreach(int dc in new[]{2,4})Assert(TelegramWire.Routes(dc,new[]{System.Net.IPAddress.Parse("149.154.167.99"),System.Net.IPAddress.Parse("149.154.167.99")}).Select(x=>x.ToString()).SequenceEqual(new[]{"149.154.167.220","149.154.167.99"}),"DC"+dc+" prefers proven alternate web endpoint before DNS, with duplicates removed");
            Assert(TelegramWire.Routes(1,new System.Net.IPAddress[0]).Length==0 && TelegramWire.Routes(2,new System.Net.IPAddress[0]).Length==1,"Only verified DC2/DC4 alternate routes survive a DNS failure");
            using(var deadline=new CancellationTokenSource(80)){
                var never=new TaskCompletionSource<int>();var watch=Stopwatch.StartNew();bool cancelled=false;
                try{await TelegramWire.Bounded(never.Task,deadline.Token);}catch(OperationCanceledException){cancelled=true;}
                Assert(cancelled && watch.ElapsedMilliseconds<2000,"Telegram deadline completes even when underlying I/O ignores cancellation");
                never.SetException(new IOException("late fixture failure"));
            }
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port,added=0,removed=0,registeredPort=0;
            TelegramWire.SocketPort=(value,add)=>{if(add){Interlocked.Increment(ref added);registeredPort=value;}else Interlocked.Increment(ref removed);};
            try{
                for(int i=0;i<32;i++)using(var cancelled=new CancellationTokenSource()){
                    cancelled.Cancel();bool correct=false;try{using(await TelegramWire.Dial(System.Net.IPAddress.Loopback,port,cancelled.Token)){}}catch(OperationCanceledException){correct=true;}
                    Assert(correct,"Cancelled Telegram connect reports cancellation without TcpClient null race "+i);
                }
                var accept=listener.AcceptTcpClientAsync();using(var stream=await TelegramWire.Dial(System.Net.IPAddress.Loopback,port,CancellationToken.None))using(var peer=await accept){
                    Assert(registeredPort==((System.Net.IPEndPoint)peer.Client.RemoteEndPoint).Port && added==removed+1,"Telegram source port is registered before connect and remains exempt while socket is alive");
                    byte[] reply=new byte[1];IAsyncResult waiting=stream.BeginRead(reply,0,1,null,null);
                    IAsyncResult sending=stream.BeginWrite(new byte[]{0x42},0,1,null,null);
                    Assert(sending.AsyncWaitHandle.WaitOne(2000),"Pending APM receive cannot block outbound Telegram send");stream.EndWrite(sending);
                    Assert((await ExactAsync(peer.GetStream(),1))[0]==0x42,"APM send reaches peer while receive is pending");await peer.GetStream().WriteAsync(new byte[]{0x24},0,1);
                    Assert(waiting.AsyncWaitHandle.WaitOne(2000) && stream.EndRead(waiting)==1 && reply[0]==0x24,"APM duplex response completes without deadlock");
                    byte[] payload=Enumerable.Range(0,65536).Select(i=>(byte)i).ToArray();var write=stream.WriteAsync(payload,0,payload.Length);var received=await ExactAsync(peer.GetStream(),payload.Length);await write;
                    Assert(received.SequenceEqual(payload),"Pinned Telegram socket preserves full binary payload");stream.Dispose();stream.Dispose();
                }
                Assert(added==removed,"Telegram cancellation and repeated disposal balance socket exemptions exactly once");
            }finally{TelegramWire.SocketPort=null;listener.Stop();}
        }
        static void TelegramTransportTests(string root,string official){
            Task.Run(()=>TelegramMediaChecks.Run(Assert)).GetAwaiter().GetResult();
            TelegramCancellationTests().GetAwaiter().GetResult();
            using(var ctr=new TelegramCtr(Hex("2b7e151628aed2a6abf7158809cf4f3c"),Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff")))Assert(ctr.Transform(Hex("6bc1bee22e409f96e93d7e117393172a")).SequenceEqual(Hex("874d6191b620e3261bef6864990db6ce")),"AES CTR matches independent NIST vector");
            foreach(int id in new[]{1,2,3,4,5,-2,-4}){byte[] init=TelegramWire.Random(64);for(int i=56;i<60;i++)init[i]=0xef;Array.Copy(BitConverter.GetBytes((short)id),0,init,60,2);byte[] header=(byte[])init.Clone();using(var ctr=TelegramWire.Outgoing(init)){byte[] encrypted=ctr.Transform(init);Array.Copy(encrypted,56,header,56,8);}bool media;Assert(TelegramWire.Dc(header,out media)==Math.Abs(id) && media==(id<0),"Direct MTProto obfuscation DC/media extracted "+id);}
            bool ignored;Reject(()=>TelegramWire.Dc(new byte[64],out ignored),"Non-MTProto stream is not routed to WSS");
            foreach(string ip in new[]{"149.154.167.51","91.108.4.5","2001:b28:f23d::a","::ffff:149.154.167.51"})Assert(TelegramFilter.Contains(System.Net.IPAddress.Parse(ip)),"Telegram CIDR includes "+ip);
            foreach(string ip in new[]{"127.0.0.1","10.0.0.1","8.8.8.8","2001:4860:4860::8888"})Assert(!TelegramFilter.Contains(System.Net.IPAddress.Parse(ip)),"Telegram CIDR excludes "+ip);
            TelegramRedirector.ValidateFilter(Path.Combine(official,"bin","WinDivert.dll"));Assert(true,"Real WinDivert filter compiles without opening driver");
            foreach(bool ipv6 in new[]{false,true}){
                int tcp=ipv6?40:20,src=ipv6?8:12,dst=ipv6?24:16,size=ipv6?16:4;byte[] packet=new byte[tcp+20];packet[0]=(byte)(ipv6?0x60:0x45);packet[ipv6?6:9]=6;packet[tcp+12]=0x50;packet[src]=10;packet[dst]=149;TelegramRedirector.PutPort(packet,tcp,55000);TelegramRedirector.PutPort(packet,tcp+2,443);
                byte[] address=new byte[80];address[8]=0;address[9]=0;address[10]=2;var routes=new System.Collections.Generic.Dictionary<int,int>{{49150,443}};
                var original=(byte[])packet.Clone();Assert(!TelegramRedirector.Reflect(packet,packet.Length,address,routes,p=>p==55000) && packet.SequenceEqual(original),"WSS upstream socket is exempt from redirect IPv6="+ipv6);
                Assert(TelegramRedirector.Reflect(packet,packet.Length,address,routes,p=>false) && TelegramRedirector.Port(packet,tcp+2)==49150 && packet[src]==149 && packet[dst]==10 && address[10]==0 && address[9]==0,"Telegram packet reflected inbound with correct WinDivert flags IPv6="+ipv6);
                TelegramRedirector.Swap(packet,src,dst,size);TelegramRedirector.PutPort(packet,tcp,49150);TelegramRedirector.PutPort(packet,tcp+2,55000);address[10]=2;
                Assert(TelegramRedirector.Reflect(packet,packet.Length,address,routes,p=>false) && TelegramRedirector.Port(packet,tcp)==443 && TelegramRedirector.Port(packet,tcp+2)==55000 && packet[src]==149 && packet[dst]==10,"Proxy response restores remote Telegram tuple IPv6="+ipv6);
            }
            var folder=Path.Combine(root,"tests","fixture-discord-"+Guid.NewGuid().ToString("N"));try{Copy(official,folder);var baseline=Strategy.Arguments(folder,"general.bat");DiscordFilter.Set(folder,false);TelegramFilter.Set(folder,true);
                foreach(string name in Directory.GetFiles(folder,"general*.bat").Select(Path.GetFileName)){var args=Strategy.Arguments(folder,name);Assert(!args.Contains("--filter-l7=discord") && !args.Contains("--hostlist-domains=discord.media") && args.Contains("--hostlist-exclude-domains="+DiscordFilter.Domains) && args.Contains("--ipset-ip="+TelegramFilter.Networks),"Discord off and Telegram on are independent: "+name);}
                var inputs=EngineHost.ReadInputs(folder);Assert(inputs.ContainsKey(DiscordFilter.RelativePath) && inputs.ContainsKey(TelegramFilter.RelativePath),"Both service settings cross helper boundary");
                TelegramRelay.Set(folder,true);Assert(EngineHost.ReadInputs(folder).ContainsKey(TelegramRelay.RelativePath),"Explicit relay permission crosses verified helper snapshot boundary");string relayTarget=folder+"-relay-copy";try{Copy(official,relayTarget);Updater.Preserve(folder,relayTarget);Assert(TelegramRelay.Enabled(relayTarget),"Core update preserves explicit relay choice");TelegramRelay.Set(folder,false);Updater.Preserve(folder,relayTarget);Assert(!TelegramRelay.Enabled(relayTarget),"Core update removes previously enabled relay when user opts out");}finally{RemoveFixture(relayTarget);}
                DiscordFilter.Set(folder,true);TelegramFilter.Set(folder,false);Assert(Strategy.Arguments(folder,"general.bat")==baseline,"Both default modes restore original upstream command");
            }finally{RemoveFixture(folder);}
            var skipped=Enumerable.Range(0,ServiceChecks.Targets.Length).Select(i=>new CheckResult{State=ServiceChecks.Targets[i].Kind==ProbeKind.Telegram?3:1}).ToArray();var candidate=new CandidateResult{Results=skipped};Assert(candidate.Complete && candidate.Total==ServiceChecks.Targets.Length-1,"Disabled Telegram is excluded from strategy selection criteria");
            var informational=Enumerable.Range(0,ServiceChecks.Targets.Length).Select(i=>new CheckResult{State=ServiceChecks.Targets[i].Kind==ProbeKind.Telegram?2:1,Informational=ServiceChecks.Targets[i].Kind==ProbeKind.Telegram,Milliseconds=ServiceChecks.Targets[i].Kind==ProbeKind.Telegram?12000:10}).ToArray();var independent=new CandidateResult{Results=informational};Assert(independent.Complete && independent.Passed==6 && independent.Total==6 && independent.Latency==10,"Independent partial Telegram cannot reject or slow-rank the main strategy");
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
        static void DnsReject(Action action,string message){bool rejected=false;try{action();}catch(Exception ex){if(!(ex is IOException) && !(ex is InvalidDataException) && !(ex is ArgumentException))throw;rejected=true;}Assert(rejected,message);}
        static string DnsFixture(string address="142.250.74.206",int ttl=180){return "{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"youtube-ui.l.google.com\",\"TTL\":10},{\"type\":1,\"data\":\""+address+"\",\"TTL\":"+ttl+"}]}";}
        static byte[] SocksRequest(string host,int port=443){byte[] name=System.Text.Encoding.ASCII.GetBytes(host);return new byte[]{5,1,0,3,(byte)name.Length}.Concat(name).Concat(new byte[]{(byte)(port/256),(byte)(port%256)}).ToArray();}
        static async Task<byte[]> ExactAsync(System.IO.Stream stream,int size){var bytes=new byte[size];int offset=0;while(offset<size){int count=await stream.ReadAsync(bytes,offset,size-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return bytes;}
        static async Task YouTubeDnsFallbackTests(string root){
            Func<CheckResult> dnsError=()=>new CheckResult{Failure="dns",Detail="Тест: Windows NXDOMAIN"};
            foreach(var target in ServiceChecks.Targets.Take(2)){
                int retries=0;
                var result=await ServiceChecks.RunWithDnsFallback(target,CancellationToken.None,2,_=>Task.FromResult(dnsError()),async cancel=>{retries++;return await ServiceChecks.Run(target,cancel,2,new ProbeHttp{Reply=(request,token)=>Task.FromResult(Response(200,"fixture"))},(uri,token)=>Task.FromResult(0));});
                Assert(result.State==1 && result.DnsRoute=="doh" && result.Failure=="" && retries==1 && result.SystemDnsFailure.Contains("NXDOMAIN"),"YouTube DNS failure retries actual HTTP probe and preserves Windows failure: "+target.Name);
            }
            foreach(string failure in new[]{"","tls","http","timeout","cancelled","connection"}){
                int retries=0;var primary=new CheckResult{State=failure==""?1:0,Failure=failure,Detail="primary"};
                var result=await ServiceChecks.RunWithDnsFallback(ServiceChecks.Targets[0],CancellationToken.None,2,_=>Task.FromResult(primary),_=>{retries++;return Task.FromResult(new CheckResult{State=1});});
                Assert(Object.ReferenceEquals(primary,result) && retries==0,"DoH does not hide successful requests or non-DNS errors: "+failure);
            }
            foreach(var target in ServiceChecks.Targets.Skip(2)){
                int retries=0;var result=await ServiceChecks.RunWithDnsFallback(target,CancellationToken.None,2,_=>Task.FromResult(dnsError()),_=>{retries++;return Task.FromResult(new CheckResult{State=1});});
                Assert(result.Failure=="dns" && retries==0,"YouTube-only fallback does not change other probes: "+target.Name);
            }
            foreach(int status in new[]{403,503}){
                var result=await ServiceChecks.RunWithDnsFallback(ServiceChecks.Targets[0],CancellationToken.None,2,_=>Task.FromResult(dnsError()),cancel=>ServiceChecks.Run(ServiceChecks.Targets[0],cancel,2,new ProbeHttp{Reply=(request,token)=>Task.FromResult(Response(status,"blocked"))},(uri,token)=>Task.FromResult(0)));
                Assert(result.State!=1 && result.Failure=="http" && result.DnsRoute=="doh" && result.SystemDnsFailure.Length>0,"Working DNS alone cannot turn failing YouTube HTTP response green: "+status);
            }
            using(var cancelled=new CancellationTokenSource()){
                var result=await ServiceChecks.RunWithDnsFallback(ServiceChecks.Targets[0],cancelled.Token,2,_=>Task.FromResult(dnsError()),_=>{cancelled.Cancel();return Task.FromResult(new CheckResult{State=1,Detail="late success"});});
                Assert(result.State==0 && result.Failure=="cancelled","Cancellation overrides a late DoH success");
            }
            var timed=await ServiceChecks.RunWithDnsFallback(ServiceChecks.Targets[0],CancellationToken.None,1,_=>Task.FromResult(dnsError()),async cancel=>{try{await Task.Delay(3000,cancel);}catch(OperationCanceledException){}return new CheckResult{State=1};});
            Assert(timed.State==0 && timed.Failure=="timeout" && timed.Milliseconds<2500,"Both DNS attempts share one deadline and cannot return late green status");
            foreach(string request in new[]{"GET https://www.youtube.com/ HTTP/1.1\r\n\r\n","CONNECT www.youtube.com:80 HTTP/1.1\r\n\r\n","CONNECT youtube.com.evil.example:443 HTTP/1.1\r\n\r\n","CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n","CONNECT github.com:443 HTTP/1.1\r\n\r\n"})
                using(var stream=new MemoryStream(System.Text.Encoding.ASCII.GetBytes(request)))DnsReject(()=>YouTubeDnsProxy.HttpDestination(stream),"HTTPS check relay rejects non-YouTube destination or unsafe protocol");
            using(var stream=new MemoryStream(System.Text.Encoding.ASCII.GetBytes("CONNECT WWW.YOUTUBE.COM:443 HTTP/1.1\r\nHost: www.youtube.com:443\r\n\r\n")))Assert(YouTubeDnsProxy.HttpDestination(stream)=="www.youtube.com","HTTP CONNECT accepts and normalizes exact YouTube hostname");
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;int resolved=0;
            try{
                using(var proxy=new YouTubeDnsProxy((host,token)=>{resolved++;Assert(host=="www.youtube.com","Real CONNECT resolves original YouTube hostname through DoH delegate");return Task.FromResult(new DnsAnswer{Addresses=new[]{System.Net.IPAddress.Parse("8.8.8.8")}});},async(ip,destinationPort)=>{Assert(ip.ToString()=="8.8.8.8" && destinationPort==443,"Real CONNECT connects to validated DNS answer on HTTPS port");var peer=new System.Net.Sockets.TcpClient();await peer.ConnectAsync(System.Net.IPAddress.Loopback,port);return peer;},httpConnect:true)){
                    var echo=Task.Run(async()=>{using(var peer=await listener.AcceptTcpClientAsync()){var stream=peer.GetStream();var payload=await ExactAsync(stream,4);await stream.WriteAsync(payload,0,payload.Length);}});
                    using(var client=new System.Net.Sockets.TcpClient()){
                        await client.ConnectAsync(System.Net.IPAddress.Loopback,proxy.Port);var stream=client.GetStream();var request=System.Text.Encoding.ASCII.GetBytes("CONNECT www.youtube.com:443 HTTP/1.1\r\nHost: www.youtube.com:443\r\n\r\n");await stream.WriteAsync(request,0,request.Length);
                        var header=await ExactAsync(stream,39);Assert(System.Text.Encoding.ASCII.GetString(header)=="HTTP/1.1 200 Connection Established\r\n\r\n","Real HTTP CONNECT handshake opens DoH tunnel");
                        var tls=new byte[]{22,3,3,42};await stream.WriteAsync(tls,0,tls.Length);Assert((await ExactAsync(stream,4)).SequenceEqual(tls),"HTTP relay forwards TLS bytes unchanged without replacing certificates");
                    }await echo;Assert(resolved==1,"HTTP relay uses supplied DNS resolver exactly once");
                }
            }finally{listener.Stop();}
            using(var dashboard=new Dashboard{Size=new Size(1000,550)}){
                dashboard.SetResult(0,new CheckResult{State=1,DnsRoute="doh",Detail="Тестовый HTTPS-запрос успешен; DNS Windows: NXDOMAIN"});
                Assert(dashboard.Statuses[0].Text=="Доступен через DoH" && dashboard.Statuses[0].AccessibleDescription.Contains("NXDOMAIN"),"Dashboard labels fallback explicitly and keeps Windows DNS error in details");
            }
        }
        static async Task YouTubeDnsTests(string root) {
            var answer=DnsOverHttps.Parse(DnsFixture());Assert(answer.Addresses.Single().ToString()=="142.250.74.206" && answer.Ttl==180,"DoH parser requires an actual public A answer and retains bounded TTL");
            DnsReject(()=>DnsOverHttps.Parse("{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"alias.example\"}]}"),"CNAME-only response cannot falsely report successful DNS");
            DnsReject(()=>DnsOverHttps.Parse("{\"Status\":3,\"Answer\":[{\"type\":1,\"data\":\"8.8.8.8\"}]}"),"DNS error status cannot be used as successful resolution");
            foreach(string ip in new[]{"0.0.0.0","10.0.0.1","127.0.0.1","169.254.1.1","172.16.0.1","192.168.0.1","100.64.0.1","224.0.0.1","198.18.0.1","192.0.2.1","198.51.100.1","203.0.113.1","::1","::ffff:127.0.0.1"})DnsReject(()=>DnsOverHttps.Parse(DnsFixture(ip)),"DoH cannot redirect browser to local or reserved address "+ip);
            Assert(DnsOverHttps.Parse(DnsFixture("8.8.8.8",99999)).Ttl==300 && DnsOverHttps.Parse(DnsFixture("8.8.8.8",0)).Ttl==0,"DNS caching never exceeds TTL or five minutes");
            Assert(DnsOverHttps.Host("ПРИМЕР.рф")=="xn--e1afmkfd.xn--p1ai","DoH names normalize IDN without shell interpolation");
            foreach(string host in new[]{"localhost","127.0.0.1","www.youtube.com/evil","www.youtube.com\r\nproxy","youtube.com --no-sandbox"})DnsReject(()=>DnsOverHttps.Host(host),"Invalid DNS destination rejected: "+host);
            string profile=Path.Combine(root,"tests","fixture-browser profile");var arguments=Strategy.Split(YouTubeDnsSession.Arguments(profile,12345));
            Assert(arguments.Contains("--user-data-dir="+profile) && arguments.Contains("--proxy-server=socks5://127.0.0.1:12345") && arguments.Last()=="https://www.youtube.com/","Separate browser receives exact isolated profile, loopback endpoint and fixed URL");
            Assert(!arguments.Any(x=>x.Contains("ignore-certificate") || x=="--no-sandbox") && arguments.Contains("--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1") && arguments.Contains("--proxy-bypass-list=<-loopback>"),"Browser keeps TLS validation and sandbox and does not resolve destinations via Windows DNS");
            using(var request=new MemoryStream(SocksRequest("www.youtube.com"))){int port;Assert(YouTubeDnsProxy.Destination(request,out port)=="www.youtube.com" && port==443,"SOCKS destination stays a DNS name until DoH resolution");}
            using(var request=new MemoryStream(SocksRequest("www.youtube.com",22)))DnsReject(()=>{int port;YouTubeDnsProxy.Destination(request,out port);},"Local relay refuses non-web ports");
            using(var request=new MemoryStream(new byte[]{5,3,0,1,127,0,0,1,0,80}))DnsReject(()=>{int port;YouTubeDnsProxy.Destination(request,out port);},"Local relay refuses UDP association and literal-IP requests");
            string resolved=null;int resolutions=0,connections=0;
            var target=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);target.Start();int targetPort=((System.Net.IPEndPoint)target.LocalEndpoint).Port;
            try {
                using(var proxy=new YouTubeDnsProxy((host,token)=>{resolutions++;resolved=host;return Task.FromResult(DnsOverHttps.Parse(DnsFixture("8.8.8.8")));},async(ip,port)=>{connections++;Assert(ip.ToString()=="8.8.8.8" && port==443,"Tunnel connects only to the exact public DoH answer");var remote=new System.Net.Sockets.TcpClient();await remote.ConnectAsync(System.Net.IPAddress.Loopback,targetPort);return remote;})) {
                    Assert(proxy.Address.Equals(System.Net.IPAddress.Loopback),"Browser relay listens on loopback only");
                    var echo=Task.Run(async()=>{using(var peer=await target.AcceptTcpClientAsync()){var stream=peer.GetStream();byte[] data=await ExactAsync(stream,4);await stream.WriteAsync(data,0,data.Length);}});
                    using(var browser=new System.Net.Sockets.TcpClient()) {
                        await browser.ConnectAsync(System.Net.IPAddress.Loopback,proxy.Port);var stream=browser.GetStream();await stream.WriteAsync(new byte[]{5,1,0},0,3);Assert((await ExactAsync(stream,2)).SequenceEqual(new byte[]{5,0}),"Real socket negotiates browser SOCKS5 handshake");
                        byte[] request=SocksRequest("www.youtube.com");await stream.WriteAsync(request,0,request.Length);Assert((await ExactAsync(stream,10))[1]==0,"Browser receives success after real tunnel connection");
                        byte[] payload=new byte[]{22,3,3,42};await stream.WriteAsync(payload,0,payload.Length);Assert((await ExactAsync(stream,4)).SequenceEqual(payload),"Tunnel passes TLS bytes unchanged without decrypting or replacing certificates");
                    }
                    await echo;Assert(resolutions==1 && connections==1 && resolved=="www.youtube.com","Tunnel uses explicit DoH resolver instead of system destination DNS");
                }
                int blockedConnections=0;
                using(var proxy=new YouTubeDnsProxy((host,token)=>Task.FromResult(new DnsAnswer{Addresses=new[]{System.Net.IPAddress.Loopback}}),(ip,port)=>{blockedConnections++;return Task.FromResult(new System.Net.Sockets.TcpClient());}))
                using(var browser=new System.Net.Sockets.TcpClient()) {
                    await browser.ConnectAsync(System.Net.IPAddress.Loopback,proxy.Port);var stream=browser.GetStream();await stream.WriteAsync(new byte[]{5,1,0},0,3);await ExactAsync(stream,2);var request=SocksRequest("youtube.com");await stream.WriteAsync(request,0,request.Length);Assert((await ExactAsync(stream,10))[1]!=0 && blockedConnections==0,"Private DNS rebinding is rejected before any outbound connection");
                }
            }finally{target.Stop();}
        }
        static void YouTubeDnsUiTests(string root) {
            int launches=0;bool fail=false;
            using(var dialog=new DnsDiagnosticDialog("DNS Windows: имя не найдено\r\nGoogle DNS через HTTPS: имя найдено\r\nТестовый отчёт; системные настройки не менялись.",()=>{launches++;if(fail)throw new IOException("Тестовый DNS недоступен");return Task.FromResult(0);},_=>{})) {
                dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-2500,-2500);dialog.Show();Application.DoEvents();
                dialog.OpenYouTube.PerformClick();Assert(launches==1 && dialog.Result.Text.Contains("не изменены") && dialog.OpenYouTube.Enabled,"YouTube action reports isolated window without claiming Windows DNS was repaired");
                Capture(dialog,Path.Combine(root,"tests","window-dns-youtube.png"));
                fail=true;dialog.OpenYouTube.PerformClick();Assert(launches==2 && dialog.Result.Text.Contains("Настройки Windows не изменены") && dialog.OpenYouTube.Enabled,"Failed browser attempt stays retryable and reports no system mutation");
                Capture(dialog,Path.Combine(root,"tests","window-dns-youtube-error.png"));
                dialog.Size=new Size(600,480);dialog.PerformLayout();Application.DoEvents();Assert(dialog.OpenYouTube.Right<=dialog.ClientSize.Width && dialog.Result.Bottom<=dialog.ClientSize.Height,"DNS and YouTube actions fit compact dialog");
            }
        }
        static void SettingsAndHelpTests(MainForm form,Store store,string root){
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;var tabs=FindTabs(form);var oldSize=form.Size;
            var help=AllControls(form).OfType<HelpGuide>().Single();
            foreach(string topic in new[]{"browser","windows"}){
                string requested=null;using(var dialog=new DnsDiagnosticDialog("Тестовый отчёт DNS",()=>Task.FromResult(0),value=>requested=value)){
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-2500,-2500);dialog.Show();Application.DoEvents();
                    AllControls(dialog).OfType<Button>().Single(b=>b.Name==(topic=="browser"?"DnsHelpBrowser":"DnsHelpWindows")).PerformClick();
                    Assert(requested==topic && !dialog.Visible,"DNS help button closes dialog and selects exact guide: "+topic);
                }
                typeof(MainForm).GetMethod("ShowHelp",flags).Invoke(form,new object[]{requested});Application.DoEvents();
                Assert(tabs.SelectedIndex==5 && help.Topic==topic,"DNS navigation opens Help with the requested guide: "+topic);
                Capture(form,Path.Combine(root,"tests","window-help-"+topic+".png"));
            }
            Assert(!AllControls(help).OfType<Label>().Any(l=>l.Text.Contains("Крестик") || l.Text.Contains("IP и страна") || l.Text.Contains("Настройки игр")),"Help omits removed reference text and keeps task instructions");
            foreach(var preset in Theme.Presets){
                tabs.SelectedIndex=1;Application.DoEvents();AllControls(form).OfType<Button>().Single(b=>b.Name=="Theme-"+preset.Id).PerformClick();Application.DoEvents();
                Assert(Theme.Current==preset.Id && form.BackColor==preset.Background && new Store(store.Root).Settings.VisualTheme==preset.Id,"Theme applies immediately and survives settings reload: "+preset.Id);
                foreach(var size in new[]{new Size(940,690),new Size(1100,830)}){
                    form.Size=size;form.PerformLayout();Application.DoEvents();
                    var choices=AllControls(form).OfType<ThemeChoice>().ToArray();Assert(choices.All(c=>c.Right<=c.Parent.ClientSize.Width && c.Bottom<=c.Parent.ClientSize.Height) && !choices[0].Bounds.IntersectsWith(choices[1].Bounds),"Theme cards fit Settings at "+size);
                    if(size.Width==1100)Capture(form,Path.Combine(root,"tests","window-theme-"+preset.Id+".png"));
                }
            }
            foreach(string topic in new[]{"start","browser","windows"}){
                typeof(MainForm).GetMethod("ShowHelp",flags).Invoke(form,new object[]{topic});form.Size=new Size(940,690);Application.DoEvents();
                Assert(AllControls(help).Where(c=>c is Label && c.Parent is SurfacePanel).All(c=>c.Right<=c.Parent.Width && c.Bottom<=c.Parent.Height),"Guide content fits compact window: "+topic);
            }
            Assert(Theme.Palette("unknown").Id=="graphite" && Theme.Palette(null).Id=="graphite","Unknown or missing saved theme falls back safely");
            tabs.SelectedIndex=1;AllControls(form).OfType<Button>().Single(b=>b.Name=="Theme-graphite").PerformClick();
            using(var restored=new MainForm(new Store(store.Root),false)){Assert(restored.BackColor==Theme.Background,"Reopened window uses persisted palette");}
            tabs.SelectedIndex=4;AllControls(form).OfType<Button>().Single(b=>b.Name=="AdvancedGames").PerformClick();Application.DoEvents();
            Assert(AllControls(form).OfType<DarkSelect>().Single(c=>c.Items.Contains("Только TCP")).Visible,"Games are reachable in Advanced");Capture(form,Path.Combine(root,"tests","window-advanced-games.png"));
            AllControls(form).OfType<Button>().Single(b=>b.Name=="AdvancedSites").PerformClick();Application.DoEvents();Assert(AllControls(form).OfType<DomainEditor>().Single().Visible,"Site lists remain reachable in Advanced");
            form.Size=oldSize;tabs.SelectedIndex=0;
        }
        static void StartupTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-startup-"+Guid.NewGuid().ToString("N"));
            string snapshot=Path.Combine(folder,"startup-"+Guid.NewGuid().ToString("N"));Copy(official,snapshot);
            try {
                string image=StartupService.Command(snapshot,"general.bat");
                var parts=Strategy.Split(image);
                Assert(parts[0]==Path.Combine(snapshot,"bin","winws.exe") && parts.Skip(1).All(x=>x.StartsWith("--")),"Service command quotes executable and contains parsed upstream arguments");
                var state=new StartupState{Snapshot=snapshot,Image=image,Strategy="general.bat"};
                StartupService.ValidateRecord(state,folder,image);Assert(true,"Service record accepts only trusted protected snapshot and exact command");
                string legacy="\""+Path.Combine(snapshot,"bin","winws.exe")+"\" "+Strategy.Arguments(snapshot,"general.bat",false);
                state.Image=legacy;StartupService.ValidateRecord(state,folder,legacy);
                Assert(image!=legacy,"Pre-Telegram startup command accepted for migration without weakening current command format");
                state.Image=legacy+" --evil";Reject(()=>StartupService.ValidateRecord(state,folder,state.Image),"Legacy migration rejects extra arbitrary arguments even when record and registry agree");
                state.Image=image+" --evil";Reject(()=>StartupService.ValidateRecord(state,folder,state.Image),"Current startup command rejects extra arbitrary arguments");state.Image=image;
                Reject(()=>StartupService.ValidateRecord(state,Path.Combine(folder,"other"),image),"Service snapshot cannot escape protected root");
                Reject(()=>StartupService.ValidateRecord(state,folder,image+" --evil"),"Changed service binary path cannot be adopted");
                state.Strategy="../general.bat";Reject(()=>StartupService.ValidateRecord(state,folder,image),"Service strategy path traversal rejected");state.Strategy="general.bat";
                File.AppendAllText(Path.Combine(snapshot,"bin","winws.exe"),"tamper");Reject(()=>StartupService.ValidateRecord(state,folder,image),"Service mutation rejects modified SYSTEM executable");
                Assert(StartupService.SameInstallation(folder,folder.ToUpperInvariant()+"\\") && !StartupService.SameInstallation(folder,folder+"-other"),"Service ownership uses exact normalized installation path");
                string exe=Path.Combine(folder,"Папка с пробелами","ByPass.exe");var startupArgs=Strategy.Split(StartupRegistration.Command(exe));
                Assert(startupArgs.SequenceEqual(new[]{exe,"--tray"}),"User logon opens GUI only in tray without reconnect or elevation flags");
                string value=null;StartupRegistration.TestRead=()=>value;StartupRegistration.TestWrite=next=>value=next;
                StartupRegistration.Set(true,exe);Assert(value==StartupRegistration.Command(exe),"Tray registration stores exact executable and tray argument");
                bool conflict=false;try{StartupRegistration.Set(false,exe+".other");}catch(InvalidOperationException){conflict=true;}
                Assert(conflict && value==StartupRegistration.Command(exe),"Tray removal cannot overwrite another installation");
                StartupRegistration.Set(false,exe);Assert(value==null,"Disabling startup removes owned tray entry");
            }finally{StartupRegistration.TestRead=null;StartupRegistration.TestWrite=null;RemoveFixture(folder);}
        }
        static void StartupUiTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-startup-ui-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new Store(folder);store.Settings.CheckUpdates=false;store.Settings.AutoAppUpdates=false;store.Save();Copy(official,store.Current);
                Trust.VerifyEngine(store.Current);
                using(var form=new MainForm(store,false)) {
                    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2500,-2500);form.Show();Application.DoEvents();
                    StartupUiScenario(form,store,root);
                }
            }finally{RemoveFixture(folder);}
        }
        static void StartupUiScenario(MainForm form,Store store,string root) {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var runner=(EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form);
            var checkbox=AllControls(form).OfType<CheckBox>().Single(x=>x.Text=="Запускать при старте Windows");
            var state=new StartupState();string registration=null;bool denied=false;int changes=0;var oldConflict=runner.ConflictCheck;runner.ConflictCheck=()=>false;AllControls(form).OfType<DomainEditor>().Single().Discard();
            int reads=0;StartupService.TestRead=_=>{reads++;return state;};StartupRegistration.TestRead=()=>registration;StartupRegistration.TestWrite=value=>registration=value;
            var acknowledge=new ManualResetEventSlim(false);bool delay=true;
            runner.TestStartupChange=(enabled,engine,strategy)=>{changes++;if(delay && !acknowledge.Wait(5000))throw new TimeoutException("test acknowledgement");if(denied)throw new System.ComponentModel.Win32Exception(1223);state=new StartupState{Installed=enabled,Owned=enabled,Running=false,Engine=Path.GetFileName(engine),Strategy=strategy};};
            Action refresh=()=>typeof(MainForm).GetMethod("RefreshState",flags).Invoke(form,null);
            Action wait=()=>{var watch=Stopwatch.StartNew();while((bool)typeof(MainForm).GetField("busy",flags).GetValue(form) && watch.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(10);}Assert(!(bool)typeof(MainForm).GetField("busy",flags).GetValue(form),"Startup UI operation completes without blocking window");};
            try {
                FindTabs(form).SelectedIndex=1;refresh();Application.DoEvents();
                Assert(!checkbox.Checked && checkbox.Enabled,"Startup checkbox defaults off based on SCM, not old JSON");
                Assert(checkbox is ToggleOption && AllControls(FindTabs(form).TabPages[1]).Contains(checkbox) && !AllControls(FindTabs(form).TabPages[1]).Contains(AllControls(form).OfType<Label>().Single(x=>x.Text=="Игровые соединения")),"Styled startup switch lives in Settings, games live separately in Advanced");
                int initialReads=reads;for(int i=0;i<10;i++)refresh();Assert(reads==initialReads,"UI refresh never polls Windows service synchronously");
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());checkbox.Checked=true;
                try{refresh();Application.DoEvents();Assert(checkbox.Checked && !checkbox.Enabled && !store.Settings.StartWithWindows,"Pending startup switch keeps requested position, disables repeat clicks and does not save before acknowledgement");Assert(AllControls(form).OfType<Label>().Any(x=>x.Text.StartsWith("Включаем…")),"Pending startup operation has a clear inline progress message");}finally{delay=false;acknowledge.Set();}wait();
                Assert(checkbox.Checked && store.Settings.StartWithWindows && registration==StartupRegistration.Command(Application.ExecutablePath),"Startup checkbox registers tray and persists only after service acknowledges success: checked="+checkbox.Checked+", saved="+store.Settings.StartWithWindows+", changes="+changes+", notices="+String.Join(" | ",AllControls(form).OfType<ProblemNotice>().Select(x=>x.Message.Text))+", log="+((TextBox)typeof(MainForm).GetField("log",flags).GetValue(form)).Text);
                Assert(AllControls(form).OfType<Label>().Any(x=>x.Text.Contains("обход запустится при старте Windows")),"Stopped service keeps startup enabled for next boot");
                Capture(form,Path.Combine(root,"tests","window-startup-enabled.png"));
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());checkbox.Checked=false;wait();Assert(!checkbox.Checked && !store.Settings.StartWithWindows && registration==null,"Disabling checkbox removes both startup registrations");
                denied=true;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());checkbox.Checked=true;wait();Assert(!checkbox.Checked && registration==null && !store.Settings.StartWithWindows,"Declined UAC restores checkbox and previous user logon registration");
                Assert(changes==3,"Each checkbox action issues a single service request");
                Assert(AllControls(form).OfType<Label>().Any(x=>x.Text.Contains("Изменение отменено")),"Cancelled Windows prompt leaves an understandable inline status");
                state=new StartupState{Installed=true,Owned=true,Running=true,Engine=store.Settings.Engine,Strategy=store.Settings.Strategy};
                using(var trayForm=new MainForm(store,false,startInTray:true)) {
                    trayForm.Show();Application.DoEvents();
                    Assert(!trayForm.Visible && !trayForm.ShowInTaskbar,"Logon launch creates tray icon without displaying window or taskbar entry");
                    typeof(MainForm).GetMethod("Restore",flags).Invoke(trayForm,null);Application.DoEvents();Assert(trayForm.Visible && trayForm.ShowInTaskbar,"Tray restore opens existing window on demand");
                    typeof(MainForm).GetMethod("Exit",flags).Invoke(trayForm,null);Assert(!trayForm.Visible && state.Running,"Exiting GUI leaves boot service running");
                }
                state=new StartupState();refresh();store.Settings.StartWithWindows=false;store.Save();

            }finally{runner.ConflictCheck=oldConflict;runner.TestStartupChange=null;StartupService.TestRead=null;StartupRegistration.TestRead=null;StartupRegistration.TestWrite=null;}
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
                NetworkFootprint.TestRead=()=>new NetworkFootprint();
                if(args.Contains("--engines-only")){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);HeaderLayoutTests(root,official);ScopeTests(root,official);ScopeTests(root,Path.Combine(root,"downloads","official-1.10.4","zapret-discord-youtube-1.10.4"));WorkspaceTests(root,official,args.Contains("--proxy-native"));ShellVersionTests().GetAwaiter().GetResult();Console.WriteLine("PASS: "+count+" engine/version checks");return 0;}
                if(args.Contains("--shutdown-only")){DriverShutdownTests(root);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);TelegramTests(root,official);Console.WriteLine("PASS: "+count+" shutdown checks");return 0;}
                if(args.Contains("--telegram-only")){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);TelegramTests(root,official);TelegramTransportTests(root,official);Console.WriteLine("PASS: "+count+" Telegram checks");return 0;}
                if(args.Contains("--core-updates-only")){
                    CoreUpdateTests(root,official).GetAwaiter().GetResult();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Core104UiTests(root,official);Console.WriteLine("PASS: "+count+" core update checks");return 0;
                }
                if(args.Contains("--core-network-only")){CoreNetworkTests(root,official).GetAwaiter().GetResult();Console.WriteLine("PASS: "+count+" live core update checks");return 0;}
                if(args.Contains("--youtube-doh-only")){
                    System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;
                    foreach(var target in ServiceChecks.Targets.Take(2)){
                        var result=ServiceChecks.RunWithDnsFallback(target,CancellationToken.None,12,_=>Task.FromResult(new CheckResult{Failure="dns",Detail="Тестовый сбой DNS Windows"}),cancel=>ServiceChecks.RunYouTubeDoH(target,cancel,12)).GetAwaiter().GetResult();
                        Console.WriteLine(target.Name+": state="+result.State+", route="+result.DnsRoute+", "+result.Detail);if(result.State!=1)return 1;
                    }return 0;
                }
                local=official;YouTubeDnsFallbackTests(root).GetAwaiter().GetResult();YouTubeDnsTests(root).GetAwaiter().GetResult();StartupTests(root,official);SecurityTests(root,official);LegacyMigrationTest(root); DriverRecoveryTests(root); DriverShutdownTests(root); FeedbackAndDriverTests(root,official).GetAwaiter().GetResult(); SelectionTests().GetAwaiter().GetResult(); ServiceProbeTests().GetAwaiter().GetResult(); DomainTests(root); UpdateFeedTests().GetAwaiter().GetResult(); CoreUpdateTests(root,official).GetAwaiter().GetResult(); AppUpdateSessionTests(root).GetAwaiter().GetResult(); StageTests(root).GetAwaiter().GetResult(); ShellReplacementTest(root); ShellRollbackTest(root); ShellHandshakeTest(root);
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
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);HeaderLayoutTests(root,official);ScopeTests(root,official);ScopeTests(root,Path.Combine(root,"downloads","official-1.10.4","zapret-discord-youtube-1.10.4"));WorkspaceTests(root,official,false);ShellVersionTests().GetAwaiter().GetResult();TelegramTests(root,official);TelegramTransportTests(root,official);
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
                        Assert(form.Controls.Count > 0, "Desktop UI constructed and rendered"); ProblemUiChecks(form,root); DiagnosticLogTests(store,root); InteractiveUiChecks(form,root); ResponsiveScreens(form,root); StartupUiTests(root,official);YouTubeDnsUiTests(root);SettingsAndHelpTests(form,store,root);AppUpdateUiTests(store,root); AutomaticUpdateTests(root,official); Core104UiTests(root,official); AppUpdateOutcomeUiTests(store);
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
        static string CoreMetadata(string version,string hash,long size) {
            return UpstreamTrust.Serialize(new{tag_name=version,draft=false,prerelease=false,assets=new[]{new{name="zapret-discord-youtube-"+version+".zip",browser_download_url=UpstreamTrust.ArchiveUrl(version),digest="sha256:"+hash,size=size}}});
        }
        static async Task CoreNetworkTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-live-core-"+Guid.NewGuid().ToString("N"));var store=new Store(folder);Copy(official,store.Current);store.Save();
            try {
                File.WriteAllText(Path.Combine(store.Current,"lists","list-general-user.txt"),"network-check.example\r\n");
                using(var updater=new Updater(store)) {
                    var release=await updater.Check();Assert(release.Version=="1.10.4","Live official latest is the requested 1.10.4 release");
                    await updater.Install(release,Console.WriteLine);
                    Assert(Updater.Version(store.Current)=="1.10.4" && File.ReadAllText(Path.Combine(store.Current,"lists","list-general-user.txt"))=="network-check.example\r\n","Live ZIP installs and preserves lists");
                    UpstreamTrust.Clear();var inputs=EngineHost.ReadInputs(store.Current);
                    Assert(inputs.ContainsKey(UpstreamTrust.ReceiptName),"Fresh helper state independently authenticates exact 1.10.4 metadata from GitHub");
                    string snapshot=Path.Combine(folder,"snapshot");Directory.CreateDirectory(snapshot);EngineHost.WriteSnapshot(snapshot,inputs,false);Strategy.ValidateEngine(snapshot);
                    Assert((string)Trust.VerifyEngine(snapshot)["version"]=="1.10.4","Live upstream core passes snapshot and strategy verification without launching its driver");
                    updater.Rollback();Assert(Updater.Version(store.Current)=="1.10.3","Live core installation can roll back to 1.10.3");
                }
            }finally{UpstreamTrust.Clear();RemoveFixture(folder);}
        }
        static async Task CoreUpdateTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-core-feed-"+Guid.NewGuid().ToString("N"));
            var store=new Store(Path.Combine(folder,"app"));Copy(official,store.Current);store.Save();
            string initial=store.Settings.Engine,settings=Path.Combine(store.Root,"data","settings.json"),before=Trust.Hash(settings);
            UpstreamTrust.TestApprovalRoot=Path.Combine(folder,"approvals");UpstreamTrust.Clear();
            try {
                string future=Path.Combine(folder,"future");Copy(official,future);File.WriteAllText(Path.Combine(future,"service.bat"),"set LOCAL_VERSION=1.10.5\r\n");
                string zip=Path.Combine(folder,"future.zip");ZipFile.CreateFromDirectory(future,zip);byte[] bytes=File.ReadAllBytes(zip);string hash=Trust.Hash(zip),metadata=CoreMetadata("1.10.5",hash,bytes.Length);
                bool corrupt=false;int downloads=0,independentChecks=0;
                UpstreamTrust.TestMetadata=version=>{independentChecks++;return metadata;};
                var http=new MockHttp{Reply=url=>{
                    if(url.EndsWith("/releases/latest"))return Response(200,metadata);
                    Assert(url==UpstreamTrust.ArchiveUrl("1.10.5"),"Only official Flowseal release archive URLs are downloaded");downloads++;
                    var content=(byte[])bytes.Clone();if(corrupt)content[0]^=1;
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(content)};
                }};
                File.WriteAllText(Path.Combine(store.Current,".upstream-ipset.sha256"),Trust.Hash(Path.Combine(store.Current,"lists","ipset-all.txt")));
                File.WriteAllText(Path.Combine(store.Current,"utils","game_filter.enabled"),"tcp\r\n");
                using(var updater=new Updater(store,http)) {
                    var release=await updater.Check();Assert(release.Version=="1.10.5" && http.Calls==1 && downloads==0,"Future upstream release is discovered directly without owner approval or a new shell");
                    corrupt=true;bool rejected=false;try{await updater.Install(release,_=>{});}catch(InvalidDataException){rejected=true;}
                    Assert(rejected && before==Trust.Hash(settings) && store.Settings.Engine==initial,"Corrupt download preserves installed core and settings");corrupt=false;
                    using(var prepared=await updater.Prepare(release,_=>{})) {
                        Assert(before==Trust.Hash(settings) && store.Settings.Engine==initial,"Archive validation finishes before switching the working core");
                        File.WriteAllText(Path.Combine(store.Current,"lists","list-general-user.txt"),"last-edit.example\r\n");updater.Activate(prepared);
                    }
                    Assert(Updater.Version(store.Current)=="1.10.5" && Trust.VerifyEngine(store.Current)!=null,"A future core is verified against the official archive without an embedded catalogue entry");
                    Assert(File.ReadAllText(Path.Combine(store.Current,"lists","list-general-user.txt"))=="last-edit.example\r\n" && File.ReadAllText(Path.Combine(store.Current,"utils","game_filter.enabled"))=="tcp\r\n","Latest user lists and game settings survive activation");
                    UpstreamTrust.Clear();var inputs=EngineHost.ReadInputs(store.Current);
                    Assert(independentChecks==1,"Fresh helper state independently fetches exact upstream release metadata");
                    string receipt=System.Text.Encoding.UTF8.GetString(inputs[UpstreamTrust.ReceiptName]);
                    Reject(()=>UpstreamTrust.AuthenticatedApproval(System.Text.Encoding.UTF8.GetBytes(receipt.Replace(hash,new string('a',64)))),"A local receipt cannot fabricate a privileged approval");
                    string snapshot=Path.Combine(folder,"snapshot");Directory.CreateDirectory(snapshot);EngineHost.WriteSnapshot(snapshot,inputs,false);
                    Assert(Trust.VerifyEngine(snapshot)!=null,"Snapshot independently validates copied immutable files");
                    File.AppendAllText(Path.Combine(snapshot,"general.bat"),"tamper");Reject(()=>Trust.VerifyEngine(snapshot),"Modified strategy cannot be authorized by upstream receipt");
                    Directory.CreateDirectory(UpstreamTrust.ApprovalRoot);string approval=UpstreamTrust.ApprovalPath(hash);File.WriteAllText(approval,receipt);UpstreamTrust.Clear();UpstreamTrust.TestMetadata=version=>{throw new IOException("Test offline");};
                    bool refused=false;try{Trust.VerifyEngine(store.Current);}catch(IOException){refused=true;}Assert(refused,"User-writable approval files cannot authorize a core offline");
                    UpstreamTrust.TestProtected=path=>true; // Simulate administrator-owned ACL only for this fixture.
                    Assert(Trust.VerifyEngine(store.Current)!=null,"Persisted administrator approval allows offline verification after process restart");
                    File.AppendAllText(Path.Combine(store.Current,"bin","winws.exe"),"tamper");Reject(()=>Trust.VerifyEngine(store.Current),"Offline approval still rejects changed executables");File.Copy(Path.Combine(official,"bin","winws.exe"),Path.Combine(store.Current,"bin","winws.exe"),true);
                    string updated=store.Settings.Engine;updater.Rollback();Assert(Updater.Version(store.Current)=="1.10.3" && store.Settings.PreviousEngine==updated && File.ReadAllText(Path.Combine(store.Current,"lists","list-general-user.txt"))=="last-edit.example\r\n","Rollback restores old core while preserving current lists");
                    Assert(Directory.GetDirectories(Path.Combine(store.Root,"data"),"staging-*").Length==0,"All download staging folders are cleaned up");
                }
                Assert(Updater.IsNewer("1.10.10","1.10.4") && !Updater.IsNewer("1.10.3","1.10.4") && !Updater.IsNewer("1.10.4","1.10.4"),"Version comparison prevents downgrades");
                foreach(string bad in new[]{metadata.Replace("sha256:","sha512:"),metadata.Replace("github.com/Flowseal/","github.com/other/"),metadata.Replace("\"draft\":false","\"draft\":true"),metadata.Replace("\"prerelease\":false","\"prerelease\":true")})Reject(()=>UpstreamTrust.ParseRelease(bad),"Invalid or non-stable upstream release rejected");
            }finally{UpstreamTrust.TestMetadata=null;UpstreamTrust.TestApprovalRoot=null;UpstreamTrust.TestProtected=null;UpstreamTrust.Clear();RemoveFixture(folder);}
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
                Assert(card.Action.Text=="Проверить обновления" && card.Action.Enabled && card.ReloadVersions.Enabled && !card.InstallVersion.Enabled && !card.Cancel.Visible,"Latest update and manual version picker have separate explicit actions");
                Capture(form,Path.Combine(root,"tests","window-updates-idle.png"));
                Pump(UpdateClick(form));Assert(card.Action.Text=="Обновить" && http.Calls==1,"Native update button changes to install without downloading");
                Assert(form.Text.StartsWith("ByPass") && AllControls(form).OfType<UpdateNotice>().Single().Visible && confirmations==0,"ByPass shows a quiet update notice without an automatic confirmation dialog");
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
                http.Reply=url=>url.Contains("releases?")?Response(200,"["+RawFeed("0.6.3")+","+RawFeed("0.6.2")+"]"):Response(200,url.EndsWith("build-proof.json")?OwnerManifest(url.Contains("v0.6.3/")?"0.6.3":"0.6.2"):Feed(url.Contains("v0.6.3/")?"0.6.3":"0.6.2"));
                Pump(session.LoadVersions());card.Versions.SelectedItem="0.6.2";Assert(card.InstallVersion.Enabled,"A published older version can be selected in the actual app card");
                accept=false;int previousConfirmations=confirmations;Pump((Task)typeof(MainForm).GetMethod("InstallSelectedShell",flags).Invoke(form,null));Assert(confirmations==previousConfirmations+1 && session.Release.Version=="0.6.2" && !session.Working,"Declining explicit downgrade makes no install and retains selected verified release");
                foreach(var size in new[]{new Size(940,690),new Size(1100,830)}){form.ClientSize=new Size((int)(size.Width*Theme.SystemScale),(int)(size.Height*Theme.SystemScale));Application.DoEvents();Assert(card.ClientRectangle.Contains(card.Versions.Bounds) && card.ClientRectangle.Contains(card.InstallVersion.Bounds) && !card.Versions.Bounds.IntersectsWith(card.Action.Bounds),"Version controls fit without overlapping latest update action: "+size);Capture(form,Path.Combine(root,"tests","window-updates-version-choice-"+size.Width+".png"));}
                http.Reply=url=>Response(200,"[]");Pump(session.LoadVersions());Assert(card.Versions.Items.Count==0 && !card.InstallVersion.Visible && session.Release==null && !session.ManualVersion && session.Message.Length==0,"Actual application clears an unavailable selection and hides its installation action");Capture(form,Path.Combine(root,"tests","window-updates-no-installable-versions.png"));
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
        static void Core104UiTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-core104-ui-"+Guid.NewGuid().ToString("N"));var store=new Store(folder);Copy(official,store.Current);
            store.Settings.CheckUpdates=false;store.Settings.AutoAppUpdates=false;store.Save();
            File.WriteAllText(Path.Combine(store.Current,"lists","list-general-user.txt"),"upgrade.example\r\n");
            File.WriteAllText(Path.Combine(store.Current,".upstream-ipset.sha256"),Trust.Hash(Path.Combine(store.Current,"lists","ipset-all.txt")));
            byte[] archive=File.ReadAllBytes(Path.Combine(root,"downloads","zapret-discord-youtube-1.10.4.zip"));
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;Exception failure=null;
            var thread=new Thread(()=>{
                try {
                    using(var form=new MainForm(store,false,false,false,false,new ShellUpdates(new MockHttp{Reply=_=>Response(500,"")}),(title,message)=>true,new Updater(store,new MockHttp{AsyncReply=async(url,token)=>{
                        if(url.EndsWith("/releases/latest"))return Response(200,CoreMetadata("1.10.4","291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34",archive.Length));
                        await Task.Delay(300,token);return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(archive)};
                    }}))) {
                        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2500,-2500);
                        form.Shown+=async(s,e)=>{
                            var runner=(EngineRunner)typeof(MainForm).GetField("runner",flags).GetValue(form);bool failStart=false;int stops=0,starts=0;
                            runner.ConflictCheck=()=>false;runner.TestStop=()=>stops++;runner.TestSync=(engine,strategy)=>{};
                            runner.TestStart=(engine,strategy)=>{starts++;if(failStart && Updater.Version(engine)=="1.10.4")throw new EngineStartException("Тестовый отказ запуска нового ядра");};
                            try {
                                FindTabs(form).SelectedIndex=2;
                                runner.Start(store.Current,store.Settings.Strategy);int initialStarts=starts;
                                await (Task)typeof(MainForm).GetMethod("CheckUpdates",flags).Invoke(form,new object[]{false});
                                var button=(Button)typeof(MainForm).GetField("install",flags).GetValue(form);
                                Assert(button.Enabled && runner.Running,"Manual core installation is available while the owned bypass is connected");
                                Capture(form,Path.Combine(root,"tests","window-core104-available.png"));
                                var pending=(Task)typeof(MainForm).GetMethod("Install",flags).Invoke(form,null);
                                Assert(runner.Running && stops==0 && store.Settings.Engine=="1.10.3","Core download keeps the old connection running and settings unchanged");
                                Capture(form,Path.Combine(root,"tests","window-core104-downloading.png"));await pending;
                                Assert(Updater.Version(store.Current)=="1.10.4" && runner.Running && starts==initialStarts+1 && stops==1,"Actual 1.10.3 to 1.10.4 installation switches once and restores connection through the test transport");
                                Assert(new Store(folder).Settings.Engine==store.Settings.Engine && File.ReadAllText(Path.Combine(store.Current,"lists","list-general-user.txt"))=="upgrade.example\r\n","Real 1.10.4 ZIP activation preserves user lists and persists the new core");
                                string snapshot=Path.Combine(folder,"verified-snapshot");Directory.CreateDirectory(snapshot);EngineHost.WriteSnapshot(snapshot,EngineHost.ReadInputs(store.Current),false);Strategy.ValidateEngine(snapshot);
                                Assert((string)Trust.VerifyEngine(snapshot)["version"]=="1.10.4","Every 1.10.4 strategy parses and the privileged snapshot passes trust verification");
                                Capture(form,Path.Combine(root,"tests","window-core104-installed.png"));
                                runner.Stop();using(var updater=new Updater(store))updater.Rollback();typeof(MainForm).GetMethod("LoadSettings",flags).Invoke(form,null);runner.Start(store.Current,store.Settings.Strategy);
                                string old=store.Settings.Engine,previous=store.Settings.PreviousEngine;
                                await (Task)typeof(MainForm).GetMethod("CheckUpdates",flags).Invoke(form,new object[]{false});failStart=true;
                                await (Task)typeof(MainForm).GetMethod("Install",flags).Invoke(form,null);
                                Assert(store.Settings.Engine==old && store.Settings.PreviousEngine==previous && new Store(folder).Settings.Engine==old && runner.Running,"Failed new-core startup restores prior settings, previous-version history and the working connection");
                                Capture(form,Path.Combine(root,"tests","window-core104-restored.png"));
                            }catch(Exception ex){failure=ex;}
                            finally{typeof(MainForm).GetField("exiting",flags).SetValue(form,true);form.Close();}
                        };
                        Application.Run(form);
                    }
                }catch(Exception ex){failure=ex;}
            });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
            try{Assert(thread.Join(60000),"Core 1.10.4 UI scenarios complete in the real WinForms message loop");if(failure!=null)throw failure;}
            finally{RemoveFixture(folder);}
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
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var uiTimer=(System.Windows.Forms.Timer)typeof(MainForm).GetField("timer",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(form);uiTimer.Stop();
            var tabs=FindTabs(form);tabs.SelectedIndex=0;var dashboard=AllControls(form).OfType<Dashboard>().Single();
            typeof(DarkSelect).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(dashboard.Profiles,new object[]{EventArgs.Empty});Application.DoEvents();
            var popup=(ToolStripDropDown)typeof(DarkSelect).GetField("popup",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(dashboard.Profiles);
            Assert(popup!=null && popup.Visible,"Custom strategy dropdown opens");
            using(var image=new Bitmap(popup.Width,popup.Height)){popup.DrawToBitmap(image,new Rectangle(Point.Empty,popup.Size));image.Save(Path.Combine(root,"tests","window-dropdown.png"));}
            dashboard.Profiles.Choose(1);Assert(dashboard.Profiles.SelectedIndex==1,"Custom strategy dropdown applies item selection");dashboard.Profiles.SelectedIndex=0;
            dashboard.SetFeedback(true);Application.DoEvents();
            Assert(dashboard.Works.Visible && dashboard.Next.Visible && !dashboard.Works.Bounds.IntersectsWith(dashboard.Next.Bounds),"Actual-client feedback buttons are visible and do not overlap");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(root,"tests","window-feedback.png"));}
            dashboard.SetFeedback(false);
            tabs.SelectedIndex=tabs.TabPages.FindIndex(x=>AllControls(x).OfType<DomainEditor>().Any());var editor=AllControls(form).OfType<DomainEditor>().Single();editor.TextEditor.Text=String.Join("\r\n",Enumerable.Range(1,100).Select(i=>"site-"+i+".example"));Application.DoEvents();
            Assert(editor.Dirty && AllControls(editor).OfType<SlimScroll>().Any(x=>x.Visible),"Editor tracks unsaved text and shows thin scrollbar for long lists");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(root,"tests","window-domain-scroll.png"));}
            editor.Discard();editor.TextEditor.Text="ui-save.example";var save=AllControls(editor).OfType<Button>().Single(x=>x.Text=="Сохранить");
            if(save.Enabled){SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());save.PerformClick();var saved=Stopwatch.StartNew();while((bool)typeof(MainForm).GetField("starting",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(form) && saved.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}Application.DoEvents();Assert(!editor.Dirty && editor.TextEditor.Text.Contains("ui-save.example") && !editor.TextEditor.ReadOnly,"Domain editor saves through actual asynchronous UI event and becomes editable again: dirty="+editor.Dirty+", readonly="+editor.TextEditor.ReadOnly+", elapsed="+saved.ElapsedMilliseconds);}else editor.Discard();
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
            form.Size=original;tabs.SelectedIndex=tabs.TabPages.FindIndex(x=>x.Text=="Дополнительно");
            AllControls(form).OfType<Button>().Single(x=>x.Name=="AdvancedGames").PerformClick();
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
        static void DriverShutdownTests(string root) {
            string folder=Path.Combine(root,"tests","fixture-stop-"+Guid.NewGuid().ToString("N"));
            string owned=Path.Combine(folder,"runtime-"+new string('a',32),"bin","WinDivert64.sys");
            Func<DriverRegistration> fresh=()=>new DriverRegistration{Path=owned,Type=1,Start=3,State=4};
            DriverRegistration value=fresh();int stops=0,deletes=0,probes=0;
            Action stop=()=>{stops++;value.State=1;},delete=()=>{deletes++;value=null;};
            Func<bool> empty=()=>{probes++;return false;};
            Assert(DriverShutdown.Release(()=>value,stop,delete,empty,folder) && stops==1 && deletes==1 && probes==2,"Last consumer stops and removes only the protected driver registration");
            Assert(DriverShutdown.Release(()=>null,()=>{throw new Exception();},()=>{throw new Exception();},()=>{throw new Exception();},folder),"Absent driver needs no installation, consumer probe or service mutation");
            value=fresh();value.State=1;probes=stops=deletes=0;
            Assert(DriverShutdown.Release(()=>value,stop,delete,empty,folder) && stops==0 && deletes==1 && probes==0,"Already unloaded owned driver only removes its registration");
            foreach(string path in new[]{Path.Combine(folder,"..","other","WinDivert64.sys"),Path.Combine(folder,"runtime-invalid","bin","WinDivert64.sys"),Path.Combine(folder,"runtime-"+new string('a',32),"bin","other.sys")}){
                value=fresh();value.Path=path;stops=deletes=0;bool refused=false;try{DriverShutdown.Release(()=>value,stop,delete,empty,folder);}catch(EngineStopException){refused=true;}Assert(refused && stops==0 && deletes==0,"Unowned or malformed driver is preserved: "+path);
            }
            Assert(DriverShutdown.OwnedPath(folder,owned) && DriverShutdown.OwnedPath(folder,owned.Replace("runtime-","startup-")),"Protected runtime and startup driver paths are recognized");
            foreach(uint type in new uint[]{0,2,16}){value=fresh();value.Type=type;stops=deletes=0;bool refused=false;try{DriverShutdown.Release(()=>value,stop,delete,empty,folder);}catch(EngineStopException){refused=true;}Assert(refused && stops==0 && deletes==0,"Non-kernel service is never stopped: "+type);}
            foreach(uint start in new uint[]{0,1,2,4}){value=fresh();value.Start=start;stops=deletes=0;bool refused=false;try{DriverShutdown.Release(()=>value,stop,delete,empty,folder);}catch(EngineStopException){refused=true;}Assert(refused && stops==0 && deletes==0,"Unexpected driver start type is preserved: "+start);}
            for(int occupied=1;occupied<=2;occupied++){
                value=fresh();stops=deletes=probes=0;bool refused=false;int threshold=occupied;
                try{DriverShutdown.Release(()=>value,stop,delete,()=>++probes>=threshold,folder);}catch(EngineStopException){refused=true;}
                Assert(refused && stops==0 && deletes==0,"Existing or newly arrived consumer prevents unloading: "+occupied);
            }
            value=fresh();stops=deletes=0;int reads=0;bool changed=false;
            try{DriverShutdown.Release(()=>{if(++reads==2)value.Path=owned.Replace(new string('a',32),new string('b',32));return value;},stop,delete,empty,folder);}catch(EngineStopException){changed=true;}
            Assert(changed && stops==0 && deletes==0,"Service path changing between inspections cancels unload");
            value=fresh();deletes=0;bool unconfirmed=false;try{DriverShutdown.Release(()=>value,()=>{},delete,empty,folder);}catch(EngineStopException){unconfirmed=true;}
            Assert(unconfirmed && deletes==0,"Driver not confirmed stopped is never deleted or reported unloaded");
            value=fresh();bool denied=false;try{DriverShutdown.Release(()=>value,()=>{throw new System.ComponentModel.Win32Exception(5);},delete,empty,folder);}catch(System.ComponentModel.Win32Exception){denied=true;}
            Assert(denied,"Stop permission failure propagates without false success");
            value=fresh();bool pending=false;try{DriverShutdown.Release(()=>value,stop,()=>{},empty,folder);}catch(EngineStopException){pending=true;}
            Assert(pending,"Retained service registration is not reported as fully released");
            var previousRead=StartupService.TestRead;StartupService.TestRead=_=>new StartupState();
            try{using(var client=new EngineRunner()){
                for(int cycle=0;cycle<4;cycle++){
                    Console.WriteLine("Checking idle pipe close: cycle "+cycle);
                    string pipeName="ByPass-stop-test-"+Guid.NewGuid().ToString("N");bool warn=cycle==1 || cycle==3,missingConfirmation=cycle==3;
                    using(var server=new System.IO.Pipes.NamedPipeServerStream(pipeName,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous)){
                        var connection=server.BeginWaitForConnection(null,null);
                        var peer=Task.Run(()=>{using(var pipe=new System.IO.Pipes.NamedPipeClientStream(".",pipeName,System.IO.Pipes.PipeDirection.InOut)){
                            pipe.Connect(5000);var command=HostWire.Read(pipe);if((string)command["action"]!="release-idle")throw new Exception("Unexpected idle command");
                            HostWire.Write(pipe,new{type="reply",running=false,telegram_running=false,closing=!missingConfirmation,error=warn && !missingConfirmation?"Test driver busy":"",error_kind=warn?"stop":""});
                        }});
                        if(!connection.AsyncWaitHandle.WaitOne(5000)){if(peer.IsFaulted)peer.GetAwaiter().GetResult();throw new Exception("Test pipe connection timed out");}
                        server.EndWaitForConnection(connection);client.TestAttach(server);bool failed=false;
                        try{client.ReleaseIfIdle();}catch(EngineStopException){failed=true;}
                        peer.GetAwaiter().GetResult();
                        Assert(failed==warn && client.IdleReleased!=warn && !client.Running && !client.TelegramRunning,"Real pipe idle close drains reply/EOF and preserves stop result: cycle "+cycle);
                        Assert(warn?!String.IsNullOrEmpty(client.StopWarning):String.IsNullOrEmpty(client.StopWarning),"Cleanup warning follows protocol instead of being treated as start failure: cycle "+cycle);
                    }
                }
            }}finally{StartupService.TestRead=previousRead;}
        }
        static void DriverRecoveryTests(string root) {
            string folder=Path.Combine(root,"tests","fixture-driver-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try {
                string path=Path.Combine(folder,"zapret-discord-youtube-1.9.8c","bin","WinDivert64.sys");
                Func<DriverRegistration> old=()=>new DriverRegistration{Path=@"\??\"+path,Type=1,Start=3,State=1};
                int deletes=0,reads=0;var log=new System.Collections.Generic.List<string>();
                bool repaired=DriverServiceRecovery.TryRepair(()=>{reads++;return old();},()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,log.Add);
                Assert(repaired && deletes==1 && reads==2 && log.Last().Contains("1.9.8c"),"Exact reported missing 1.9.8c driver registration is removed once after revalidation");
                Assert(!Directory.Exists(Path.GetDirectoryName(path)),"Recovery does not recreate or write into the user's obsolete zapret folder");
                Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"fixture");deletes=0;
                Assert(!DriverServiceRecovery.TryRepair(old,()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null) && deletes==0,"Existing stopped external driver registration remains untouched");
                File.Delete(path);
                foreach(uint state in new uint[]{2,3,4,5,6,7}) {
                    Assert(!DriverServiceRecovery.TryRepair(()=>{var value=old();value.State=state;return value;},()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null) && deletes==0,"Active or transitional WinDivert state "+state+" is never stopped or deleted");
                }
                foreach(uint type in new uint[]{2,16,32}) {
                    Assert(!DriverServiceRecovery.TryRepair(()=>{var value=old();value.Type=type;return value;},()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null),"Only a kernel driver qualifies for cleanup");
                }
                foreach(uint start in new uint[]{0,1,2,4}) {
                    Assert(!DriverServiceRecovery.TryRepair(()=>{var value=old();value.Start=start;return value;},()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null),"Boot, automatic and disabled services are not reset");
                }
                reads=0;Assert(!DriverServiceRecovery.TryRepair(()=>{reads++;return old();},()=>deletes++,()=>true,DriverServiceRecovery.MissingFile,null) && reads==0 && deletes==0,"External winws prevents all recovery activity");
                int conflicts=0;Assert(!DriverServiceRecovery.TryRepair(old,()=>deletes++,()=>++conflicts>1,DriverServiceRecovery.MissingFile,null) && deletes==0,"External winws appearing between inspections cancels deletion");
                reads=0;Assert(!DriverServiceRecovery.TryRepair(()=>{var value=old();if(++reads>1)value.State=4;return value;},()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null) && deletes==0,"Driver becoming active during inspection is preserved");
                reads=0;Assert(!DriverServiceRecovery.TryRepair(()=>{var value=old();if(++reads>1)value.Path=path+".changed";return value;},()=>deletes++,()=>false,p=>true,null) && deletes==0,"Changed registration path cancels stale repair");
                Assert(!DriverServiceRecovery.TryRepair(()=>null,()=>deletes++,()=>false,DriverServiceRecovery.MissingFile,null) && deletes==0,"Absent service needs no cleanup");
                Assert(!DriverServiceRecovery.TryRepair(old,()=>deletes++,()=>false,p=>false,null) && deletes==0,"Unknown or inaccessible file is not treated as missing");
                Assert(!DriverServiceRecovery.MissingFile(@"\\server\share\WinDivert64.sys") && !DriverServiceRecovery.MissingFile(Path.Combine(folder,"other.sys")),"Network paths and unrelated driver filenames are excluded");
                bool denied=false;try{DriverServiceRecovery.TryRepair(old,()=>{throw new System.ComponentModel.Win32Exception(5);},()=>false,DriverServiceRecovery.MissingFile,null);}catch(System.ComponentModel.Win32Exception){denied=true;}
                Assert(denied,"SCM access denial is propagated rather than silently claiming repair");
            }finally{RemoveFixture(folder);}
        }
        static async Task FeedbackAndDriverTests(string root,string official) {
            string folder=Path.Combine(root,"tests","fixture-feedback-"+Guid.NewGuid().ToString("N"));
            var store=new Store(folder);Copy(official,store.Current);var feedback=new StrategyFeedback(store);
            string alt3="general (ALT3).bat",alt9="general (ALT9).bat";
            try {
                var names=store.Strategies();
                Assert(feedback.Order(names,alt9)[0]==alt9,"Existing manually selected ALT9 precedes ALT3 without hardcoding a winner");
                feedback.Accept(alt9);
                Assert(new StrategyFeedback(new Store(folder)).Working==alt9,"User-confirmed strategy persists across application restart");
                Assert(feedback.Order(names,alt3)[0]==alt9,"Confirmed working strategy has priority over another current selection");
                feedback.Reject(alt3);Assert(!feedback.Order(names,alt3).Contains(alt3),"Rejected false-positive ALT3 is skipped by continued selection");
                feedback.Reject(alt9);Assert(feedback.Working=="" && new Store(folder).Settings.WorkingStrategy=="","Rejecting a previously accepted strategy removes its confirmation");
                feedback.ResetRejected();Assert(feedback.Order(names,alt3).Contains(alt3),"A new explicit scan can retry rejected candidates");
                bool missingRejected=false;try{feedback.Accept("missing.bat");}catch(InvalidOperationException){missingRejected=true;}Assert(missingRejected,"Missing strategy cannot be saved as user-confirmed");
                feedback.Accept(alt9);store.Settings.Engine="1.10.2";
                Assert(feedback.Working=="" && !feedback.IsRejected(alt3),"Confirmation is scoped to the engine version");
                string running=null;int calls=0;
                Func<CancellationToken,Task<CheckResult[]>> probes=token=>Task.FromResult(Enumerable.Range(0,ServiceChecks.Targets.Length).Select(x=>new CheckResult{State=1,Milliseconds=running==alt3?1:100}).ToArray());
                var result=await StrategySelector.Run(new[]{alt3,alt9},(name,token)=>{running=name;return Task.FromResult(0);},()=>running=null,probes,CancellationToken.None,null,null,false,null,alt9);
                Assert(result.Winner.Strategy==alt9,"Full scan keeps preferred ALT9 even when synthetic ALT3 latency is lower");
                store.Settings.Engine="1.10.3";feedback.Reject(alt3);feedback.Reject(alt9);
                result=await StrategySelector.Run(feedback.Order(names,alt9),(name,token)=>{calls++;running=name;return Task.FromResult(0);},()=>running=null,probes,CancellationToken.None,null,null,true);
                Assert(result.Winner!=null && result.Winner.Strategy!=alt3 && result.Winner.Strategy!=alt9,"Continue finds another candidate without retrying rejected successful probes");
                feedback.Order(names,alt9).ToList().ForEach(feedback.Reject);calls=0;
                result=await StrategySelector.Run(feedback.Order(names,alt9),(name,token)=>{calls++;return Task.FromResult(0);},()=>running=null,probes,CancellationToken.None,null,null,true);
                Assert(result.Winner==null && calls==0 && running==null,"Exhausted rejected candidates leave bypass stopped");
                string error="windivert: error opening filter: The system cannot find the path specified.";
                Assert(EngineDriverException.IsFailure(error) && !EngineDriverException.IsFailure("windivert initialized. capture is started.") && !EngineDriverException.IsFailure("windivert: error opening filter: The parameter is incorrect."),"Driver load failure is distinct from readiness and invalid strategy filter");
                bool failed=false;calls=0;
                try{await StrategySelector.Run(new[]{alt3,alt9},async (name,token)=>{calls++;await EngineRunner.WaitForReady(()=>false,()=>false,token,100,()=>error);},()=>running=null,probes,CancellationToken.None,null,null,true);}catch(EngineDriverException){failed=true;}
                Assert(failed && calls==1 && running==null,"WinDivert failure aborts scan before any network probe or second strategy");
                Assert(UserProblems.Message(new EngineDriverException(error)).Contains("WinDivert"),"Driver error displays a specific user explanation");
                string snapshot=Path.Combine(folder,"runtime-"+new string('a',32));Copy(official,snapshot);
                string driver=Path.Combine(snapshot,"bin","WinDivert64.sys"),registered=@"\??\"+driver;
                string outside=Path.Combine(folder,"outside","WinDivert64.sys");
                Assert(EngineHost.OwnedDriver(folder,registered)==driver && EngineHost.OwnedDriver(folder,outside)==null && EngineHost.OwnedDriver(folder,Path.Combine(folder,"runtime-invalid","bin","WinDivert64.sys"))==null,"Recovery allows only exact own runtime driver paths");
                var inputs=EngineHost.ReadInputs(official);
                EngineHost.CleanupSnapshot(snapshot,new[]{registered,outside});
                Assert(File.Exists(driver) && !File.Exists(Path.Combine(snapshot,"bin","winws.exe")) && !Directory.Exists(Path.Combine(snapshot,"lists")),"Exit preserves only registered own driver, deletes executable and user lists");
                File.Delete(driver);
                var restored=EngineHost.RestoreDriverFiles(folder,inputs,new[]{registered,outside});
                Assert(restored.SequenceEqual(new[]{driver}) && Trust.Hash(driver)==Trust.Hash(Path.Combine(official,"bin","WinDivert64.sys")) && !File.Exists(outside),"Missing registered own driver restored byte-for-byte from verified original core; external path untouched");
                Assert(EngineHost.RestoreDriverFiles(folder,inputs,new[]{registered}).Length==0,"Existing driver is neither replaced nor overwritten");
                EngineHost.CleanupSnapshot(snapshot,new string[0]);
                Assert(!Directory.Exists(snapshot),"Unregistered runtime is removed completely");
            }finally{RemoveFixture(folder);}
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
                Assert(release.Version == "1.10.4", "Latest official release fetched");
                await updater.Install(release, Console.WriteLine);
                Assert(Updater.Version(store.Current) == release.Version, "Verified official ZIP installed");
                Assert(File.ReadAllText(Path.Combine(store.Current, "lists", "list-general-user.txt")) == "my-service.example\n", "User list survives real update");
                updater.Rollback(); Assert(Updater.Version(store.Current) == "1.10.3", "Rollback after real update");
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
