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
                try { Application.Run(new MainForm(new Store(AppDomain.CurrentDomain.BaseDirectory), args.Contains("--connect"), args.Contains("--autoselect"), args.Contains("--restore-connect"))); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить Zapret Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed class MainForm : Form {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        readonly Store store; readonly EngineRunner runner = new EngineRunner(); readonly Updater updater;
        readonly Color background = Color.FromArgb(16, 20, 30), card = Color.FromArgb(25, 31, 45), muted = Color.FromArgb(160, 174, 195), accent = Color.FromArgb(116, 220, 183);
        Label state, subtitle, version, updateText; Button connect, install, rollback, check;
        ComboBox profiles, categories, games; CheckBox autoCheck; TextBox tcp, udp, log;
        Label[] checkStatuses, checkTimes; Label checkSummary; Button runChecks;
        DarkTabs tabs; bool starting;
        Dashboard dashboard; bool scanning, dnsBusy, pendingExit, stopAfterScan;
        string activeStrategy; int checkEpoch; Task manualCheckTask;
        CancellationTokenSource scanCancellation;
        readonly ShellUpdates shellUpdates = new ShellUpdates();
        Label shellText; Button shellInstall; CheckBox autoShell; string stagedShell; bool shellChecking;
        DateTime nextShellCheck = DateTime.MinValue; bool previouslyOn;
        CancellationTokenSource checkCancellation; bool testing, checkingUpdates;
        NotifyIcon tray; System.Windows.Forms.Timer timer;
        Release release; bool busy, exiting, loading, elevation;
        public MainForm(Store store, bool start, bool auto = false, bool restoreConnection = false) {
            this.store = store; updater = new Updater(store);
            activeStrategy = store.Settings.Strategy;
            Text = "Обход"; ClientSize = new Size(920, 800); MinimumSize = new Size(900, 640);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = background; ForeColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi; Icon = AppIcon.Load();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); Controls.Add(root);
            var heading = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(heading, 0, 0);
            var minimize = Button("−", card, 732, 0, 40, 32); var close = Button("×", card, 780, 0, 40, 32);
            minimize.Click += (s,e) => Hide(); close.Click += (s,e) => Hide();
            heading.Controls.AddRange(new Control[] {minimize,close}); minimize.BringToFront(); close.BringToFront();
            heading.Resize += (s,e) => { close.Left = heading.Width - 40; minimize.Left = heading.Width - 88; };
            MouseEventHandler drag = (s,e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            heading.MouseDown += drag; foreach (Control child in heading.Controls) if (child is Label) child.MouseDown += drag;
            var status = new Panel { Dock = DockStyle.Fill, BackColor = card, Margin = new Padding(0, 0, 0, 18) };
            state = Label("Обход выключен", 23, Color.White, 22, 18, 450, 46);
            subtitle = Label("Выберите стратегию и нажмите «Включить»", 10, muted, 24, 76, 480, 46);
            connect = Button("Включить", accent, 608, 38, 180, 52);
            connect.Click += async (s,e) => await Toggle(); status.Controls.AddRange(new Control[] {state, subtitle, connect}); root.Controls.Add(status, 0, 1);
            tabs = new DarkTabs { Dock = DockStyle.Fill };
            var settings = Page("Подключение"); var filters = Page("Настройки"); var updates = Page("Обновления"); var logs = Page("Журнал"); var help = Page("Помощь");
            tabs.AddPages(new[] {settings,filters,updates,logs,help}); root.Controls.Add(tabs,0,2);
            settings.AutoScrollMinSize = new Size(825,510);
            dashboard = new Dashboard(); settings.Controls.Add(dashboard);
            profiles = dashboard.Profiles; categories = dashboard.Categories; checkSummary = dashboard.Summary;
            checkStatuses = dashboard.Statuses; checkTimes = dashboard.Times; runChecks = dashboard.Check;
            categories.SelectedIndexChanged += (s,e) => {if(!loading) FilterProfiles();};
            profiles.SelectedIndexChanged += (s,e) => {
                if(!loading && profiles.SelectedItem!=null && !scanning && !runner.Running) Guard(()=>{store.Settings.Strategy=profiles.SelectedItem.ToString();store.Save();});
            };
            dashboard.Apply.Click += async (s,e) => await ApplyStrategy();
            dashboard.Scan.Click += async (s,e) => await AutoSelect(false);
            dashboard.Cancel.Click += (s,e) => {if(scanCancellation!=null)scanCancellation.Cancel();};
            dashboard.Check.Click += async (s,e) => await RunChecks();
            dashboard.Dns.Click += async (s,e) => await DiagnoseDns();
            dashboard.Pick += name => {if(scanning)return;categories.SelectedIndex=0;profiles.SelectedItem=name;};
            filters.Controls.Add(Label("GameFilter",17,Color.White,20,20,680,35));
            games = Combo(20,83,290); games.Items.AddRange(new object[] {"Выключен","TCP и UDP","Только TCP","Только UDP"}); filters.Controls.Add(games);
            filters.Controls.Add(Label("TCP-порты",9,muted,330,58,175,22)); filters.Controls.Add(Label("UDP-порты",9,muted,522,58,175,22));
            tcp=Input(330,83,176);udp=Input(522,83,178);filters.Controls.AddRange(new Control[]{tcp,udp});
            var saveGame=Button("Сохранить GameFilter",card,20,132,260,38);saveGame.Click+=(s,e)=>Guard(SaveGame);filters.Controls.Add(saveGame);
            filters.Controls.Add(Label("Изменять фильтры можно при выключенном обходе.\nПорты: 1024-65535 или, например, 1024-1934,1936-65535.",9,muted,20,190,680,55));
            var lists=Button("Мои списки доменов",card,20,273,260,38);lists.Click+=(s,e)=>Guard(()=>Open(Path.Combine(store.Current,"lists")));filters.Controls.Add(lists);
            version = Label("", 17, Color.White, 20, 22, 680, 38); updates.Controls.Add(version);
            updateText = Label("Обновления из Flowseal/zapret-discord-youtube", 10, muted, 20, 70, 680, 74); updates.Controls.Add(updateText);
            check = Button("Проверить", card, 20, 157, 190, 42); check.Click += async (s,e) => await CheckUpdates(false);
            install = Button("Установить обновление", accent, 226, 157, 270, 42); install.Enabled = false; install.Click += async (s,e) => await Install();
            rollback = Button("Вернуть прошлую версию", card, 20, 216, 285, 42);
            rollback.Click += (s,e) => Guard(() => { if (runner.Running || runner.Conflict || scanning || starting || testing) throw new InvalidOperationException("Сначала остановите все процессы winws."); updater.Rollback(); InvalidateChecks("Ядро изменено · запустите новую проверку"); release = null; LoadSettings(); Append("Выполнен откат ядра."); });
            updates.Controls.AddRange(new Control[] { check, install, rollback });
            autoCheck = new CheckBox { Text = "Проверять при открытии приложения", Location = new Point(20, 283), Size = new Size(650, 30), ForeColor = Color.White };
            autoCheck.CheckedChanged += (s,e) => { if (!loading) Guard(() => {store.Settings.CheckUpdates = autoCheck.Checked; store.Save();}); }; updates.Controls.Add(autoCheck);
            shellText = Label("Оболочка " + ShellUpdates.Current + " · xxXdemidXxx/discord_obhod_app", 10, muted, 20, 330, 750, 58); updates.Controls.Add(shellText);
            shellInstall = Button("Обновить оболочку", accent, 20, 390, 260, 40); shellInstall.Click += async (s,e) => await ApplyShell(); updates.Controls.Add(shellInstall);
            var shellCheck = Button("Проверить оболочку", card, 296, 390, 250, 40); shellCheck.Click += async (s,e) => await CheckShell(false); updates.Controls.Add(shellCheck);
            autoShell = new CheckBox { Text = "Автоматически получать обновления оболочки", Location = new Point(20, 443), Size = new Size(750, 30), ForeColor = Color.White };
            autoShell.CheckedChanged += (s,e) => { if(!loading) Guard(()=>{store.Settings.AutoAppUpdates=autoShell.Checked;store.Save();}); }; updates.Controls.Add(autoShell);
            updates.AutoScrollMinSize = new Size(800, 540);
            updates.Controls.Add(Label("Проверка при запуске и каждые 15 минут. SHA-256 проверяется до установки.\nПри включённом обходе обновление ждёт его остановки; настройки сохраняются.",9,muted,20,484,750,48));
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = background, ForeColor = muted, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9) }; logs.Padding = new Padding(16); logs.Controls.Add(log);
            help.Controls.Add(Label("Как пользоваться", 17, Color.White, 20, 20, 680, 38));
            help.Controls.Add(Label("Выберите стратегию → «Применить и проверить».\nРезультаты находятся рядом; вручную выключать обход не нужно.\n«Автоподбор» проверяет все стратегии и повторно проверяет лучшую.\nЕго можно отменить — предыдущая стратегия вернётся.\n\nЕсли появилась ошибка DNS, нажмите «Диагностика DNS».\nВстроенные проверки используют DNS Windows, браузер может\nиспользовать свой Secure DNS.\n\nУспех HTTPS-проверок не подтверждает видео и голосовой UDP.\nПроверьте ролик YouTube и голосовой канал Discord отдельно.\n\nКрестик → трей. Для завершения выберите «Выход» у значка.",10,muted,20,76,730,285));
            var docs = Button("Инструкция Flowseal ↗", card, 20, 373, 280, 38); docs.Click += (s,e) => Guard(() => Open("https://github.com/Flowseal/zapret-discord-youtube#readme")); help.Controls.Add(docs);
            var shortcut = Button("Ярлык на рабочем столе", card, 316, 373, 285, 38);
            shortcut.Click += (s,e) => Guard(() => {AppShortcut.Create(); shortcut.Text = "Ярлык «Обход» создан"; Append("Создан ярлык «Обход» на рабочем столе.");}); help.Controls.Add(shortcut);
            var footer = Label("Flowseal  •  оболочка "+ShellUpdates.Current+"                         HTTPS-проверки · видео и голос проверьте в приложениях",9,muted,0,0,850,30);footer.Dock=DockStyle.Fill;root.Controls.Add(footer,0,3);
            tray = new NotifyIcon { Icon = Icon, Text = "Обход — выключен", Visible = true };
            var menu = new ContextMenuStrip(); menu.Items.Add("Открыть", null, (s,e) => Restore()); menu.Items.Add("Включить / выключить", null, async (s,e) => await Toggle()); menu.Items.Add("Выход", null, (s,e) => Exit()); tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Restore();
            runner.Log += Append; timer = new System.Windows.Forms.Timer { Interval = 800 }; timer.Tick += async (s,e) => {
                RefreshState();
                if(store.Settings.AutoAppUpdates && !shellChecking && !checkingUpdates && !busy && !starting && !scanning && !testing && !dnsBusy && !exiting) {
                    if(stagedShell!=null && !runner.Running && !runner.Conflict) await ApplyShell();
                    else if(DateTime.UtcNow>=nextShellCheck) await CheckShell(true);
                }
            }; timer.Start();
            LoadSettings();
            Shown += async (s,e) => {Append("Обход "+ShellUpdates.Current+". Ядро "+Updater.Version(store.Current));if(auto)await AutoSelect(restoreConnection);else if(start)await ApplyStrategy();if(!IsDisposed && store.Settings.CheckUpdates)await CheckUpdates(true);};
            FormClosing += (s,e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        }
        Label Label(string text, float size, Color color, int x, int y, int width, int height) {
            return new Label { Text = text, Font = new Font("Segoe UI", size), ForeColor = color, Location = new Point(x,y), Size = new Size(width,height) };
        }
        Button Button(string text, Color color, int x, int y, int width, int height) {
            var b = new FlatAction { Text = text, BackColor = color, ForeColor = color == accent ? background : Color.White, FlatStyle = FlatStyle.Flat, Location = new Point(x,y), Size = new Size(width,height), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0; return b;
        }
        Panel Page(string text) { return new Panel { Text = text, BackColor = background, ForeColor = Color.White, AutoScroll = true, AutoScrollMinSize = new Size(780, 438) }; }
        ComboBox Combo(int x, int y, int width) {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x,y), Size = new Size(width,32), BackColor = card, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 25 };
            box.DrawItem += (s,e) => {
                using (var brush = new SolidBrush(card)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, box.Items[e.Index].ToString().Replace(".bat", ""), Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };
            return box;
        }
        TextBox Input(int x, int y, int width) { return new TextBox { Location = new Point(x,y), Size = new Size(width,32), BackColor = card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle }; }
        void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        void Error(Exception ex) { Append("Ошибка: " + ex.Message); MessageBox.Show(this, ex.Message, "Zapret Desktop", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
                autoShell.Checked = store.Settings.AutoAppUpdates;
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
            if (runner.Running || busy || scanning || starting) throw new InvalidOperationException("Сначала выключите обход.");
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
            token.ThrowIfCancellationRequested();await Task.Delay(250,token);
            activeStrategy=strategy;runner.Start(store.Current,strategy);RefreshState();
            await Task.Delay(1400,token);
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
                try{Elevate("--autoselect"+(runner.Running?" --restore-connect":""));}catch(Exception ex){Error(ex);}return;
            }
            bool wasRunning=runner.Running || restoreConnection;
            string original=runner.Running?activeStrategy:store.Settings.Strategy;
            scanning=true;stopAfterScan=false;scanCancellation=new CancellationTokenSource();var token=scanCancellation.Token;
            var names=store.Strategies();dashboard.ResetHistory(names);RefreshState();bool chosen=false;
            try {
                InvalidateChecks("Автоподбор начинается…");await StopChecks();runner.Stop();
                var result=await StrategySelector.Run(names,
                    async (name,cancel)=>{InvalidateChecks("Проверяется: "+name.Replace(".bat",""));await StartEngine(name,cancel);},
                    ()=>runner.Stop(),
                    async cancel=>await ServiceChecks.Batch(cancel,(index,item)=>{if(!IsDisposed)dashboard.SetResult(index,item);}),token,
                    (index,total,name)=>{if(IsDisposed)return;dashboard.ScanSummary.Text="Стратегия "+index+" из "+total+"\n"+name.Replace(".bat","");dashboard.Progress(name);RefreshState();},
                    candidate=>{if(IsDisposed)return;dashboard.Record(candidate);checkSummary.Text=candidate.Strategy.Replace(".bat","")+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length;Append("Автоподбор: "+candidate.Strategy+" · "+candidate.Passed+"/"+ServiceChecks.Targets.Length+(candidate.Error.Length>0?" · "+candidate.Error:""));});
                if(result.Winner!=null) {
                    store.Settings.Strategy=result.Winner.Strategy;store.Save();activeStrategy=result.Winner.Strategy;chosen=true;
                    loading=true;categories.SelectedIndex=0;profiles.Items.Clear();profiles.Items.AddRange(names);profiles.SelectedItem=activeStrategy;loading=false;
                    for(int i=0;i<result.Winner.Results.Length;i++)dashboard.SetResult(i,result.Winner.Results[i]);
                    checkSummary.Text="Успешных ответов: 5 из 5 · "+activeStrategy.Replace(".bat","")+"\nПовторная проверка пройдена · "+DateTime.Now.ToString("HH:mm:ss");
                    dashboard.ScanSummary.Text="Выбрана "+activeStrategy.Replace(".bat","")+".\nОбход оставлен включённым. Проверьте видео и голос.";
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
                updateText.Text = "Проверка официального GitHub…"; release = await updater.Check(); if(IsDisposed)return;
                if (release.Version == Updater.Version(store.Current)) { updateText.Text = "Установлен последний релиз: " + release.Version; release = null; }
                else { updateText.Text = "Доступен официальный релиз " + release.Version + ".\nВыключите обход и нажмите «Установить обновление»."; Append("Доступно обновление ядра " + release.Version); }
            } catch (Exception ex) { if(IsDisposed)return; release = null; updateText.Text = "Не удалось проверить GitHub. Повторите позже.\n" + ex.Message; Append("Проверка обновлений: " + ex.Message); if (!quiet) Error(ex); }
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
                if(!IsDisposed && epoch==checkEpoch){checkSummary.Text="Успешных ответов: "+results.Count(x=>x.State==1)+" из 5 · "+engineState+"\n"+DateTime.Now.ToString("HH:mm:ss");if(results.Any(x=>x.Failure=="dns"))dashboard.ScanSummary.Text="Есть ошибка DNS.\nНажмите «Диагностика DNS».";}
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
            if (busy || scanning || starting || release == null) return;
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
            state.Text = scanning ? "Автоподбор стратегий" : on ? "Обход включён" : (external ? "Другой обход запущен" : "Обход выключен"); state.ForeColor = on || scanning ? accent : (external ? Color.FromArgb(239, 198, 114) : Color.White);
            subtitle.Text = scanning ? "Проверяем все варианты. Результаты — в таблице ниже." : on ? "Работает ядро · " + activeStrategy.Replace(".bat", "") : (external ? "Остановите внешний winws через его service.bat.\nПроверку сервисов можно запустить прямо сейчас." : "Выберите стратегию или запустите автоподбор");
            connect.Text = scanning ? "Остановить подбор" : starting ? "Запуск…" : (on ? "Выключить" : (external ? "Внешний winws" : "Включить")); connect.Enabled = !busy && !external && !starting;
            profiles.Enabled = categories.Enabled = !busy && !starting && !scanning;
            games.Enabled = tcp.Enabled = udp.Enabled = !on && !busy && !starting && !scanning;
            dashboard.Apply.Enabled = dashboard.Scan.Enabled = !busy && !external && !starting && !scanning && !testing && !dnsBusy;
            dashboard.Cancel.Enabled = scanning && scanCancellation!=null && !scanCancellation.IsCancellationRequested;
            dashboard.Dns.Enabled = !busy && !starting && !scanning && !dnsBusy;
            check.Enabled = !busy && !scanning && !checkingUpdates; install.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && release != null;
            rollback.Enabled = !on && !external && !busy && !scanning && !starting && !checkingUpdates && !testing && !String.IsNullOrEmpty(store.Settings.PreviousEngine);
            autoCheck.Enabled = !busy && !checkingUpdates; runChecks.Enabled = !busy && !starting && !scanning && !testing;
            shellInstall.Enabled = stagedShell!=null && !busy && !starting && !scanning && !testing && !shellChecking;
            tray.Text = "Обход — " + (on ? "включён" : "выключен");
        }
        async Task CheckShell(bool quiet) {
            if(shellChecking || stagedShell!=null || busy || scanning || starting)return;
            shellChecking=true;nextShellCheck=DateTime.UtcNow.AddMinutes(15);RefreshState();
            try {
                var available=await shellUpdates.Check();if(IsDisposed)return;
                if(available==null){shellText.Text="Оболочка "+ShellUpdates.Current+" · установлена последняя версия";return;}
                shellText.Text="Оболочка "+available.Version+" доступна · загрузка…";
                stagedShell=await shellUpdates.Stage(available,store.Root);if(IsDisposed)return;
                shellText.Text="Оболочка "+available.Version+" скачана и проверена.\nУстановится после остановки обхода или по кнопке.";Append(shellText.Text);
            }catch(Exception ex){if(!IsDisposed){shellText.Text="Обновления оболочки: "+ex.Message;Append(shellText.Text);if(!quiet)Error(ex);}}
            finally{shellChecking=false;if(!IsDisposed)RefreshState();}
        }
        async Task ApplyShell() {
            if(stagedShell==null || busy || starting || scanning || testing || checkingUpdates || shellChecking)return;
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
            base.WndProc(ref m);
        }
        protected override void OnLoad(EventArgs e) {
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width - 40), Math.Min(Height, area.Height - 40));
            if (StartPosition == FormStartPosition.CenterScreen) Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
            base.OnLoad(e);
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

