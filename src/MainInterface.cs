using System;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class ToggleOption : CheckBox {
        public ToggleOption(){DoubleBuffered=true;AutoSize=false;Height=34;ForeColor=Theme.Text;BackColor=Theme.Surface;Cursor=Cursors.Hand;Font=new Font("Segoe UI",10);}
        protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;int h=(int)(22*Theme.SystemScale),w=(int)(42*Theme.SystemScale);var r=new Rectangle(0,(Height-h)/2,w,h);using(var p=Theme.Round(r,h/2))using(var b=new SolidBrush(Enabled && Checked?Theme.Accent:Theme.Border))e.Graphics.FillPath(b,p);using(var b=new SolidBrush(Theme.Text))e.Graphics.FillEllipse(b,Checked?r.Right-h+3:r.X+3,r.Y+3,h-6,h-6);TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle((int)(54*Theme.SystemScale),0,Width-(int)(54*Theme.SystemScale),Height),Enabled?ForeColor:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);}
    }
    public sealed partial class MainForm {
        float windowScale=Theme.SystemScale; int Px(int value){return (int)Math.Round(value*windowScale);}
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified){windowScale*=factor.Width;base.ScaleControl(factor,specified);}
        DomainEditor domainEditor;Button saveGameButton;Label gameSaved;bool expandedPorts;
        ToggleOption telegram,discord; Button externalBypass; Label telegramStatus,externalStatus;
        void BuildInterface() {
            ownerMark=Theme.Label("Проверяем подпись сборки…",8);ownerMark.Dock=DockStyle.Bottom;ownerMark.Height=Px(24);ownerMark.TextAlign=ContentAlignment.MiddleLeft;ownerMark.AutoEllipsis=true;
            Text="ByPass";ClientSize=new Size(Px(1100),Px(830));MinimumSize=new Size(Px(940),Px(690));FormBorderStyle=FormBorderStyle.None;ControlBox=false;ShowIcon=false;
            // Leave the form itself exposed at every edge so child panels do
            // not intercept the native resize hit test.
            Padding=new Padding(Px(6));
            StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=background;ForeColor=Theme.Text;AutoScaleMode=AutoScaleMode.None;Icon=AppIcon.Load();
            var titlebar=new Panel{Dock=DockStyle.Top,Height=Px(36),BackColor=Theme.Input};var title=Theme.Label("ByPass",9,true);title.SetBounds(Px(18),Px(0),Px(320),Px(36));title.ForeColor=Theme.Muted;title.TextAlign=ContentAlignment.MiddleLeft;titlebar.Controls.Add(title);
            Button minimize=Theme.Button("−"),maximize=Theme.Button("□"),close=Theme.Button("×");minimize.Name="MinimizeWindow";maximize.Name="MaximizeWindow";close.Name="HideWindow";minimize.AccessibleName="Свернуть";maximize.AccessibleName="Развернуть или восстановить";close.AccessibleName="Скрыть в значок у часов";foreach(var b in new[]{minimize,maximize,close}){b.BackColor=Theme.Input;b.Size=new Size(Px(44),Px(30));titlebar.Controls.Add(b);}titlebar.Resize+=(s,e)=>{close.SetBounds(titlebar.Width-Px(48),Px(3),Px(44),Px(30));maximize.SetBounds(titlebar.Width-Px(94),Px(3),Px(44),Px(30));minimize.SetBounds(titlebar.Width-Px(140),Px(3),Px(44),Px(30));};minimize.Click+=(s,e)=>WindowState=FormWindowState.Minimized;maximize.Click+=(s,e)=>WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;close.Click+=(s,e)=>Hide();
            MouseEventHandler drag=(s,e)=>{if(e.Button==MouseButtons.Left){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}};titlebar.MouseDown+=drag;title.MouseDown+=drag;titlebar.DoubleClick+=(s,e)=>maximize.PerformClick();title.DoubleClick+=(s,e)=>maximize.PerformClick();
            tabs=new DarkTabs{Dock=DockStyle.Fill};Panel connection=Page("Подключение"),domains=Page("Дополнительно"),filters=Page("Настройки"),updates=Page("Обновления"),logs=Page("Журнал"),help=Page("Помощь");help.Controls.Add(ownerMark);tabs.AddPages(new[]{connection,filters,updates,logs,domains,help});Controls.Add(tabs);updateNotice=new UpdateNotice();updateNotice.ShellRequested+=async()=>await AppUpdateAction();updateNotice.CoreRequested+=async()=>await Install();Controls.Add(updateNotice);problemNotice=new ProblemNotice();problemNotice.Details.Click+=(s,e)=>tabs.SelectedIndex=3;Controls.Add(problemNotice);Controls.Add(titlebar);
            var connectionLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,Padding=Padding.Empty};connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));connectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,Px(152)));connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));connection.Controls.Add(connectionLayout);connectionLayout.Resize+=(s,e)=>connectionLayout.RowStyles[0].Height=Px(connectionLayout.Height<Px(650)?124:152);
            var status=new SurfacePanel{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Px(16))};state=Theme.Label("Сервисы выключены",15,true);subtitle=Theme.Label("Включите нужный тумблер",9);subtitle.ForeColor=Theme.Muted;status.Controls.AddRange(new Control[]{state,subtitle});
            discord=new ToggleOption{Name="DiscordMessages",Text="Discord и YouTube"};telegram=new ToggleOption{Name="TelegramMessages",Text="Telegram (experimental)"};telegramStatus=Theme.Label("",9);telegramStatus.ForeColor=Theme.Muted;
            status.Controls.AddRange(new Control[]{discord,telegram,telegramStatus});status.Resize+=(s,e)=>{int p=status.Padding.Left;bool compact=status.Height<Px(120);int y=Px(compact?68:80),textWidth=status.Width-p*2;state.SetBounds(p,Px(12),Math.Max(Px(100),textWidth),Px(28));subtitle.SetBounds(p,Px(43),Math.Max(Px(100),textWidth),Px(26));discord.SetBounds(p,y,Px(242),Px(30));telegram.SetBounds(p+Px(254),y,Px(242),Px(30));telegramStatus.SetBounds(p+Px(520),y-Px(1),Math.Max(0,status.Width-p*2-Px(520)),Px(compact?34:44));};telegram.CheckedChanged+=async(s,e)=>await SetTelegram();discord.CheckedChanged+=async(s,e)=>await SetDiscord();
            dashboard=new Dashboard{Dock=DockStyle.Fill,Margin=Padding.Empty};connectionLayout.Controls.Add(status,0,0);connectionLayout.Controls.Add(dashboard,0,1);profiles=dashboard.Profiles;categories=dashboard.Categories;checkSummary=dashboard.Summary;runChecks=dashboard.Check;checkStatuses=dashboard.Statuses;checkTimes=dashboard.Times;
            categories.SelectedIndexChanged+=(s,e)=>{if(!loading)FilterProfiles();};profiles.SelectedIndexChanged+=(s,e)=>{if(!loading && profiles.SelectedItem!=null && !scanning && !runner.Running)Guard(()=>{store.Settings.Strategy=profiles.SelectedItem.ToString();store.Save();});};dashboard.Apply.Click+=async(s,e)=>await ApplyStrategy();dashboard.Scan.Click+=async(s,e)=>{strategyFeedback.ResetRejected();await AutoSelect(false);};dashboard.Works.Click+=(s,e)=>Guard(ConfirmStrategy);dashboard.Next.Click+=async(s,e)=>{try{await NextStrategy();}catch(Exception ex){Error(ex);}};dashboard.Cancel.Click+=(s,e)=>{if(scanCancellation!=null)scanCancellation.Cancel();};dashboard.Check.Click+=async(s,e)=>await RunChecks();dashboard.Dns.Click+=async(s,e)=>await DiagnoseDns();dashboard.Pick+=name=>{if(scanning)return;categories.SelectedIndex=0;profiles.SelectedItem=name;};
            BuildAdvanced(domains);
            BuildFilters(filters);BuildUpdates(updates);BuildHelp(help);
            var logEditor=new DarkEditor{Dock=DockStyle.Fill};log=logEditor.Editor;log.ReadOnly=true;logs.Controls.Add(logEditor);
            var logTools=new Panel{Dock=DockStyle.Top,Height=Px(52)};var exportLog=Theme.Button("Сохранить лог",true);var logSaved=Theme.Label("Один TXT-файл для отправки разработчику",9);logSaved.ForeColor=Theme.Muted;
            logTools.Controls.AddRange(new Control[]{exportLog,logSaved});logs.Controls.Add(logTools);logTools.Resize+=(s,e)=>{exportLog.SetBounds(0,0,Px(190),Px(40));logSaved.SetBounds(Px(206),0,Math.Max(0,logTools.Width-Px(206)),Px(40));logSaved.TextAlign=ContentAlignment.MiddleLeft;};
            exportLog.Click+=(s,e)=>Guard(()=>{using(var dialog=new SaveFileDialog{Title="Сохранить журнал ByPass",Filter="Текстовый журнал (*.txt)|*.txt",FileName="ByPass-log-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt",DefaultExt="txt",AddExtension=true}){if(dialog.ShowDialog(this)!=DialogResult.OK)return;DiagnosticLog.Save(store,runner.Running,activeStrategy,log.Text,dialog.FileName);logSaved.Text="Лог сохранён. Этот файл можно отправить разработчику.";}});

        }
        Panel gamePage,sitePage; Button sitesSection,gamesSection; bool advancedGames;
        void BuildAdvanced(Panel page){
            var navigation=new Panel{Dock=DockStyle.Top,Height=Px(58)};
            var body=new Panel{Dock=DockStyle.Fill};sitesSection=Theme.Button("Правила для сайтов",true);sitesSection.Name="AdvancedSites";gamesSection=Theme.Button("Игровые соединения");gamesSection.Name="AdvancedGames";
            navigation.Controls.AddRange(new Control[]{sitesSection,gamesSection});navigation.Resize+=(sender,e)=>{sitesSection.SetBounds(0,0,Px(230),Px(40));gamesSection.SetBounds(Px(242),0,Px(230),Px(40));};
            sitePage=new Panel{Dock=DockStyle.Fill};gamePage=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Visible=false};body.Controls.Add(gamePage);body.Controls.Add(sitePage);page.Controls.Add(body);page.Controls.Add(navigation);
            domainEditor=new DomainEditor{Dock=DockStyle.Fill};sitePage.Controls.Add(domainEditor);domainEditor.SaveRequested+=async(kind,text)=>await SaveDomains(kind,text);
            BuildGames(gamePage);sitesSection.Click+=(sender,e)=>ShowAdvanced(false);gamesSection.Click+=(sender,e)=>ShowAdvanced(true);
        }
        void ShowAdvanced(bool gamesVisible){advancedGames=gamesVisible;sitePage.Visible=!gamesVisible;gamePage.Visible=gamesVisible;sitesSection.BackColor=gamesVisible?Theme.ButtonSurface:Theme.Accent;gamesSection.BackColor=gamesVisible?Theme.Accent:Theme.ButtonSurface;}
        void BuildFilters(Panel page){
            page.AutoScroll=true;
            var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=4,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var startupCard=new SurfacePanel{Name="StartupCard",Height=Px(214),Dock=DockStyle.Top,Margin=new Padding(0,0,0,Px(16))};
            var startupTitle=Theme.Label("Автозапуск",18,true);
            startWindows=new ToggleOption{Name="WindowsStartup",Text="Запускать при старте Windows"};
            startupStatus=Theme.Label("Автозапуск выключен",9);startupStatus.ForeColor=Theme.Muted;
            var startupHelp=Theme.Label("Включает обход при загрузке Windows. При входе ByPass остаётся у часов.\nИспользуется последняя стратегия. Для Telegram оставьте ByPass работающим в трее.",9);startupHelp.ForeColor=Theme.Muted;
            startupCard.Controls.AddRange(new Control[]{startupTitle,startWindows,startupStatus,startupHelp});
            startupCard.Resize+=(sender,e)=>{int p=startupCard.Padding.Left,w=startupCard.Width-p*2;startupTitle.SetBounds(p,p,w,Px(32));startWindows.SetBounds(p,p+Px(47),w,Px(34));startupStatus.SetBounds(p,p+Px(87),w,Px(24));startupHelp.SetBounds(p,p+Px(122),w,Px(52));};
            startWindows.CheckedChanged+=async(sender,e)=>await SetWindowsStartup();
            var externalCard=new SurfacePanel{Name="ExternalBypassCard",Dock=DockStyle.Top,Height=Px(188),Margin=new Padding(0,0,0,Px(16))};
            var externalTitle=Theme.Label("Другой обход",18,true);externalStatus=Theme.Label("Другой обход не обнаружен",9);externalStatus.ForeColor=Theme.Muted;
            var externalHelp=Theme.Label("Останавливает обнаруженный winws и связанные службы.\nФайлы и настройки автозапуска остаются на месте.",9);externalHelp.ForeColor=Theme.Muted;
            externalBypass=Theme.Button("Отключить другой обход");externalBypass.Name="StopExternalBypass";externalBypass.Click+=async(s,e)=>await StopOtherBypass();externalCard.Controls.AddRange(new Control[]{externalTitle,externalStatus,externalHelp,externalBypass});
            externalCard.Resize+=(s,e)=>{int p=externalCard.Padding.Left,w=externalCard.Width-p*2;externalTitle.SetBounds(p,p,w,Px(32));externalStatus.SetBounds(p,p+Px(38),w,Px(24));externalHelp.SetBounds(p,p+Px(66),w,Px(42));externalBypass.SetBounds(p,p+Px(116),Math.Min(w,Px(300)),Px(40));};
            var appearance=new SurfacePanel{Name="AppearanceCard",Dock=DockStyle.Top,Height=Px(238),Margin=Padding.Empty};
            var title=Theme.Label("Оформление",18,true);var hint=Theme.Label("Три тёмные темы. Выбор применяется сразу и сохраняется.",9);hint.ForeColor=Theme.Muted;
            var choices=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Theme.Presets,p=>new ThemeChoice(p)));
            appearance.Controls.AddRange(new Control[]{title,hint});appearance.Controls.AddRange(choices);
            foreach(var choice in choices)choice.Click+=(sender,e)=>Guard(()=>ApplyVisualTheme(choice.Palette.Id));
            appearance.Resize+=(sender,e)=>{int p=appearance.Padding.Left,w=appearance.Width-p*2,gap=Px(12),width=(w-gap*2)/3;title.SetBounds(p,p,w,Px(32));hint.SetBounds(p,p+Px(39),w,Px(28));for(int i=0;i<choices.Length;i++)choices[i].SetBounds(p+i*(width+gap),p+Px(82),width,Px(116));};
            var shortcut=Theme.Button("Создать ярлык");shortcut.Margin=new Padding(0,Px(16),0,0);shortcut.Size=new Size(Px(210),Px(40));shortcut.Click+=(sender,e)=>Guard(()=>{AppShortcut.Create();shortcut.Text="Ярлык создан";});
            layout.Controls.Add(startupCard,0,0);layout.Controls.Add(externalCard,0,1);layout.Controls.Add(appearance,0,2);layout.Controls.Add(shortcut,0,3);page.Controls.Add(layout);
        }
        void ApplyVisualTheme(string id){
            var previous=Theme.Palette(Theme.Current);var next=Theme.Palette(id);string saved=store.Settings.VisualTheme;
            store.Settings.VisualTheme=next.Id;try{store.Save();}catch{store.Settings.VisualTheme=saved;throw;}
            SuspendLayout();try{Theme.Use(next.Id);Theme.Recolor(this,previous);tabs.SelectedIndex=tabs.SelectedIndex;ShowAdvanced(advancedGames);RefreshState();}finally{ResumeLayout(true);}Invalidate(true);
        }
        void BuildGames(Panel page){
            var surface=new SurfacePanel{Dock=DockStyle.Top,Height=Px(480)};page.Controls.Add(surface);Label title=Theme.Label("Игровые соединения",18,true),description=Theme.Label("Дополнительный обход для игр (GameFilter).\nДля YouTube и Discord обычно не нужен. Включайте, если игра\nне подключается; если появились сбои — выключите.",10);description.ForeColor=Theme.Muted;var mode=Theme.Label("Режим",10,true);games=new DarkSelect();games.Items.AddRange(new object[]{"Выключен · рекомендуется","TCP и UDP · все игровые соединения","Только TCP","Только UDP"});var advanced=Theme.Button("Настроить порты ▾");tcp=Input(0,0,200);udp=Input(0,0,200);Label tcpLabel=Theme.Label("TCP-порты",9,true),udpLabel=Theme.Label("UDP-порты",9,true),portHelp=Theme.Label("Если не уверены, оставьте 1024-65535.",9);portHelp.ForeColor=Theme.Muted;saveGameButton=Theme.Button("Сохранить",true);gameSaved=Theme.Label("",9);gameSaved.ForeColor=Theme.Success;saveGameButton.Click+=(s,e)=>Guard(()=>{SaveGame();gameSaved.Text="Настройки сохранены";});Action layout=()=>{int p=surface.Padding.Left,w=surface.Width-p*2;title.SetBounds(p,p,w,Px(36));description.SetBounds(p,p+Px(54),w,Px(76));mode.SetBounds(p,p+Px(152),w,Px(22));games.SetBounds(p,p+Px(185),Math.Min(w,Px(540)),Px(40));advanced.SetBounds(p,p+Px(245),Px(220),Px(36));tcpLabel.SetBounds(p,p+Px(301),Px(200),Px(24));udpLabel.SetBounds(p+Px(220),p+Px(301),Px(200),Px(24));tcp.SetBounds(p,p+Px(333),Px(200),Px(28));udp.SetBounds(p+Px(220),p+Px(333),Px(200),Px(28));portHelp.SetBounds(p,p+Px(379),w,Px(24));foreach(var c in new Control[]{tcp,udp,tcpLabel,udpLabel,portHelp})c.Visible=expandedPorts;saveGameButton.SetBounds(p,p+(expandedPorts?Px(423):Px(305)),Px(180),Px(42));gameSaved.SetBounds(p+Px(200),saveGameButton.Top,w-Px(200),Px(42));surface.Height=Px(expandedPorts?510:395);};advanced.Click+=(s,e)=>{expandedPorts=!expandedPorts;advanced.Text=expandedPorts?"Скрыть порты ▴":"Настроить порты ▾";layout();};surface.Controls.AddRange(new Control[]{title,description,mode,games,advanced,tcp,udp,tcpLabel,udpLabel,portHelp,saveGameButton,gameSaved});surface.Resize+=(s,e)=>layout();layout();
        }
        void BuildUpdates(Panel page){
            page.AutoScroll=true;page.AutoScrollMinSize=new Size(0,Px(668));
            appUpdateCard=new AppUpdateCard{Dock=DockStyle.Top};
            var engineCard=new SurfacePanel{Dock=DockStyle.Top,Height=Px(52)};
            var gap=new Panel{Dock=DockStyle.Top,Height=Px(16)};
            page.Controls.Add(engineCard);page.Controls.Add(gap);page.Controls.Add(appUpdateCard);
            appUpdateCard.Action.Click+=async(s,e)=>await AppUpdateAction();appUpdateCard.Cancel.Click+=(s,e)=>{appUpdates.Cancel();appUpdateCard.Cancel.Enabled=false;};
            autoShell=appUpdateCard.Startup;autoInstallShell=appUpdateCard.AutoInstall;autoInstallShell.CheckedChanged+=(s,e)=>SaveAutoUpdate(true,autoInstallShell.Checked);autoShell.CheckedChanged+=(s,e)=>{if(!loading)Guard(()=>{store.Settings.AutoAppUpdates=autoShell.Checked;store.Save();});};
            coreManual=Theme.Button("Релизы");coreManual.Visible=false;coreManual.Click+=(s,e)=>Guard(()=>Open("https://github.com/Flowseal/zapret-discord-youtube/releases/latest"));var coreTitle=Theme.Label("Ядро",18,true);version=Theme.Label("",10);version.ForeColor=Theme.Muted;updateText=Theme.Label("",10);updateText.ForeColor=Theme.Muted;check=Theme.Button("Проверить обновления");check.Click+=async(s,e)=>await CheckUpdates(false);install=Theme.Button("Установить",true);install.Click+=async(s,e)=>await Install();rollback=Theme.Button("Вернуть предыдущую версию");rollback.Click+=(s,e)=>Guard(()=>{if(runner.Running || runner.Conflict || scanning || starting || testing || domainEditor.Dirty)throw new InvalidOperationException("Остановите обход и сохраните изменения списков.");updater.Rollback();runner.SyncStartup(store.Current,store.Settings.Strategy);InvalidateChecks("Проверка требуется заново");release=null;LoadSettings();Append("Выполнен откат ядра.");});autoCheck=new ToggleOption{Text="Проверять при запуске"};autoInstallCore=new ToggleOption{Text="Скачивать и устанавливать автоматически"};autoInstallCore.CheckedChanged+=(s,e)=>SaveAutoUpdate(false,autoInstallCore.Checked);autoCheck.CheckedChanged+=(s,e)=>{if(!loading)Guard(()=>{store.Settings.CheckUpdates=autoCheck.Checked;store.Save();});};engineCard.Controls.AddRange(new Control[]{coreTitle,version,updateText,check,install,rollback,coreManual,autoCheck,autoInstallCore});engineCard.Resize+=(s,e)=>{int p=engineCard.Padding.Left,w=engineCard.Width-p*2;coreTitle.SetBounds(p,p,w-Px(160),Px(32));coreManual.SetBounds(engineCard.Width-p-Px(140),p,Px(140),Px(32));version.SetBounds(p,p+Px(37),w,Px(24));updateText.SetBounds(p,p+Px(70),w,Px(26));check.SetBounds(p,p+Px(108),Px(230),Px(40));install.SetBounds(p+Px(246),p+Px(108),Px(170),Px(40));rollback.SetBounds(p+Px(432),p+Px(108),Px(260),Px(40));autoCheck.SetBounds(p,p+Px(164),w,Px(34));autoInstallCore.SetBounds(p,p+Px(204),w,Px(34));};
            engineCard.Height=Px(282);
        }
        void SaveAutoUpdate(bool shell,bool enabled) {
            if(loading)return;
            Guard(()=>{if(shell){store.Settings.AutoInstallShell=enabled;if(enabled)autoShell.Checked=true;}else{store.Settings.AutoInstallCore=enabled;if(enabled)autoCheck.Checked=true;}store.Save();RefreshState();});
        }
        async System.Threading.Tasks.Task SaveDomains(int kind,string text){
            if(busy || scanning || starting || runner.Conflict)return;
            try{DomainLists.Normalize(text,kind);}catch(Exception ex){domainEditor.SetError(ex.Message);return;}
            bool reconnect=runner.Running,failed=false;starting=true;RefreshState();
            try{InvalidateChecks("Список сайтов изменён · проверяем заново");await StopChecks();if(reconnect)runner.Stop();DomainLists.Write(store.Current,kind,text);domainEditor.Saved();if(!reconnect)runner.SyncStartup(store.Current,store.Settings.Strategy);Append("Сохранён пользовательский список сайтов.");if(reconnect){await StartEngine(activeStrategy,System.Threading.CancellationToken.None);starting=false;await RunChecks();}}
            catch(Exception ex){failed=true;domainEditor.SetError(ex.Message);}
            finally{starting=false;if(!IsDisposed)RefreshState();}
            if(failed && reconnect && !runner.Running && !IsDisposed) {starting=true;try{await StartEngine(activeStrategy,System.Threading.CancellationToken.None);}catch(Exception restart){Append(restart.Message);}finally{starting=false;RefreshState();}}
        }
    }
}
