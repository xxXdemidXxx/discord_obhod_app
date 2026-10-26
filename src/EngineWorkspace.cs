using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretDesktop {
    public sealed partial class MainForm {
        DarkSelect backendChoice,proxyProfile;SurfacePanel proxyPane;
        ToggleOption strictScope,scopeYouTube,scopeDiscord,scopeVoice;
        Button stopEverything,inspectFootprint,openProxyYouTube,openProxyDiscord,applyProxy;
        Label footprintText,backendHint;NetworkFootprint footprint;bool footprintBusy,backendChanging,stopAllAfterScan;
        string cleanupFailure;
        readonly Dictionary<string,DashboardSnapshot> backendResults=new Dictionary<string,DashboardSnapshot>();
        BackendTransition backendTransition;bool? renderedBackend;string renderedTargets="";Label scopeTitle,scopeHint;
        Control scopeSurface,startupBackendCard,coreBackendCard,coreBackendGap;
        readonly List<OwnedJob> proxyBrowsers=new List<OwnedJob>();readonly List<Process> proxyBrowserProcesses=new List<Process>();
        bool ProxyMode {get{return store.Settings.Backend=="byedpi";}}
        ScopeOptions NativeScope(){return new ScopeOptions{Strict=store.Settings.StrictScope,YouTube=store.Settings.ScopeYouTube,Discord=store.Settings.ScopeDiscord,Voice=store.Settings.ScopeVoice};}
        ScopeOptions CurrentScope(){return ProxyMode?new ScopeOptions{YouTube=store.Settings.ProxyScopeYouTube,Discord=store.Settings.ProxyScopeDiscord}:NativeScope();}
        void BuildBackendControls(Control status) {
            backendChoice=new DarkSelect{Name="BypassBackend"};backendChoice.Items.AddRange(new object[]{"zapret · системный обход","ByeDPI · без драйвера"});
            backendChoice.SelectedIndex=ProxyMode?1:0;
            stopEverything=Theme.Button("Остановить всё и проверить");stopEverything.Name="StopAllBypasses";
            status.Controls.AddRange(new Control[]{backendChoice,stopEverything});
            status.Layout+=(s,e)=>LayoutConnectionHeader(status);LayoutConnectionHeader(status);
            backendChoice.SelectedIndexChanged+=async(s,e)=>{if(!loading)await ChangeBackend();};stopEverything.Click+=async(s,e)=>await StopEverything();
            proxyPane=new SurfacePanel{Name="ProxyWorkspace",Visible=ProxyMode};var title=Theme.Label("Вариант ByeDPI",12,true);
            proxyProfile=new DarkSelect{Name="ByeDpiProfile"};proxyProfile.Items.AddRange(new object[]{"Split · разделение TLS","Disorder · порядок пакетов","TLS record · разделение записи"});proxyProfile.SelectedIndex=Math.Max(0,Array.IndexOf(ProxyBypass.Profiles,store.Settings.ProxyStrategy));
            applyProxy=Theme.Button("Включить и проверить",true);applyProxy.Name="ApplyProxyBackend";openProxyYouTube=Theme.Button("Открыть YouTube");openProxyDiscord=Theme.Button("Открыть Discord");
            backendHint=Theme.Label("Отдельное окно · без системного прокси.\nУстановленный Discord и голос не поддерживаются.\nВарианты меняются с полной остановкой.",9);backendHint.ForeColor=Theme.Muted;
            proxyPane.Controls.AddRange(new Control[]{title,proxyProfile,applyProxy,openProxyYouTube,openProxyDiscord,backendHint});dashboard.Controls.Add(proxyPane);
            backendTransition=new BackendTransition();dashboard.Controls.Add(backendTransition);
            dashboard.Layout+=(s,e)=>{proxyPane.Bounds=profiles.Parent.Bounds;};proxyPane.Resize+=(s,e)=>{int p=Px(18),w=proxyPane.Width-p*2;title.SetBounds(p,Px(12),w,Px(26));proxyProfile.SetBounds(p,Px(44),w,Px(34));applyProxy.SetBounds(p,Px(86),w,Px(38));openProxyYouTube.SetBounds(p,Px(132),w,Px(32));openProxyDiscord.SetBounds(p,Px(172),w,Px(32));backendHint.SetBounds(p,Px(214),w,Math.Max(Px(70),proxyPane.Height-Px(224)));};
            applyProxy.Click+=async(s,e)=>await SetProxyEnabled(true);openProxyYouTube.Click+=(s,e)=>Guard(()=>OpenProxyBrowser("https://www.youtube.com/"));openProxyDiscord.Click+=(s,e)=>Guard(()=>OpenProxyBrowser("https://discord.com/app"));
            proxyProfile.SelectedIndexChanged+=(s,e)=>{if(loading || !ProxyMode || runner.ProxyRunning || busy || starting || proxyProfile.SelectedIndex<0)return;Guard(()=>{store.Settings.ProxyStrategy=ProxyBypass.Profiles[proxyProfile.SelectedIndex];store.Save();InvalidateChecks("Вариант ByeDPI изменён · проверьте его заново");});};
        }
        void LayoutConnectionHeader(Control status){
            int p=status.Padding.Left,right=status.Width-p-Px(300),leftWidth=Math.Max(0,right-p-Px(16)),y=Px(status.Height<Px(120)?68:80);
            status.SuspendLayout();try{
                state.SetBounds(p,Px(12),leftWidth,Px(28));subtitle.SetBounds(p,Px(43),leftWidth,Px(26));discord.SetBounds(p,y,leftWidth,Px(30));
                backendChoice.SetBounds(right,Px(12),Px(300),Px(34));stopEverything.SetBounds(right,y,Px(300),Px(34));
            }finally{status.ResumeLayout(false);}
        }
        Control BuildScopeCard() {
            var surface=new SurfacePanel{Name="BypassScopeCard",Dock=DockStyle.Top,Height=Px(388),Margin=new Padding(0,0,0,Px(16))};
            scopeSurface=surface;scopeTitle=Theme.Label("Настройки zapret",18,true);
            strictScope=new ToggleOption{Name="StrictServiceScope",Text="Ограничивать zapret выбранными сервисами"};
            scopeYouTube=new ToggleOption{Name="ScopeYouTube",Text="YouTube"};scopeDiscord=new ToggleOption{Name="ScopeDiscord",Text="Discord"};scopeVoice=new ToggleOption{Name="ScopeDiscordVoice",Text="Голос Discord · UDP / STUN"};
            scopeHint=Theme.Label("",9);scopeHint.ForeColor=Theme.Muted;
            inspectFootprint=Theme.Button("Проверить процессы и службы");inspectFootprint.Name="InspectBypassFootprint";
            var details=Theme.Button("Подробности в журнале");details.Click+=(s,e)=>tabs.SelectedIndex=3;
            footprintText=Theme.Label("Остановка ещё не проверена",9);footprintText.ForeColor=Theme.Muted;
            surface.Controls.AddRange(new Control[]{scopeTitle,strictScope,scopeYouTube,scopeDiscord,scopeVoice,scopeHint,inspectFootprint,details,footprintText});
            surface.Resize+=(s,e)=>{int p=surface.Padding.Left,w=surface.Width-p*2;bool proxy=ProxyMode;int y=proxy?44:84;scopeTitle.SetBounds(p,p,w,Px(34));strictScope.SetBounds(p,p+Px(44),w,Px(32));scopeYouTube.SetBounds(p,p+Px(y),Px(190),Px(32));scopeDiscord.SetBounds(p+Px(200),p+Px(y),Px(190),Px(32));scopeVoice.SetBounds(p+Px(400),p+Px(y),Math.Max(Px(260),w-Px(400)),Px(32));scopeHint.SetBounds(p,p+Px(proxy?90:130),w,Px(72));inspectFootprint.SetBounds(p,p+Px(proxy?170:216),Px(300),Px(38));details.SetBounds(p+Px(312),inspectFootprint.Top,Px(230),Px(38));footprintText.SetBounds(p,p+Px(proxy?224:270),w,Px(68));};
            foreach(var option in new[]{strictScope,scopeYouTube,scopeDiscord,scopeVoice})option.CheckedChanged+=async(s,e)=>{if(!loading)await SaveScope();};
            inspectFootprint.Click+=async(s,e)=>await InspectFootprint();return surface;
        }
        void BuildTelegramPage(){
            var page=Page("Telegram");page.AutoScroll=true;
            var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var surface=new SurfacePanel{Name="TelegramConnectionCard",Dock=DockStyle.Top,Height=Px(264),Margin=new Padding(0,0,0,Px(16))};
            var title=Theme.Label("Telegram",18,true);var hint=Theme.Label("Отдельный мост для сообщений и файлов. Звонки не поддерживаются.\nИспользует WinDivert: при включённом Telegram драйвер остаётся загруженным.\nДля сравнения работы игр нажмите «Остановить всё и проверить» на главной вкладке.",9);hint.ForeColor=Theme.Muted;
            surface.Controls.AddRange(new Control[]{title,telegram,telegramStatus,hint});surface.Resize+=(s,e)=>{int p=surface.Padding.Left,w=surface.Width-p*2;title.SetBounds(p,p,w,Px(34));telegram.SetBounds(p,p+Px(48),w,Px(34));telegramStatus.SetBounds(p,p+Px(90),w,Px(44));hint.SetBounds(p,p+Px(148),w,Px(72));};
            layout.Controls.Add(surface,0,0);layout.Controls.Add(telegramMedia,0,1);page.Controls.Add(layout);tabs.AddPages(new[]{page});
        }
        void LoadBackendSettings() {
            if(store.Settings.Backend!="byedpi")store.Settings.Backend="zapret";
            backendChoice.SelectedIndex=ProxyMode?1:0;strictScope.Checked=store.Settings.StrictScope;scopeYouTube.Checked=CurrentScope().YouTube;scopeDiscord.Checked=CurrentScope().Discord;scopeVoice.Checked=store.Settings.ScopeVoice;
            if(!runner.StartupEnabled && Directory.Exists(store.Current))ServiceScope.Write(store.Current,NativeScope());
        }
        async Task SaveScope() {
            if(busy || starting || scanning || runner.Running || runner.TelegramRunning || runner.StartupEnabled){RefreshBackendControls();return;}
            var next=new ScopeOptions{Strict=strictScope.Checked,YouTube=scopeYouTube.Checked,Discord=scopeDiscord.Checked,Voice=scopeVoice.Checked};
            busy=true;RefreshState();try{
                if(!next.YouTube && !next.Discord)throw new InvalidDataException("Выберите YouTube или Discord.");
                if(ProxyMode){store.Settings.ProxyScopeYouTube=next.YouTube;store.Settings.ProxyScopeDiscord=next.Discord;}
                else{await Task.Run(()=>ServiceScope.Write(store.Current,next));store.Settings.StrictScope=next.Strict;store.Settings.ScopeYouTube=next.YouTube;store.Settings.ScopeDiscord=next.Discord;store.Settings.ScopeVoice=next.Voice;}store.Save();
                InvalidateChecks("Область обхода изменена · включите движок и проверьте сервисы");Append("Область обхода: "+ServiceScope.Domains(next)+" · голос "+next.Voice+" · ограничения zapret "+next.Strict);
            }catch(Exception ex){Error(ex);}finally{busy=false;RefreshState();}
        }
        async Task ChangeBackend() {
            bool nextProxy=backendChoice.SelectedIndex==1;if(nextProxy==ProxyMode)return;
            if(busy || starting || scanning || testing || backendChanging || checkingUpdates || appUpdates.Working){RefreshBackendControls();return;}
            string previousBackend=store.Settings.Backend;var savedView=dashboard.CaptureResults();savedView.Configuration=BackendConfiguration();if(String.IsNullOrEmpty(savedView.Context))savedView.Context=ProxyMode?"ByeDPI · "+store.Settings.ProxyStrategy:"zapret · "+Theme.StrategyName(store.Settings.Strategy);backendResults[previousBackend]=savedView;backendTransition.BeginCapture(dashboard);
            backendChanging=busy=true;problemNotice.Clear();RefreshState();
            try {
                await StopChecks();if(runner.OwnsSession)await Task.Run(()=>StopAllCore());await InspectFootprint();
                store.Settings.Backend=nextProxy?"byedpi":"zapret";store.Save();activeStrategy=nextProxy?"ByeDPI · выключен":store.Settings.Strategy;
                previouslyOn=false;checkEpoch++;DashboardSnapshot saved;backendResults.TryGetValue(store.Settings.Backend,out saved);if(saved!=null && saved.Configuration!=BackendConfiguration())saved=null;dashboard.RestoreResults(saved);
                Append("Выбран движок: "+(nextProxy?"ByeDPI · отдельный браузер без драйвера":"zapret · системный WinDivert"));
            }catch(Exception ex){Error(ex);}finally{backendChanging=busy=false;RefreshState();backendTransition.Reveal();}
        }
        void StopAllCore() {
            cleanupFailure=null;
            try{
            CloseProxyBrowsers();if(youtubeDns!=null){youtubeDns.Dispose();youtubeDns=null;}
            runner.Stop();runner.SetTelegram(store.Current,false);TelegramFilter.Set(store.Current,false);DiscordFilter.Set(store.Current,false);
            if(runner.StartupEnabled)runner.SetStartup(false,store.Current,store.Settings.Strategy);
            if(!StartupRegistration.RemoveOwned(Application.ExecutablePath))Append("Автозапуск другой копии ByPass оставлен без изменений.");store.Settings.StartWithWindows=false;store.Save();runner.ReleaseIfIdle();
            }catch(Exception ex){cleanupFailure=ex.Message;throw;}
        }
        async Task StopEverything() {
            if(scanning){stopAllAfterScan=true;stopAfterScan=true;if(scanCancellation!=null)scanCancellation.Cancel();return;}
            if(busy || starting || appUpdates.Working)return;
            busy=true;problemNotice.Clear();RefreshState();try{await StopChecks();await Task.Run(()=>StopAllCore());InvalidateChecks("Все обходы ByPass выключены · можно сравнить работу игры");Append("Собственные обходы и автозапуск выключены. Проверяем оставшиеся компоненты.");}
            catch(Exception ex){Error(ex);}await InspectFootprint();busy=false;RefreshState();
        }
        async Task InspectFootprint() {
            if(footprintBusy)return;footprintBusy=true;
            try{footprint=await Task.Run(()=>NetworkFootprint.Read());string warning=cleanupFailure??runner.StopWarning;if(!String.IsNullOrEmpty(warning)){footprint.Verified=false;footprint.Items.Add("Полная остановка не подтверждена: "+warning);}Append("Проверка остановки: "+footprint.Summary);foreach(string item in footprint.Items)Append("  "+item);}
            catch(Exception ex){footprint=new NetworkFootprint{Verified=false};footprint.Items.Add(ex.Message);Append("Не удалось проверить остановку: "+ex.Message);}
            finally{footprintBusy=false;if(!IsDisposed)RefreshBackendControls();}
        }
        async Task SetProxyEnabled(bool enabled) {
            if(loading || busy || starting || scanning || appUpdates.Working){RefreshState();return;}
            busy=starting=true;problemNotice.Clear();RefreshState();
            try{
                await StopChecks();await Task.Run(()=>StopAllCore());
                if(enabled){string profile=ProxyBypass.Profiles[Math.Max(0,proxyProfile.SelectedIndex)];store.Settings.ProxyStrategy=profile;store.Save();await Task.Run(()=>runner.StartProxy(store.Root,profile,CurrentScope()));activeStrategy="ByeDPI · "+profile;Append("Откройте YouTube или Discord кнопкой в ByPass. Системный прокси не изменён.");}
                InvalidateChecks(enabled?"ByeDPI включён · проверяем его маршрут":"ByeDPI выключен");
            }catch(Exception ex){Error(ex);}finally{busy=starting=false;}
            if(!enabled)await InspectFootprint();RefreshState();
            if(enabled && runner.ProxyRunning)await RunChecks();
        }
        void OpenProxyBrowser(string url) {
            if(!runner.ProxyRunning)throw new InvalidOperationException("Сначала включите ByeDPI.");
            if(!ServiceScope.Allows(new Uri(url).Host,CurrentScope()))throw new InvalidOperationException("Включите этот сервис в «Настройки → Область обхода».");
            string browser=YouTubeDnsSession.Browser(),profile=Path.Combine(store.Root,"data","byedpi-browser","session-"+Guid.NewGuid().ToString("N"));Trust.NoLinks(profile);Directory.CreateDirectory(profile);
            string args=YouTubeDnsSession.Arguments(profile,runner.ProxyPort).Replace("https://www.youtube.com/",url)+" --force-webrtc-ip-handling-policy=disable_non_proxied_udp";
            var process=Process.Start(new ProcessStartInfo(browser,args){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(browser)});
            try{proxyBrowsers.Add(new OwnedJob(process));proxyBrowserProcesses.Add(process);}catch{try{if(!process.HasExited)process.Kill();}finally{process.Dispose();}throw;}
        }
        void CloseProxyBrowsers(){foreach(var job in proxyBrowsers)job.Dispose();proxyBrowsers.Clear();foreach(var process in proxyBrowserProcesses){try{if(!process.HasExited && !process.WaitForExit(5000))throw new EngineStopException("Отдельное окно браузера ещё закрывается.");}finally{process.Dispose();}}proxyBrowserProcesses.Clear();}
        void RefreshBackendControls() {
            if(IsDisposed || Disposing || backendChoice==null || backendChoice.IsDisposed)return;bool previous=loading;loading=true;
            try {
                if(!backendChanging)backendChoice.SelectedIndex=ProxyMode?1:0;strictScope.Checked=store.Settings.StrictScope;scopeYouTube.Checked=CurrentScope().YouTube;scopeDiscord.Checked=CurrentScope().Discord;scopeVoice.Checked=store.Settings.ScopeVoice;
                bool idle=!busy && !starting && !scanning && !testing && !appUpdates.Working;
                backendChoice.Enabled=idle && !checkingUpdates;stopEverything.Enabled=(!busy && !starting && !appUpdates.Working) || scanning;
                strictScope.Enabled=scopeYouTube.Enabled=scopeDiscord.Enabled=scopeVoice.Enabled=idle && !runner.Running && !runner.TelegramRunning && !runner.StartupEnabled;
                scopeVoice.Enabled=scopeVoice.Enabled && !ProxyMode && store.Settings.ScopeDiscord;
                inspectFootprint.Enabled=!footprintBusy && idle;footprintText.Text=footprint==null?"Проверка покажет и остановленные службы. Чужие компоненты выводятся в журнале.":footprint.Summary+(footprint.Clean?"\nФайлы программы и настройки сохраняются.":"\nПодробности и пути — в журнале. Чужие службы автоматически не удаляются.");footprintText.ForeColor=footprint==null?Theme.Muted:footprint.Clean?Theme.Success:Theme.Warning;
                ApplyBackendView();
                proxyProfile.Enabled=applyProxy.Enabled=idle;openProxyYouTube.Enabled=idle && runner.ProxyRunning && CurrentScope().YouTube;openProxyDiscord.Enabled=idle && runner.ProxyRunning && CurrentScope().Discord;
                if(ProxyMode){telegram.Enabled=false;startWindows.Enabled=false;dashboard.Apply.Enabled=dashboard.Scan.Enabled=false;dashboard.SetFeedback(false);}
                if(ProxyMode || store.Settings.StrictScope){games.Enabled=tcp.Enabled=udp.Enabled=saveGameButton.Enabled=false;gameSaved.Text="Не используется в выбранном режиме";}
                if(ProxyMode)domainEditor.SetAllowed(false,false);
                telegramStatus.Text=ProxyMode?"Для моста Telegram выберите zapret на главной вкладке.":telegramChanging?"Изменяем подключение…":runner.TelegramRunning?"Мост включён · WinDivert используется":"Мост выключен";
                if(!runner.Running && !runner.TelegramRunning && footprint!=null && !footprint.Clean){state.Text="Обход ByPass выключен · есть компоненты";state.ForeColor=Theme.Warning;subtitle.Text=footprint.Summary+" · подробности в настройках";}
                else if(ProxyMode){state.Text=runner.ProxyRunning?"ByeDPI включён":"ByeDPI выключен";subtitle.Text=runner.ProxyRunning?"Только отдельное окно · "+store.Settings.ProxyStrategy:"YouTube и Discord в браузере · без системного прокси";}
                else if(!runner.Running && runner.TelegramRunning){state.Text="Основной обход выключен";subtitle.Text="Telegram работает в своей вкладке · WinDivert используется";}
            }finally{loading=previous;}
        }
        int[] ConnectionTargets(){var scope=CurrentScope();return Enumerable.Range(0,ServiceChecks.Targets.Length).Where(i=>ServiceChecks.Targets[i].Kind!=ProbeKind.Telegram && !ServiceChecks.Targets[i].Name.StartsWith("GitHub") && (!scope.Strict || ServiceScope.Allows(new Uri(ServiceChecks.Targets[i].Url).Host,scope))).ToArray();}
        string BackendConfiguration(){var scope=CurrentScope();return store.Settings.Backend+"|"+(ProxyMode?store.Settings.ProxyStrategy:store.Settings.Engine+"|"+store.Settings.Strategy)+"|"+scope.Strict+"|"+scope.YouTube+"|"+scope.Discord+"|"+scope.Voice;}
        void ApplyBackendView(){
            bool proxy=ProxyMode;if(renderedBackend!=proxy){
                dashboard.SuspendLayout();try{if(renderedBackend==null)dashboard.ClearResults(proxy?"Включите ByeDPI или проверьте прямое соединение":"Примените стратегию zapret или проверьте прямое соединение");profiles.Parent.Visible=!proxy;proxyPane.Visible=proxy;proxyPane.Bounds=profiles.Parent.Bounds;if(proxy)proxyPane.BringToFront();if(backendTransition.Active)backendTransition.BringToFront();dashboard.SetProxyMode(proxy);
                    strictScope.Visible=scopeVoice.Visible=!proxy;scopeTitle.Text=proxy?"Настройки ByeDPI":"Настройки zapret";scopeSurface.Height=Px(proxy?342:388);
                    scopeHint.Text=proxy?"Выбранные сервисы открываются в отдельном окне через локальный прокси.\nУстановленный Discord и голос не подключаются.\nЭти настройки сохраняются отдельно от zapret.":"В ограниченном режиме общие CDN/IPSet, GameFilter и TCP timestamps исключены.\nГолос использует более широкое UDP-правило.\nДля проверки игр оставьте его выключенным.";
                    if(startupBackendCard!=null)startupBackendCard.Visible=!proxy;if(coreBackendCard!=null)coreBackendCard.Visible=!proxy;if(coreBackendGap!=null)coreBackendGap.Visible=!proxy;tabs.SetPageAvailable(4,!proxy);
                    discord.Text=proxy?"Включить ByeDPI":"Включить zapret";renderedBackend=proxy;
                }finally{dashboard.ResumeLayout(true);}
            }
            var targets=ConnectionTargets();string signature=store.Settings.Backend+":"+String.Join(",",targets);if(renderedTargets!=signature){renderedTargets=signature;dashboard.ConfigureTargets(proxy?"ByeDPI":"zapret",targets);}
        }
    }
}
