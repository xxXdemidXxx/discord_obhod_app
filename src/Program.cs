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
            if (args.Length >= 3 && args[0] == "--apply-update") { ShellUpdates.Apply(args); return; }
            bool created;
            using (var mutex = new Mutex(true, "Local\\ZapretDesktop-Flowseal", out created)) {
                if (!created) {
                    if (!args.Contains("--connect") && !args.Contains("--autoselect")) { AppActivation.ShowExisting(); return; }
                    try { if (!mutex.WaitOne(10000)) return; } catch (AbandonedMutexException) { }
                }
                try { Application.Run(new MainForm(new Store(AppDomain.CurrentDomain.BaseDirectory), args.Contains("--connect"), args.Contains("--autoselect"), args.Contains("--restore-connect"), args.Contains("--full-scan"))); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить Zapret Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed partial class MainForm : Form {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        readonly Store store; readonly EngineRunner runner = new EngineRunner(); readonly Updater updater;
        readonly Color background = Theme.Background, card = Theme.Surface, muted = Theme.Muted, accent = Theme.Accent;
        Label state, subtitle, version, updateText; Button connect, install, rollback, check;
        DarkSelect profiles, categories, games; CheckBox autoCheck; TextBox tcp, udp, log;
        Label[] checkStatuses, checkTimes; Label checkSummary; Button runChecks;
        DarkTabs tabs; bool starting;
        Dashboard dashboard; bool scanning, dnsBusy, pendingExit, stopAfterScan;
        string activeStrategy,scanStep=""; int checkEpoch; Task manualCheckTask;
        CancellationTokenSource scanCancellation;
        readonly ShellUpdates shellUpdates = new ShellUpdates();
        Label shellText; Button shellInstall; CheckBox autoShell; string stagedShell; bool shellChecking;
        bool previouslyOn;
        CancellationTokenSource checkCancellation; bool testing, checkingUpdates;
        NotifyIcon tray; System.Windows.Forms.Timer timer;
        Release release; bool busy, exiting, loading, elevation;
        public MainForm(Store store, bool start, bool auto = false, bool restoreConnection = false, bool fullScan = false) {
            this.store = store; updater = new Updater(store);
            activeStrategy = store.Settings.Strategy;
            BuildInterface();
            if(fullScan)dashboard.ScanMode.SelectedIndex=1;
            tray = new NotifyIcon { Icon = Icon, Text = "Обход — выключен", Visible = true };
            var menu = new ContextMenuStrip(); menu.Items.Add("Открыть", null, (s,e) => Restore()); menu.Items.Add("Включить / выключить", null, async (s,e) => await Toggle()); menu.Items.Add("Выход", null, (s,e) => Exit()); tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Restore();
            runner.Log += Append; timer = new System.Windows.Forms.Timer { Interval = 800 }; timer.Tick += async (s,e) => {
                RefreshState();
                if(store.Settings.AutoAppUpdates && !shellChecking && !checkingUpdates && !busy && !starting && !scanning && !testing && !dnsBusy && !exiting) {
                    if(stagedShell!=null && !runner.Running && !runner.Conflict && !domainEditor.Dirty) await ApplyShell();

                }
            }; timer.Start();
            LoadSettings();
            Shown += async (s,e) => {Append("Обход "+ShellUpdates.Current+". Ядро "+Updater.Version(store.Current));if(auto)await AutoSelect(restoreConnection);else if(start)await ApplyStrategy();if(!IsDisposed && store.Settings.AutoAppUpdates)await CheckShell(true);if(!IsDisposed && store.Settings.CheckUpdates)await CheckUpdates(true);};
            FormClosing += (s,e) => { if (!exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }
        Label Label(string text, float size, Color color, int x, int y, int width, int height) {
            return new Label { Text = text, Font = new Font("Segoe UI", size), ForeColor = color, Location = new Point(x,y), Size = new Size(width,height) };
        }
        Button Button(string text, Color color, int x, int y, int width, int height) {
            var b = new FlatAction { Text = text, BackColor = color, ForeColor = color == accent ? background : Color.White, FlatStyle = FlatStyle.Flat, Location = new Point(x,y), Size = new Size(width,height), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0; return b;
        }
        Panel Page(string text) { return new Panel { Text = text, BackColor = background, ForeColor = Color.White, AutoScroll = false, Padding = new Padding(Px(24)) }; }
        TextBox Input(int x, int y, int width) { return new TextBox { Location = new Point(x,y), Size = new Size(width,32), BackColor = card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle }; }
        void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        void Error(Exception ex) { Append("Ошибка: " + ex.Message); MessageBox.Show(this, ex.Message, "Обход", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
            } finally { loading = false; RefreshState(); }
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
            Append("Настройки GameFilter сохранены.");
        }
        async Task Toggle() {
            if (busy || elevation || starting) return;
            if(scanning){stopAfterScan=true;if(scanCancellation!=null)scanCancellation.Cancel();return;}
            try {
                if(runner.Running){starting=true;InvalidateChecks("Обход выключен · запустите новую проверку");await StopChecks();runner.Stop();return;}
                await ApplyStrategy();
            }catch(Exception ex){Error(ex);}finally{starting=false;if(!IsDisposed)RefreshState();}
        }
        bool IsAdmin() {using(var identity=WindowsIdentity.GetCurrent())return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
        void Elevate(string arguments) {
            elevation=true;
            try{Process.Start(new ProcessStartInfo(Application.ExecutablePath,arguments){UseShellExecute=true,Verb="runas"});Exit();}
            finally{elevation=false;}
        }
        async Task ApplyStrategy() {
            if(busy || elevation || starting || scanning)return;
            starting=true;RefreshState();
            try {
                if (profiles.SelectedItem == null) throw new InvalidOperationException("Нет стратегий. Проверьте папку data/engines.");
                if(runner.Conflict)throw new InvalidOperationException("Сначала завершите внешний winws через его исходное приложение.");
                InvalidateChecks("Применяем стратегию…");await StopChecks();runner.Stop();
                store.Settings.Strategy = profiles.SelectedItem.ToString(); store.Save();
                if(!IsAdmin()){Elevate("--connect");return;}
                await StartEngine(store.Settings.Strategy,CancellationToken.None);
                starting=false;RefreshState();await RunChecks();
            }catch(Exception ex){if(!IsDisposed)Error(ex);}finally{starting=false;if(!IsDisposed)RefreshState();}
        }
        async Task StartEngine(string strategy,CancellationToken token) {
            token.ThrowIfCancellationRequested();await Task.Delay(150,token);
            activeStrategy=strategy;runner.Start(store.Current,strategy);RefreshState();
            await Task.Delay(650,token);
            if(!runner.Running)throw new IOException("Ядро завершилось после запуска. Подробности в журнале.");
        }
        void InvalidateChecks(string reason) {checkEpoch++;dashboard.ClearResults(reason);}
        async Task StopChecks() {
            if(checkCancellation!=null)checkCancellation.Cancel();
            if(manualCheckTask!=null){try{await manualCheckTask;}catch(OperationCanceledException){}}
        }
        async Task AutoSelect(bool restoreConnection) {
            if(scanning || busy || starting || elevation || dnsBusy)return;
            if(runner.Conflict){Error(new InvalidOperationException("Завершите внешний winws перед автоподбором."));return;}
            if(!IsAdmin()) {
                try{Elevate("--autoselect"+(runner.Running?" --restore-connect":"")+(dashboard.ScanMode.SelectedIndex==1?" --full-scan":""));}catch(Exception ex){Error(ex);}return;
            }
            bool wasRunning=runner.Running || restoreConnection;
            string original=runner.Running?activeStrategy:store.Settings.Strategy;
            bool quick=dashboard.ScanMode.SelectedIndex==0;
            Strategy.ValidateEngine(store.Current);
            scanning=true;stopAfterScan=false;scanCancellation=new CancellationTokenSource();var token=scanCancellation.Token;
            var names=store.Strategies();dashboard.ResetHistory(names);RefreshState();bool chosen=false;
            try {
                InvalidateChecks("Автоподбор начинается…");await StopChecks();runner.Stop();
                var result=await StrategySelector.Run(names,
                    async (name,cancel)=>{InvalidateChecks("Проверяется: "+name.Replace(".bat",""));await StartEngine(name,cancel);},
                    ()=>runner.Stop(),
                    async cancel=>await ServiceChecks.Batch(cancel,(index,item)=>{if(!IsDisposed)dashboard.SetResult(index,item);},quick?6:12),token,
                    (index,total,name)=>{if(IsDisposed)return;scanStep=index+" / "+total+" · "+Theme.StrategyName(name);dashboard.ScanSummary.Text=scanStep;dashboard.Progress(name);RefreshState();},
                    candidate=>{if(IsDisposed)return;dashboard.Record(candidate);checkSummary.Text=candidate.Strategy.Replace(".bat","")+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length;Append("Автоподбор: "+candidate.Strategy+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length+(candidate.Error.Length>0?" · "+candidate.Error:""));},quick,async cancel=>await ServiceChecks.Batch(cancel,(index,item)=>{if(!IsDisposed)dashboard.SetResult(index,item);}));
                if(result.Winner!=null) {
                    store.Settings.Strategy=result.Winner.Strategy;store.Save();activeStrategy=result.Winner.Strategy;chosen=true;
                    loading=true;categories.SelectedIndex=0;profiles.Items.Clear();profiles.Items.AddRange(names);profiles.SelectedItem=activeStrategy;loading=false;
                    for(int i=0;i<result.Winner.Results.Length;i++)dashboard.SetResult(i,result.Winner.Results[i]);
                    checkSummary.Text="Все сервисы доступны";
                    dashboard.ScanSummary.Text="Найден рабочий вариант: "+Theme.StrategyName(activeStrategy);
                }else {
                    bool dnsFailure=result.Candidates.Any(x=>x.Results.Any(y=>y.Failure=="dns"));
                    dashboard.ScanSummary.Text="Полного результата 5/5 не найдено."+(dnsFailure?"\nЕсть ошибки DNS — откройте диагностику.":"\nРезультаты всех стратегий — в таблице.");
                }
            }catch(OperationCanceledException){if(!IsDisposed)dashboard.ScanSummary.Text="Автоподбор отменён.";}
            catch(Exception ex){if(!IsDisposed){dashboard.ScanSummary.Text="Автоподбор завершился с ошибкой.";Error(ex);}}
            {
                if(!chosen) {
                    try {
                        runner.Stop();activeStrategy=original;store.Settings.Strategy=original;
                        if(!IsDisposed && !pendingExit && !stopAfterScan && wasRunning){await StartEngine(original,CancellationToken.None);InvalidateChecks("Возвращена "+original.Replace(".bat","")+" · проверка требуется заново");}
                        else if(!IsDisposed)InvalidateChecks("Обход выключен · выберите стратегию из таблицы");
                    }catch(Exception ex){if(!IsDisposed)Append("Восстановление стратегии: "+ex.Message);}
                }
                scanning=false;scanCancellation.Dispose();scanCancellation=null;
                if(!IsDisposed){RefreshState();if(pendingExit)Exit();}
            }
        }
        async Task CheckUpdates(bool quiet) {
            if (busy || scanning || checkingUpdates) return; checkingUpdates = true; RefreshState();
            try {
                updateText.Text = "Проверяем обновления…"; release = await updater.Check(); if(IsDisposed)return;
                if (release.Version == Updater.Version(store.Current)) { updateText.Text = "Обновлений нет"; release = null; }
                else { updateText.Text = "Доступна версия " + release.Version; Append("Доступно обновление ядра " + release.Version); }
            } catch (Exception ex) { if(IsDisposed)return; release = null; updateText.Text = "Не удалось проверить обновления. Попробуйте позже."; Append("Проверка обновлений: " + ex.Message); if (!quiet) Error(ex); }
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
                if(!IsDisposed && epoch==checkEpoch){checkSummary.Text="Доступно: "+results.Count(x=>x.State==1)+" из 5";if(results.Any(x=>x.Failure=="dns"))dashboard.ScanSummary.Text="Есть ошибка DNS.\nНажмите «Диагностика DNS».";}
            }catch(OperationCanceledException){}catch(Exception ex){if(!IsDisposed)Error(ex);}
            finally{testing=false;localCancellation.Dispose();if(checkCancellation==localCancellation)checkCancellation=null;if(!IsDisposed)RefreshState();}
        }
        async Task DiagnoseDns() {
            if(dnsBusy || scanning || busy)return;dnsBusy=true;RefreshState();
            try {
                var report=await DnsDiagnostics.Run(CancellationToken.None);if(IsDisposed)return;Append(report);
                using(var dialog=new Form{Text="Диагностика DNS",Size=new Size(720,570),StartPosition=FormStartPosition.CenterParent,BackColor=background,ForeColor=Color.White}) {
                    var text=new TextBox{Text=report,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=background,ForeColor=muted,Font=new Font("Segoe UI",10),BorderStyle=BorderStyle.None};
                    var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60,Padding=new Padding(10)};
                    var networks=Button("Подключения Windows",card,0,0,240,38);networks.Click+=(s,e)=>Guard(()=>Open("ncpa.cpl"));
                    var docs=Button("Инструкция по DNS ↗",card,0,0,230,38);docs.Click+=(s,e)=>Guard(()=>Open("https://developers.google.com/speed/public-dns/docs/using#windows"));
                    actions.Controls.AddRange(new Control[]{networks,docs});dialog.Padding=new Padding(16);dialog.Controls.Add(text);dialog.Controls.Add(actions);dialog.ShowDialog(this);
                }
            }catch(Exception ex){if(!IsDisposed)Error(ex);}finally{dnsBusy=false;if(!IsDisposed)RefreshState();}
        }
        async Task Install() {
            if (busy || scanning || starting || domainEditor.Dirty || release == null) return;
            if (runner.Running || runner.Conflict) { Error(new InvalidOperationException("Сначала остановите все процессы winws.")); return; }
            busy = true; RefreshState();
            try { await StopChecks(); InvalidateChecks("Ядро обновлено · запустите проверку заново"); await updater.Install(release, message => {updateText.Text = message; Append(message);}); release = null; LoadSettings(); }
            catch (Exception ex) { updateText.Text = "Обновление не установлено. Текущее ядро сохранено."; Error(ex); }
            finally { busy = false; RefreshState(); }
        }
        void RefreshState() {
            if (IsDisposed) return;
            bool on = runner.Running, external = !on && runner.Conflict;
            if(previouslyOn && !on && !scanning && !starting && !exiting) { InvalidateChecks("Ядро остановлено · проверка требуется заново"); if(checkCancellation!=null)checkCancellation.Cancel(); }
            previouslyOn=on;
            state.Text = scanning ? "Подбираем стратегию" : on ? "Обход включён" : (external ? "Другой обход запущен" : "Обход выключен"); state.ForeColor=on?Theme.Success:external?Theme.Warning:Theme.Text;
            subtitle.Text = scanning ? (scanStep.Length>0?scanStep:"Ищем рабочий вариант…") : on ? Theme.StrategyName(activeStrategy) : (external ? "Закройте другой обход, чтобы управлять подключением здесь" : "Выберите вариант или нажмите «Подобрать автоматически»");
            connect.Text = scanning ? "Остановить подбор" : starting ? "Запуск…" : (on ? "Выключить" : (external ? "Внешний winws" : "Включить")); connect.Enabled = !busy && !external && !starting;
            profiles.Enabled = categories.Enabled = !busy && !starting && !scanning;
            games.Enabled = tcp.Enabled = udp.Enabled = saveGameButton.Enabled = !on && !external && !busy && !starting && !scanning; domainEditor.SetAllowed(!external && !busy && !starting && !scanning,on);
            dashboard.ScanMode.Enabled = !busy && !starting && !scanning;
            dashboard.Apply.Enabled = dashboard.Scan.Enabled = !busy && !external && !starting && !scanning && !testing && !dnsBusy;
            dashboard.Cancel.Enabled = scanning && scanCancellation!=null && !scanCancellation.IsCancellationRequested;
            dashboard.Dns.Enabled = !busy && !starting && !scanning && !dnsBusy;
            check.Enabled = !busy && !scanning && !checkingUpdates; install.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && !domainEditor.Dirty && release != null;
            rollback.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && !domainEditor.Dirty && !String.IsNullOrEmpty(store.Settings.PreviousEngine);
            autoCheck.Enabled = !busy && !checkingUpdates; runChecks.Enabled = !busy && !starting && !scanning && !testing;
            shellInstall.Enabled = stagedShell!=null && !busy && !starting && !scanning && !testing && !shellChecking && !domainEditor.Dirty;
            tray.Text = "Обход — " + (on ? "включён" : "выключен");
        }
        async Task CheckShell(bool quiet) {
            if(shellChecking || stagedShell!=null || busy || scanning || starting)return;
            shellChecking=true;RefreshState();
            try {
                var available=await shellUpdates.Check();if(IsDisposed)return;
                if(available==null){shellText.Text="Версия "+ShellUpdates.Current+" · обновлений нет";return;}
                shellText.Text="Скачиваем обновление "+available.Version+"…";
                stagedShell=await shellUpdates.Stage(available,store.Root);if(IsDisposed)return;
                shellText.Text="Обновление "+available.Version+" готово к установке";Append(shellText.Text);
            }catch(Exception ex){if(!IsDisposed){shellText.Text="Не удалось проверить обновления. Попробуйте позже.";Append("Обновления приложения: "+ex.Message);if(!quiet)Error(ex);}}
            finally{shellChecking=false;if(!IsDisposed)RefreshState();}
        }
        async Task ApplyShell() {
            if(stagedShell==null || busy || starting || scanning || testing || checkingUpdates || shellChecking || domainEditor.Dirty)return;
            bool reconnect=runner.Running;busy=true;RefreshState();
            try {
                await StopChecks();
                ShellUpdates.Launch(stagedShell,Application.ExecutablePath,reconnect);
                runner.Stop();exiting=true;Close();
            }catch(Exception ex){busy=false;if(!IsDisposed){RefreshState();Error(ex);}}
        }
        void Open(string target) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AppActivation.Allow(Handle); }
        protected override void WndProc(ref Message m) {
            if ((uint)m.Msg == AppActivation.Message) { Restore(); return; }
            if(m.Msg==0x83 && m.WParam!=IntPtr.Zero){m.Result=IntPtr.Zero;return;}
            base.WndProc(ref m);
            if(m.Msg==0x84 && WindowState==FormWindowState.Normal){long point=m.LParam.ToInt64();var p=PointToClient(new Point((short)(point&0xffff),(short)((point>>16)&0xffff)));int edge=8;bool left=p.X<edge,right=p.X>=ClientSize.Width-edge,top=p.Y<edge,bottom=p.Y>=ClientSize.Height-edge;int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;if(hit!=0)m.Result=new IntPtr(hit);}
        }
        protected override CreateParams CreateParams {get{var cp=base.CreateParams;cp.Style|=0x40000|0x10000|0x20000;return cp;}}
        protected override void OnLoad(EventArgs e) {
            base.OnLoad(e);
            var area=Screen.FromControl(this).WorkingArea;
            MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-32),Math.Min(MinimumSize.Height,area.Height-32));
            Size=new Size(Math.Min(Width,area.Width-32),Math.Min(Height,area.Height-32));
            if(StartPosition==FormStartPosition.CenterScreen)Location=new Point(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);
        }
        void Exit() {
            if(scanning){pendingExit=true;stopAfterScan=true;if(scanCancellation!=null)scanCancellation.Cancel();return;}
            if (busy) { Restore(); Error(new InvalidOperationException("Дождитесь завершения текущей операции.")); return; }
            try { runner.Stop(); exiting = true; Close(); } catch (Exception ex) { Error(ex); }
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (checkCancellation != null) checkCancellation.Cancel();
                if (scanCancellation != null) scanCancellation.Cancel();
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (tray != null) tray.Dispose();
                runner.Dispose(); updater.Dispose(); shellUpdates.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
