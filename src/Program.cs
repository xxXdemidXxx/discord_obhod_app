using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZapretDesktop {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(s,e)=>{
                var window=Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if(window!=null){try{window.ReportProblem(e.Exception);}catch{Trace.TraceError(e.Exception.ToString());}}
                else Trace.TraceError(e.Exception.ToString());
            };
            if (args.Length >= 3 && args[0] == "--apply-update") { ShellUpdates.Apply(args); return; }
            bool created;
            using (var mutex = new Mutex(true, "Local\\ZapretDesktop-Flowseal", out created)) {
                if (!created) {
                    if (!args.Contains("--connect") && !args.Contains("--autoselect")) { if(!args.Contains("--tray"))AppActivation.ShowExisting(); return; }
                    try { if (!mutex.WaitOne(10000)) return; } catch (AbandonedMutexException) { }
                }
                try { Application.Run(new MainForm(new Store(AppDomain.CurrentDomain.BaseDirectory), args.Contains("--connect"), args.Contains("--autoselect"), args.Contains("--restore-connect"), args.Contains("--full-scan"),startInTray:args.Contains("--tray"))); }
                catch (Exception ex) {
                    try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","app.log"),DateTime.Now.ToString("s")+" Запуск ByPass: "+ex+Environment.NewLine);}catch{}
                    MessageBox.Show("Не удалось открыть ByPass.\nРаспакуйте полный ZIP программы в новую папку и запустите ByPass.exe.\nЕсли это повторяется, передайте разработчику файл data/app.log.", "ByPass", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed partial class MainForm : Form {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        readonly Store store; readonly StrategyFeedback strategyFeedback; readonly EngineRunner runner = new EngineRunner(); readonly Updater updater;
        Color background {get{return Theme.Background;}} Color card {get{return Theme.Surface;}} Color muted {get{return Theme.Muted;}} Color accent {get{return Theme.Accent;}}
        Label state, subtitle, version, updateText; Button connect, install, rollback, check;
        DarkSelect profiles, categories, games; CheckBox autoCheck; TextBox tcp, udp, log;
        Label[] checkStatuses, checkTimes; Label checkSummary; Button runChecks;
        DarkTabs tabs; bool starting;
        Dashboard dashboard; bool scanning, dnsBusy, pendingExit, stopAfterScan;
        YouTubeDnsSession youtubeDns; readonly CancellationTokenSource youtubeCancellation=new CancellationTokenSource();
        string activeStrategy,scanStep=""; int checkEpoch; Task manualCheckTask;
        CancellationTokenSource scanCancellation;
        readonly AppUpdateSession appUpdates;
        readonly Func<string,string,bool> confirmUpdate;
        UpdateNotice updateNotice;
        ProblemNotice problemNotice;
        Label ownerMark;
        AppUpdateCard appUpdateCard; Button coreManual; CheckBox autoShell; bool updateOutcome;
        CheckBox autoInstallShell,autoInstallCore,startWindows; Label startupStatus;
        bool startupChanging,startupPolling; string startupMessage="",startupReadError="";
        bool previouslyOn;
        CancellationTokenSource checkCancellation; bool testing, checkingUpdates;
        NotifyIcon tray; System.Windows.Forms.Timer timer;
        Release release; bool busy, exiting, loading;
        public MainForm(Store store, bool start, bool auto = false, bool restoreConnection = false, bool fullScan = false, ShellUpdates shellUpdates = null, Func<string,string,bool> updateConfirmation = null,Updater coreUpdates=null, bool startInTray=false) {
            this.store = store; strategyFeedback=new StrategyFeedback(store); updater = coreUpdates??new Updater(store);appUpdates=new AppUpdateSession(shellUpdates??new ShellUpdates());
            confirmUpdate=updateConfirmation??((title,message)=>MessageBox.Show(this,message,title,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes);
            Theme.Use(store.Settings.VisualTheme);
            runner.RefreshStartup();
            activeStrategy = runner.StartupEnabled?runner.StartupStrategy:store.Settings.Strategy;
            BuildInterface();if(startInTray){Opacity=0;ShowInTaskbar=false;Shown+=(s,e)=>{Hide();Opacity=1;};}
            if(fullScan)dashboard.ScanMode.SelectedIndex=1;
            tray = new NotifyIcon { Icon = Icon, Text = "ByPass — выключен", Visible = true };
            var menu = new ContextMenuStrip(); menu.Items.Add("Открыть", null, (s,e) => Restore()); menu.Items.Add("Включить / выключить", null, async (s,e) => await Toggle()); menu.Items.Add("Выход", null, (s,e) => Exit()); tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Restore();
            runner.Log += Append; timer = new System.Windows.Forms.Timer { Interval = 800 };
            timer.Tick += async (s,e) => await PollStartup();timer.Start();
            appUpdates.Changed+=AppUpdateChanged;
            LoadSettings();
            RenderOwner(OwnerIdentity.Current(Application.ExecutablePath));
#if !TESTING
            Shown+=async(s,e)=>{bool verified=await OwnerIdentity.RestoreProof(Application.ExecutablePath);if(!IsDisposed)RenderOwner(verified);};
#endif
            var outcome=ShellUpdates.TakeOutcome(store.Root);updateOutcome=outcome!=null;
            if(outcome!=null){if(outcome.Success && outcome.Version==ShellUpdates.Current)appUpdates.Complete();else {appUpdates.Fail("Обновление не установлено. Текущая версия сохранена.");Append("Установка обновления: "+outcome.Error);}tabs.SelectedIndex=2;}
            Shown += async (s,e) => {
                Append("ByPass "+ShellUpdates.Current+". Ядро "+Updater.Version(store.Current));
                bool earlyUpdates=store.Settings.AutoInstallShell || store.Settings.AutoInstallCore;
                if(earlyUpdates){if((store.Settings.AutoAppUpdates || store.Settings.AutoInstallShell) && !updateOutcome)await CheckShell(true);if(IsDisposed || exiting)return;if(store.Settings.CheckUpdates || store.Settings.AutoInstallCore)await CheckUpdates(true);if(IsDisposed || exiting)return;}
                if(auto)await AutoSelect(restoreConnection);else if(start)await ApplyStrategy();
                if(!earlyUpdates && !IsDisposed && store.Settings.AutoAppUpdates && !updateOutcome)await CheckShell(true);
                if(!earlyUpdates && !IsDisposed && store.Settings.CheckUpdates)await CheckUpdates(true);
            };
            FormClosing += (s,e) => { if (!exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }
        Label Label(string text, float size, Color color, int x, int y, int width, int height) {
            return new Label { Text = text, Font = new Font("Segoe UI", size), ForeColor = color, Location = new Point(x,y), Size = new Size(width,height) };
        }
        void RenderOwner(bool verified) {
            ownerMark.Text="ByPass "+ShellUpdates.Current+" · "+(verified?"Автор: "+OwnerIdentity.Owner+" · Подпись сборки подтверждена":"Подпись автора не подтверждена");
            ownerMark.ForeColor=verified?Theme.Success:Theme.Muted;
        }
        Button Button(string text, Color color, int x, int y, int width, int height) {
            var b = new FlatAction { Text = text, BackColor = color, ForeColor = color == accent ? background : Color.White, FlatStyle = FlatStyle.Flat, Location = new Point(x,y), Size = new Size(width,height), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0; return b;
        }
        Panel Page(string text) { return new Panel { Text = text, BackColor = background, ForeColor = Color.White, AutoScroll = false, Padding = new Padding(Px(24)) }; }
        TextBox Input(int x, int y, int width) { return new TextBox { Location = new Point(x,y), Size = new Size(width,32), BackColor = card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle }; }
        void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        void Error(Exception ex) { Append("Ошибка: " + ex); problemNotice.ShowProblem(ex); }
        internal void ReportProblem(Exception ex){Error(ex);}
        public void Append(string line) {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), line); } catch (InvalidOperationException) {} return; }
            var text = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
            if (log.TextLength > 100000) log.Clear(); log.AppendText(text);
            try { var file = Path.Combine(store.Root, "data", "app.log"); if (File.Exists(file) && new FileInfo(file).Length > 2000000) File.Move(file, file + "." + DateTime.Now.ToString("yyyyMMddHHmmss")); File.AppendAllText(file, text); } catch { }
        }
        void LoadSettings() {
            loading = true;
            try {
                categories.SelectedIndex = 0;
                profiles.Items.Clear(); profiles.Items.AddRange(store.Strategies());
                profiles.SelectedItem = store.Settings.Strategy; if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0;
                string t,u; Strategy.GamePorts(store.Current, out t, out u); games.SelectedIndex = t == "12" ? (u == "12" ? 0 : 3) : (u == "12" ? 2 : 1);
                tcp.Text = t == "12" ? "1024-65535" : t; udp.Text = u == "12" ? "1024-65535" : u;
                autoCheck.Checked = store.Settings.CheckUpdates; version.Text = "Ядро " + Updater.Version(store.Current);
                autoShell.Checked = store.Settings.AutoAppUpdates; domainEditor.LoadEngine(store.Current);
                autoInstallShell.Checked=store.Settings.AutoInstallShell;autoInstallCore.Checked=store.Settings.AutoInstallCore;
                startWindows.Checked=runner.StartupEnabled;
            } finally { loading = false; RefreshState(); }
        }
        async Task PollStartup(){
            if(IsDisposed || startupPolling || busy || starting || scanning)return;
            startupPolling=true;
            try{await Task.Run(()=>runner.RefreshStartup());startupReadError="";}
            catch(Exception ex){startupReadError="Не удалось проверить автозапуск. Повторите попытку.";Trace.TraceError(ex.Message);}
            finally{startupPolling=false;if(!IsDisposed)RefreshState();}
        }
        async Task SetWindowsStartup() {
            if(loading)return;
            if(busy || starting || scanning || startupChanging){RefreshState();return;}
            bool enabled=startWindows.Checked,reconnect=runner.Running,changed=false,registrationChanged=false;
            string previousRegistration=null;
            string engine=store.Current,strategy=reconnect?activeStrategy:(profiles.SelectedItem==null?store.Settings.Strategy:profiles.SelectedItem.ToString());
            startupChanging=true;busy=true;startupMessage="";RefreshState();
            try {
                Exception failure=null;
                try {
                    await StopChecks();
                    await Task.Run(()=>{
                        if(enabled){Trust.VerifyEngine(engine);Strategy.Arguments(engine,strategy);}
                        previousRegistration=StartupRegistration.Read();
                        if(enabled && reconnect)runner.Stop();
                        StartupRegistration.Set(enabled,Application.ExecutablePath);registrationChanged=true;
                        runner.SetStartup(enabled,engine,strategy);changed=true;
                    });
                    store.Settings.StartWithWindows=runner.StartupEnabled;store.Settings.Strategy=strategy;store.Save();activeStrategy=strategy;
                    Append(enabled?"Запуск при старте Windows включён. При выходе из ByPass обход продолжит работать.":"Запуск при старте Windows выключен.");
                    if(!enabled && reconnect)await StartEngine(strategy,CancellationToken.None);
                }catch(Exception ex){failure=ex;}
                if(failure!=null){
                    if(!changed && registrationChanged){
                        try{await Task.Run(()=>StartupRegistration.Write(previousRegistration));}catch(Exception rollbackError){Append(rollbackError.Message);}
                    }
                    var cancelled=failure.GetBaseException() as System.ComponentModel.Win32Exception;
                    if(cancelled!=null && cancelled.NativeErrorCode==1223){startupMessage="Изменение отменено · автозапуск "+(runner.StartupEnabled?"включён":"выключен");Append(startupMessage);}
                    else{startupMessage="Не удалось изменить автозапуск. Повторите попытку.";Error(failure);}
                    if(reconnect && !runner.Running && !runner.StartupEnabled){try{await StartEngine(strategy,CancellationToken.None);}catch(Exception restart){Error(restart);}}
                }
            }finally{startupChanging=false;busy=false;if(!IsDisposed)RefreshState();}
        }
        void FilterProfiles() {
            var selected = profiles.SelectedItem == null ? store.Settings.Strategy : profiles.SelectedItem.ToString();
            int group = categories.SelectedIndex;
            var list = store.Strategies().Where(x => group == 0 || (group == 1 && x == "general.bat") || (group == 2 && x.StartsWith("general (ALT")) ||
                (group == 3 && x.Contains("FAKE TLS AUTO")) || (group == 4 && x.Contains("SIMPLE FAKE")) || (group == 5 && x.Contains("EXP"))).ToArray();
            profiles.Items.Clear(); profiles.Items.AddRange(list); profiles.SelectedItem = selected;
            if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0;
        }
        void SaveGame() {
            if (runner.Running || runner.Conflict || busy || scanning || starting) throw new InvalidOperationException("Сначала выключите обход.");
            if (!Strategy.ValidPorts(tcp.Text.Trim()) || !Strategy.ValidPorts(udp.Text.Trim())) throw new InvalidDataException("Проверьте диапазоны портов: допустимы числа от 1 до 65535.");
            var path = Path.Combine(store.Current, "utils", "game_filter.enabled");
            if (games.SelectedIndex == 0) { if (File.Exists(path)) File.Delete(path); }
            else {
                var mode = new[] { "disabled", "all", "tcp", "udp" }[games.SelectedIndex];
                if (Updater.Version(store.Current) == "1.10.2") {
                    if (tcp.Text.Trim() != "1024-65535" || udp.Text.Trim() != "1024-65535") throw new InvalidOperationException("Свои диапазоны требуют ядро 1.10.3 или новее.");
                    File.WriteAllText(path, mode + "\r\n");
                } else File.WriteAllText(path, "mode=" + mode + "\r\ntcp=" + tcp.Text.Trim() + "\r\nudp=" + udp.Text.Trim() + "\r\n");
            }
            runner.SyncStartup(store.Current,store.Settings.Strategy);Append("Настройки GameFilter сохранены.");
        }
        async Task Toggle() {
            if (busy || starting) return;
            if(scanning){stopAfterScan=true;if(scanCancellation!=null)scanCancellation.Cancel();return;}
            try {
                if(runner.Running){starting=true;InvalidateChecks("Обход выключен · запустите новую проверку");await StopChecks();runner.Stop();return;}
                await ApplyStrategy();
            }catch(Exception ex){Error(ex);}finally{starting=false;if(!IsDisposed)RefreshState();}
        }
        async Task ApplyStrategy() {
            if(busy || starting || scanning)return;
            problemNotice.Clear();
            starting=true;RefreshState();
            try {
                if (profiles.SelectedItem == null) throw new InvalidOperationException("Нет стратегий. Проверьте папку data/engines.");
                if(runner.Conflict)throw new InvalidOperationException("Сначала завершите внешний winws через его исходное приложение.");
                InvalidateChecks("Применяем стратегию…");await StopChecks();runner.Stop();
                store.Settings.Strategy = profiles.SelectedItem.ToString(); store.Save();
                await StartEngine(store.Settings.Strategy,CancellationToken.None);
                starting=false;RefreshState();await RunChecks();
            }catch(Exception ex){if(!IsDisposed)Error(ex);}finally{starting=false;if(!IsDisposed)RefreshState();}
        }
        async Task StartEngine(string strategy,CancellationToken token) {
            token.ThrowIfCancellationRequested();await Task.Delay(150,token);
            activeStrategy=strategy;await Task.Run(()=>runner.Start(store.Current,strategy));RefreshState();
            try{await EngineRunner.WaitForReady(()=>runner.Running,()=>runner.Ready,token,10000,()=>runner.DriverFailure);}catch{runner.Stop();throw;}
            if(!runner.Running)throw new IOException("Ядро завершилось после запуска. Подробности в журнале.");
        }
        void InvalidateChecks(string reason) {checkEpoch++;dashboard.ClearResults(reason);}
        async Task StopChecks() {
            if(checkCancellation!=null)checkCancellation.Cancel();
            if(manualCheckTask!=null){try{await manualCheckTask;}catch(OperationCanceledException){}}
        }
        async Task AutoSelect(bool restoreConnection) {
            if(scanning || busy || starting || dnsBusy)return;
            problemNotice.Clear();
            if(runner.Conflict){Error(new InvalidOperationException("Завершите внешний winws перед автоподбором."));return;}
            bool wasRunning=runner.Running || restoreConnection;
            string original=runner.Running?activeStrategy:store.Settings.Strategy;
            bool quick=dashboard.ScanMode.SelectedIndex==0;
            try{Strategy.ValidateEngine(store.Current);Trust.VerifyEngine(store.Current);}catch(Exception ex){Error(new EngineSetupException("Не удалось подготовить ядро.\n"+ex.Message,ex));return;}
            scanning=true;stopAfterScan=false;scanCancellation=new CancellationTokenSource();var token=scanCancellation.Token;
            var names=strategyFeedback.Order(store.Strategies(),original);dashboard.ResetHistory(names);RefreshState();bool chosen=false,setupFailed=false;
            Append("Порядок подбора: "+String.Join(" → ",names));
            try {
                InvalidateChecks("Автоподбор начинается…");await StopChecks();runner.Stop();
                var result=await StrategySelector.Run(names,
                    async (name,cancel)=>{InvalidateChecks("Проверяется: "+name.Replace(".bat",""));await StartEngine(name,cancel);},
                    ()=>runner.Stop(),
                    async cancel=>await ServiceChecks.Batch(cancel,(index,item)=>{if(!IsDisposed)dashboard.SetResult(index,item);},quick?6:12),token,
                    (index,total,name)=>{if(IsDisposed)return;scanStep=index+" / "+total+" · "+Theme.StrategyName(name);dashboard.ScanSummary.Text=scanStep;dashboard.Progress(name);RefreshState();},
                    candidate=>{if(IsDisposed)return;dashboard.Record(candidate);checkSummary.Text=candidate.Strategy.Replace(".bat","")+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length;Append("Автоподбор: "+candidate.Strategy+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length+(candidate.Error.Length>0?" · "+candidate.Error:""));for(int i=0;i<candidate.Results.Length;i++)Append("  "+ServiceChecks.Targets[i].Name+": "+candidate.Results[i].Detail+" · "+candidate.Results[i].Milliseconds+" мс");},quick,async cancel=>await ServiceChecks.Batch(cancel,(index,item)=>{if(!IsDisposed)dashboard.SetResult(index,item);}),strategyFeedback.Preferred(original));
                if(result.Winner!=null) {
                    store.Settings.Strategy=result.Winner.Strategy;store.Save();activeStrategy=result.Winner.Strategy;chosen=true;
                    loading=true;categories.SelectedIndex=0;profiles.Items.Clear();profiles.Items.AddRange(names);profiles.SelectedItem=activeStrategy;loading=false;
                    for(int i=0;i<result.Winner.Results.Length;i++)dashboard.SetResult(i,result.Winner.Results[i]);
                    checkSummary.Text="Сетевые пробы пройдены · проверьте Discord";
                    dashboard.ScanSummary.Text="Выбран для проверки: "+Theme.StrategyName(activeStrategy);
                }else {
                    bool dnsFailure=result.Candidates.Any(x=>x.Results.Any(y=>y.Failure=="dns"));
                    dashboard.ScanSummary.Text="Полного результата не найдено."+(dnsFailure?"\nЕсть ошибки DNS — откройте диагностику.":"\nРезультаты всех стратегий — в таблице.");
                }
            }catch(OperationCanceledException){if(!IsDisposed)dashboard.ScanSummary.Text="Автоподбор отменён.";}
            catch(Exception ex){setupFailed=ex is EngineSetupException;if(!IsDisposed){dashboard.ScanSummary.Text=ex is EngineSetupException?"Подбор остановлен: не удалось запустить ядро.\nПодробности — в журнале.":"Автоподбор завершился с ошибкой.";Error(ex);}}
            {
                if(!chosen) {
                    try {
                        runner.Stop();activeStrategy=original;store.Settings.Strategy=original;
                        if(!IsDisposed && !pendingExit && !stopAfterScan && !setupFailed && wasRunning && !strategyFeedback.IsRejected(original)){await StartEngine(original,CancellationToken.None);InvalidateChecks("Возвращена "+original.Replace(".bat","")+" · проверка требуется заново");}
                        else if(!IsDisposed){runner.SyncStartup(store.Current,original);InvalidateChecks("Обход выключен · выберите стратегию из таблицы");}
                    }catch(Exception ex){if(!IsDisposed)Append("Восстановление стратегии: "+ex.Message);}
                }
                scanning=false;scanCancellation.Dispose();scanCancellation=null;
                if(!IsDisposed){RefreshState();if(pendingExit)Exit();}
            }
        }
        void ConfirmStrategy() {
            if(!runner.Running || !runner.Ready || scanning || starting || testing || busy)return;
            strategyFeedback.Accept(activeStrategy);
            checkSummary.Text="Вы подтвердили работу: "+Theme.StrategyName(activeStrategy);
            dashboard.ScanSummary.Text="Подтверждённый вариант сохранён.";
            Append("Пользователь подтвердил работу стратегии: "+activeStrategy);
        }
        async Task NextStrategy() {
            if(!runner.Running || scanning || starting || testing || busy)return;
            strategyFeedback.Reject(activeStrategy);
            Append("Пользователь отклонил стратегию: "+activeStrategy+" · продолжаем подбор");
            await StopChecks();runner.Stop();await AutoSelect(false);
        }
        async Task CheckUpdates(bool quiet) {
            if (busy || scanning || checkingUpdates) return; checkingUpdates = true; RefreshState();
            try {
                coreManual.Tag=null;updateText.Text = "Проверяем обновления…"; release = await updater.Check(); if(IsDisposed)return;
                if (release.Version == Updater.Version(store.Current)) { updateText.Text = "Обновлений нет"; release = null; }
                else { updateText.Text = "Доступна версия " + release.Version; Append("Доступно обновление ядра " + release.Version);if(store.Settings.AutoInstallCore)await InstallCore(true); }
            } catch (Exception ex) { if(IsDisposed)return; coreManual.Tag=true;release = null; updateText.Text = ex is UpdateCheckException?ex.Message:"Не удалось проверить обновления. Попробуйте позже."; Append("Проверка обновлений: " + ex.Message); }
            finally { checkingUpdates = false; RefreshState(); }
        }
        Task RunChecks() {
            if(testing)return manualCheckTask ?? Task.FromResult(0);
            if(busy || scanning || starting)return Task.FromResult(0);
            manualCheckTask=RunChecksCore();return manualCheckTask;
        }
        async Task RunChecksCore() {
            testing=true;checkCancellation=new CancellationTokenSource();var localCancellation=checkCancellation;var token=localCancellation.Token;
            int epoch=++checkEpoch;
            var engineState=runner.Running?activeStrategy.Replace(".bat",""):(runner.Conflict?"внешний winws":"обход выключен");
            dashboard.ClearResults("Проверяем: "+engineState+"…");RefreshState();
            try {
                var results=await ServiceChecks.Batch(token,(index,item)=>{if(IsDisposed || epoch!=checkEpoch)return;dashboard.SetResult(index,item);Append(ServiceChecks.Targets[index].Name+": "+item.Detail+" · "+item.Milliseconds+" мс");});
                if(!IsDisposed && epoch==checkEpoch){checkSummary.Text="Сетевых проб: "+results.Count(x=>x.State==1)+" из "+ServiceChecks.Targets.Length+(!runner.Running && !runner.Conflict?" · обход выключен":results.All(x=>x.State==1)?" · проверьте Discord":"");if(results.Any(x=>x.Failure=="dns"))dashboard.ScanSummary.Text="Есть ошибка DNS Windows.\nНажмите «Диагностика DNS».";else if(results.Any(x=>x.State==1 && x.DnsRoute=="doh"))dashboard.ScanSummary.Text="YouTube доступен через DoH.\nВ браузере включите безопасный DNS.";}
            }catch(OperationCanceledException){}catch(Exception ex){if(!IsDisposed)Error(ex);}
            finally{testing=false;localCancellation.Dispose();if(checkCancellation==localCancellation)checkCancellation=null;if(!IsDisposed)RefreshState();}
        }
        async Task DiagnoseDns() {
            if(dnsBusy || scanning || busy)return;dnsBusy=true;RefreshState();
            try {
                var report=await DnsDiagnostics.Run(CancellationToken.None);if(IsDisposed)return;Append(report);
                string helpTopic=null;
                using(var dialog=new DnsDiagnosticDialog(report,OpenYouTubeDns,topic=>helpTopic=topic))dialog.ShowDialog(this);
                if(helpTopic!=null && !IsDisposed)ShowHelp(helpTopic);
            }catch(Exception ex){if(!IsDisposed)Error(ex);}finally{dnsBusy=false;if(!IsDisposed)RefreshState();}
        }
        async Task OpenYouTubeDns() {
            bool created=youtubeDns==null;
            if(created)youtubeDns=new YouTubeDnsSession(store.Root);
            try {
                await youtubeDns.Open(youtubeCancellation.Token);
                Append("Открыто отдельное окно YouTube: DNS через HTTPS; DNS Windows и основной профиль браузера не изменены.");
            }catch(Exception ex) {
                if(created){youtubeDns.Dispose();youtubeDns=null;}
                Append("Отдельное окно YouTube: "+ex.GetBaseException().Message);throw;
            }
        }
        Task Install(){return InstallCore(false);}
        async Task InstallCore(bool automatic) {
            if (busy || scanning || starting || domainEditor.Dirty || release == null) return;
            if(automatic && !store.Settings.AutoInstallCore)return;
            if (runner.Conflict || (!automatic && runner.Running)) { if(!automatic)Error(new InvalidOperationException("Сначала выключите обход."));else Append("Автообновление ядра отложено: запущен другой обход.");return; }
            if(!automatic && !confirmUpdate("Обновление ядра","Установить новую версию ядра "+release.Version+"?\n\nВаши настройки и списки сохранятся. Предыдущую версию можно будет вернуть."))return;
            bool reconnect=runner.Running;
            busy = true; RefreshState();
            try {
                try { await StopChecks(); if(reconnect)runner.Stop();InvalidateChecks("Обновляем ядро…"); await updater.Install(release, message => {updateText.Text = message; Append(message);}); runner.SyncStartup(store.Current,store.Settings.Strategy);release = null; LoadSettings(); }
                catch (Exception ex) { updateText.Text = "Обновление не установлено. Текущее ядро сохранено."; if(!IsDisposed)Error(ex); }
                if(reconnect && !IsDisposed && !exiting){try{await StartEngine(store.Settings.Strategy,CancellationToken.None);}catch(Exception ex){Error(ex);}}
            }finally{busy=false;if(!IsDisposed)RefreshState();}
        }
        void RefreshState() {
            if (IsDisposed) return;
            bool on = runner.Running, external = !on && runner.Conflict;
            startWindows.Enabled=!busy && !starting && !scanning && !testing && !dnsBusy && !appUpdates.Working && !external && !domainEditor.Dirty;
            if(!startupChanging){bool previousLoading=loading;loading=true;startWindows.Checked=runner.StartupEnabled;loading=previousLoading;}
            startupStatus.Text=startupChanging?(startWindows.Checked?"Включаем… Подтвердите запрос Windows, если он появится.":"Выключаем… Подтвердите запрос Windows, если он появится."):
                startupMessage.Length>0?startupMessage:startupReadError.Length>0?startupReadError:runner.StartupEnabled?(on?"Автозапуск включён · обход работает":"Автозапуск включён · обход запустится при старте Windows"):"Автозапуск выключен";
            startupStatus.ForeColor=startupChanging?Theme.Muted:startupReadError.Length>0 || startupMessage.Length>0?Theme.Warning:runner.StartupEnabled?Theme.Success:Theme.Muted;
            if(runner.StartupEnabled && on && !starting && !scanning)activeStrategy=runner.StartupStrategy;
            if(previouslyOn && !on && !scanning && !starting && !exiting) { InvalidateChecks("Ядро остановлено · проверка требуется заново"); if(checkCancellation!=null)checkCancellation.Cancel(); }
            previouslyOn=on;
            state.Text = scanning ? "Подбираем стратегию" : on ? "Обход включён" : (external ? "Другой обход запущен" : "Обход выключен"); state.ForeColor=on?Theme.Success:external?Theme.Warning:Theme.Text;
            subtitle.Text = scanning ? (scanStep.Length>0?scanStep:"Проверяем сетевые пробы…") : on ? Theme.StrategyName(activeStrategy) : (external ? "Закройте другой обход, чтобы управлять подключением здесь" : "Выберите вариант или нажмите «Подобрать автоматически»");
            connect.Text = scanning ? "Остановить подбор" : starting ? "Запуск…" : (on ? "Выключить" : (external ? "Внешний winws" : "Включить")); connect.Enabled = !busy && !external && !starting;
            profiles.Enabled = categories.Enabled = !busy && !starting && !scanning;
            games.Enabled = tcp.Enabled = udp.Enabled = saveGameButton.Enabled = !on && !external && !busy && !starting && !scanning; domainEditor.SetAllowed(!external && !busy && !starting && !scanning,on);
            dashboard.ScanMode.Enabled = !busy && !starting && !scanning;
            dashboard.SetFeedback(on && runner.Ready && !busy && !starting && !scanning && !testing && !dnsBusy && !exiting);
            dashboard.Apply.Enabled = dashboard.Scan.Enabled = !busy && !external && !starting && !scanning && !testing && !dnsBusy;
            dashboard.Cancel.Enabled = scanning && scanCancellation!=null && !scanCancellation.IsCancellationRequested;
            dashboard.Dns.Enabled = !busy && !starting && !scanning && !dnsBusy;
            check.Enabled = !busy && !scanning && !checkingUpdates; install.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && !domainEditor.Dirty && release != null;
            rollback.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && !domainEditor.Dirty && !String.IsNullOrEmpty(store.Settings.PreviousEngine);
            autoCheck.Enabled = !busy && !checkingUpdates && !store.Settings.AutoInstallCore;autoInstallCore.Enabled=!busy && !checkingUpdates; runChecks.Enabled = !busy && !starting && !scanning && !testing;
            appUpdateCard.Action.Enabled = !appUpdates.Working && !busy && !starting && !scanning && !dnsBusy && (appUpdates.Release==null || !domainEditor.Dirty);
            appUpdateCard.Render(appUpdates,appUpdates.Release!=null && domainEditor.Dirty && !appUpdates.Working?"Сохраните изменения в «Дополнительно», чтобы обновиться.":null);
            updateNotice.Render(appUpdates.Release==null?null:appUpdates.Release.Version,release==null?null:release.Version,appUpdateCard.Action.Enabled,install.Enabled,on || external);
            autoShell.Enabled = !appUpdates.Working && !busy && !store.Settings.AutoInstallShell;autoInstallShell.Enabled=!appUpdates.Working && !busy;coreManual.Visible=coreManual.Tag!=null;
            tray.Text = "ByPass — " + (on ? "включён" : "выключен");
        }
        void AppUpdateChanged() {
            if(IsDisposed || Disposing)return;
            if(InvokeRequired){try{BeginInvoke(new Action(AppUpdateChanged));}catch(InvalidOperationException){}return;}
            appUpdateCard.Render(appUpdates);tabs.SetNotice(2,appUpdates.Release!=null);RefreshState();
        }
        async Task CheckShell(bool quiet) {
            if(busy || scanning || starting || appUpdates.Working)return;
            await appUpdates.Check();
            if(appUpdates.State==AppUpdateState.Failed)Append("Проверка обновления приложения: "+appUpdates.LastError);
            if(appUpdates.State==AppUpdateState.Available)Append("Доступно обновление приложения "+appUpdates.Release.Version);
            if(!IsDisposed && appUpdates.State==AppUpdateState.Available && store.Settings.AutoInstallShell)await InstallShell(true);
        }
        Task AppUpdateAction(){return InstallShell(false);}
        async Task InstallShell(bool automatic) {
            if(appUpdates.Release==null){await CheckShell(false);return;}
            if(busy || starting || scanning || dnsBusy || appUpdates.Working || domainEditor.Dirty)return;
            if(automatic && !store.Settings.AutoInstallShell)return;
            if(!automatic && !confirmUpdate("Обновление ByPass","Обновить оболочку до версии "+appUpdates.Release.Version+"?\n\nПриложение перезапустится. Ваши настройки и списки сохранятся."))return;
            await ApplyShell();
        }
        async Task ApplyShell() {
            if(busy || starting || scanning || dnsBusy || appUpdates.Working || domainEditor.Dirty)return;
            busy=true;RefreshState();
            try {
                if(await appUpdates.Prepare(store.Root)) {
                    await StopChecks();
                    bool reconnect=runner.Running && !runner.StartupEnabled;
                    appUpdates.Installing();
                    await ShellUpdates.Launch(appUpdates.Prepared,Application.ExecutablePath,reconnect,!Visible);
                    if(!runner.StartupEnabled)runner.Stop();exiting=true;Close();
                }
            }catch(Exception ex){if(!IsDisposed){appUpdates.Fail("Не удалось установить обновление. Попробуйте ещё раз.");Append("Обновления приложения: "+ex.Message);}}
            finally{busy=false;if(!IsDisposed){RefreshState();if(pendingExit)Exit();}}
        }
        void Open(string target) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        void Restore() { ShowInTaskbar=true;Show(); WindowState = FormWindowState.Normal; Activate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AppActivation.Allow(Handle); NativeFrame.Apply(Handle); }
        protected override void WndProc(ref Message m) {
            if ((uint)m.Msg == AppActivation.Message) { Restore(); return; }
            if(m.Msg==0x83 || m.Msg==0x85){m.Result=IntPtr.Zero;return;}
            if(m.Msg==0x86){m.Result=new IntPtr(1);return;}
            base.WndProc(ref m);
            if(m.Msg==0x24){var screen=Screen.FromHandle(m.HWnd);NativeFrame.ConstrainMaximize(m.LParam,screen.Bounds,screen.WorkingArea,MinimumSize);}
            if(m.Msg==0x84 && WindowState==FormWindowState.Normal){long point=m.LParam.ToInt64();var p=PointToClient(new Point((short)(point&0xffff),(short)((point>>16)&0xffff)));int edge=Px(6);bool left=p.X<edge,right=p.X>=ClientSize.Width-edge,top=p.Y<edge,bottom=p.Y>=ClientSize.Height-edge;int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;if(hit!=0)m.Result=new IntPtr(hit);}
        }
        protected override CreateParams CreateParams {get{var cp=base.CreateParams;cp.Style=(cp.Style & ~NativeFrame.Caption)|0x40000|0x10000|0x20000;return cp;}}
        protected override void OnLoad(EventArgs e) {
            base.OnLoad(e);NativeFrame.Apply(Handle);
            var area=Screen.FromControl(this).WorkingArea;
            MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-32),Math.Min(MinimumSize.Height,area.Height-32));
            Size=new Size(Math.Min(Width,area.Width-32),Math.Min(Height,area.Height-32));
            if(StartPosition==FormStartPosition.CenterScreen)Location=new Point(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);
        }
        void Exit() {
            if(scanning){pendingExit=true;stopAfterScan=true;if(scanCancellation!=null)scanCancellation.Cancel();return;}
            if(appUpdates.State==AppUpdateState.Downloading){pendingExit=true;appUpdates.Cancel();return;}
            if(appUpdates.State==AppUpdateState.Installing){Restore();tabs.SelectedIndex=2;return;}
            if (busy) { Restore(); Error(new InvalidOperationException("Дождитесь завершения текущей операции.")); return; }
            try { if(!runner.StartupEnabled)runner.Stop(); exiting = true; Close(); } catch (Exception ex) { Error(ex); }
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                youtubeCancellation.Cancel();if(youtubeDns!=null)youtubeDns.Dispose();
                if (checkCancellation != null) checkCancellation.Cancel();
                if (scanCancellation != null) scanCancellation.Cancel();
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (tray != null) tray.Dispose();
                runner.Dispose(); updater.Dispose(); appUpdates.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
