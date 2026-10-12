using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace ZapretDesktop {
    public class ResponsiveControl : UserControl {
        public ResponsiveControl(){AutoScaleMode=AutoScaleMode.None;}
        protected float uiScale=Theme.SystemScale;
        protected int S(float value){return (int)Math.Round(value*uiScale);}
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified){uiScale*=factor.Width;base.ScaleControl(factor,specified);}
    }
    public sealed class StrategyHistory : Control {
        readonly List<CandidateResult> entries=new List<CandidateResult>();readonly SlimScroll scroll;int offset,selected=-1;string current;float uiScale=Theme.SystemScale;
        public event Action<string> Pick;public int Count{get{return entries.Count;}}
        public StrategyHistory(){DoubleBuffered=true;BackColor=Theme.Surface;Font=new Font("Segoe UI",9);scroll=new SlimScroll{Dock=DockStyle.Right};Controls.Add(scroll);scroll.Changed+=v=>{offset=v;Invalidate();};}
        int Row{get{return (int)(36*uiScale);}}int Head{get{return (int)(34*uiScale);}}
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified){uiScale*=factor.Width;base.ScaleControl(factor,specified);}
        protected override void OnResize(EventArgs e){base.OnResize(e);Sync();}
        void Sync(){int visible=Math.Max(1,(Height-Head)/Row);offset=Math.Min(offset,Math.Max(0,entries.Count-visible));scroll.SetRange(entries.Count-visible,visible,offset);Invalidate();}
        public void Reset(string[] names){entries.Clear();entries.AddRange(names.Select(x=>new CandidateResult{Strategy=x}));offset=0;selected=-1;current=null;Sync();}
        public void Progress(string name){current=name;int i=entries.FindIndex(x=>x.Strategy==name);if(i>=0){int visible=Math.Max(1,(Height-Head)/Row);if(i<offset)offset=i;if(i>=offset+visible)offset=i-visible+1;Sync();}Invalidate();}
        public void Record(CandidateResult result){int i=entries.FindIndex(x=>x.Strategy==result.Strategy);if(i<0)entries.Add(result);else entries[i]=result;current=null;Sync();}
        int[] Columns(){int usable=Width-scroll.Width-20;int first=(int)(usable*.32),last=(int)(usable*.16),service=(usable-first-last)/ServiceChecks.Targets.Length;return new[]{first}.Concat(Enumerable.Repeat(service,ServiceChecks.Targets.Length)).Concat(new[]{last}).ToArray();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var columns=Columns();var headers=new[]{"Стратегия","YouTube","Картинки","Discord","Gateway","Обновл.","GitHub","Итог"};int x=12;for(int i=0;i<headers.Length;i++){Draw(e.Graphics,headers[i],new Rectangle(x,0,columns[i],Head),Theme.Muted,i>0);x+=columns[i];}using(var p=new Pen(Theme.Border))e.Graphics.DrawLine(p,12,Head,Width-14,Head);
            if(entries.Count==0){TextRenderer.DrawText(e.Graphics,"Здесь появятся результаты подбора",Font,new Rectangle(16,Head+12,Width-32,Height-Head-12),Theme.Muted,TextFormatFlags.Top|TextFormatFlags.EndEllipsis);return;}
            int visible=(int)Math.Ceiling((Height-Head)/(double)Row);for(int i=offset;i<Math.Min(entries.Count,offset+visible);i++){var item=entries[i];var r=new Rectangle(8,Head+(i-offset)*Row,Width-scroll.Width-16,Row);if(i==selected || item.Confirmed)using(var b=new SolidBrush(item.Confirmed?Color.FromArgb(39,64,48):Color.FromArgb(63,65,71)))e.Graphics.FillRectangle(b,r);x=12;Draw(e.Graphics,Theme.StrategyName(item.Strategy),new Rectangle(x,r.Y,columns[0],Row),Theme.Text,false);x+=columns[0];for(int j=0;j<ServiceChecks.Targets.Length;j++){var result=j<item.Results.Length?item.Results[j]:null;string text=result==null?"—":result.State==1?"✓":result.Failure=="dns"?"DNS":"×";Draw(e.Graphics,text,new Rectangle(x,r.Y,columns[j+1],Row),result==null?Theme.Muted:result.State==1?Theme.Success:Theme.Danger,true);x+=columns[j+1];}string total=item.Confirmed?"Выбрана":current==item.Strategy?"Проверяем":item.Results.Length>0?item.Passed+" / "+ServiceChecks.Targets.Length:item.Error.Length>0?"Ошибка":"—";Draw(e.Graphics,total,new Rectangle(x,r.Y,columns[columns.Length-1],Row),item.Confirmed?Theme.Success:Theme.Muted,true);}}
        void Draw(Graphics g,string text,Rectangle bounds,Color color,bool center){TextRenderer.DrawText(g,text,Font,bounds,color,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|(center?TextFormatFlags.HorizontalCenter:TextFormatFlags.Left));}
        protected override void OnMouseWheel(MouseEventArgs e){scroll.Value=offset-(e.Delta/120)*3;}
        protected override void OnMouseDown(MouseEventArgs e){selected=offset+(e.Y-Head)/Row;if(e.Y<Head || selected>=entries.Count)selected=-1;Invalidate();base.OnMouseDown(e);}
        protected override void OnDoubleClick(EventArgs e){if(selected>=0 && Pick!=null)Pick(entries[selected].Strategy);base.OnDoubleClick(e);}
    }
    public sealed class Dashboard : ResponsiveControl {
        readonly SurfacePanel selection=new SurfacePanel(),services=new SurfacePanel();readonly Label chooseTitle,serviceTitle,historyTitle;readonly Panel[] rows=new Panel[ServiceChecks.Targets.Length];
        public DarkSelect Profiles,Categories,ScanMode;public Button Apply,Scan,Cancel,Check,Dns;public Label Summary,ScanSummary;
        public readonly Label[] Statuses=new Label[ServiceChecks.Targets.Length],Times=new Label[ServiceChecks.Targets.Length];readonly Label[] names=new Label[ServiceChecks.Targets.Length];
        public readonly StrategyHistory History=new StrategyHistory();public event Action<string> Pick;
        public Dashboard(){DoubleBuffered=true;BackColor=Theme.Background;Font=new Font("Segoe UI",10);Size=new Size(1000,550);
            chooseTitle=Theme.Label("Стратегия обхода",12,true);serviceTitle=Theme.Label("Доступность сервисов",12,true);historyTitle=Theme.Label("Результаты подбора",10,true);
            Categories=new DarkSelect();Categories.Items.AddRange(new object[]{"Все стратегии","Основная","ALT","FAKE TLS AUTO","SIMPLE FAKE","Экспериментальные"});
            Profiles=new DarkSelect{Format=x=>Theme.StrategyName(Convert.ToString(x))};ScanMode=new DarkSelect{Font=new Font("Segoe UI",9)};ScanMode.Items.AddRange(new object[]{"Быстрый · первый рабочий вариант","Полный · сравнить все стратегии"});ScanMode.SelectedIndex=0;
            Apply=Theme.Button("Применить и проверить",true);Scan=Theme.Button("Подобрать автоматически",true);Cancel=Theme.Button("Отмена");
            ScanSummary=Theme.Label("Быстрый подбор остановится на первом\nподтверждённом рабочем варианте.",9);ScanSummary.ForeColor=Theme.Muted;ScanMode.SelectedIndexChanged+=(s,e)=>ScanSummary.Text=ScanMode.SelectedIndex==0?"Остановимся на первом рабочем варианте.":"Проверим все варианты и сравним результат.";
            selection.Controls.AddRange(new Control[]{chooseTitle,Categories,Profiles,Apply,ScanMode,Scan,Cancel,ScanSummary});
            Summary=Theme.Label("Нажмите «Применить и проверить»",9);Summary.ForeColor=Theme.Muted;
            services.Controls.AddRange(new Control[]{serviceTitle,Summary});
            for(int i=0;i<ServiceChecks.Targets.Length;i++){rows[i]=new Panel{BackColor=Theme.Surface};names[i]=Theme.Label(ServiceChecks.Targets[i].Name,9);Statuses[i]=Theme.Label("Не проверено",9);Statuses[i].ForeColor=Theme.Muted;Times[i]=Theme.Label("—",9);Times[i].ForeColor=Theme.Muted;Times[i].TextAlign=ContentAlignment.MiddleRight;Statuses[i].Cursor=Cursors.Hand;Statuses[i].Click+=(s,e)=>{var label=(Label)s;if(label.Tag!=null && Convert.ToString(label.Tag).Length>0)MessageBox.Show(this,Convert.ToString(label.Tag),"Результат проверки");};rows[i].Controls.AddRange(new Control[]{names[i],Statuses[i],Times[i]});services.Controls.Add(rows[i]);}
            Check=Theme.Button("Проверить сервисы");Dns=Theme.Button("Диагностика DNS");services.Controls.AddRange(new Control[]{Check,Dns});History.Pick+=name=>{if(Pick!=null)Pick(name);};Controls.AddRange(new Control[]{selection,services,historyTitle,History});
        }
        protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(selection==null || Profiles==null)return;int gap=S(16),left=Math.Max(S(304),(int)(Width*.35));bool compact=Height<S(505);int top=S(compact?316:350);selection.SetBounds(0,0,left,top);services.SetBounds(left+gap,0,Width-left-gap,top);
            int pad=S(18),w=left-pad*2;chooseTitle.SetBounds(pad,S(16),w,S(26));Categories.SetBounds(pad,S(52),w,S(36));Profiles.SetBounds(pad,S(96),w,S(36));Apply.SetBounds(pad,S(144),w,S(40));ScanMode.SetBounds(pad,S(compact?196:204),w,S(36));Scan.SetBounds(pad,S(compact?244:252),w-S(82),S(40));Cancel.SetBounds(left-pad-S(74),Scan.Top,S(74),S(40));ScanSummary.SetBounds(pad,S(compact?292:302),w,S(42));ScanSummary.Visible=!compact;
            int sw=services.Width-pad*2;serviceTitle.SetBounds(pad,S(16),sw,S(26));Summary.SetBounds(pad,S(48),sw,S(38));int row=(compact?157:191)/ServiceChecks.Targets.Length;for(int i=0;i<ServiceChecks.Targets.Length;i++){rows[i].SetBounds(pad,S(91+i*row),sw,S(row-2));int nameW=(int)(sw*.41),timeW=S(72);names[i].SetBounds(0,0,nameW,rows[i].Height);names[i].TextAlign=ContentAlignment.MiddleLeft;Statuses[i].SetBounds(nameW,0,sw-nameW-timeW-S(8),rows[i].Height);Statuses[i].TextAlign=ContentAlignment.MiddleLeft;Times[i].SetBounds(sw-timeW,0,timeW,rows[i].Height);}
            int by=top-pad-S(40);Check.SetBounds(pad,by,(sw-S(10))/2,S(40));Dns.SetBounds(Check.Right+S(10),by,sw-Check.Width-S(10),S(40));historyTitle.SetBounds(0,top+S(18),Width,S(22));History.SetBounds(0,top+S(46),Width,Math.Max(S(42),Height-top-S(46)));}
        public void SetResult(int index,CheckResult result){Statuses[index].Text=result.State==1?"Доступен":result.Failure=="dns"?"Ошибка DNS":result.Failure=="timeout"?"Нет ответа":result.State==2?result.Detail.Split('|')[0]:"Нет соединения";Statuses[index].Tag=result.Detail;Statuses[index].AccessibleDescription=result.Detail;Statuses[index].ForeColor=result.State==1?Theme.Success:result.State==2?Theme.Warning:Theme.Danger;Times[index].Text=result.Milliseconds+" мс";}
        public void ClearResults(string reason){Summary.Text=reason;for(int i=0;i<ServiceChecks.Targets.Length;i++){Statuses[i].Text="Не проверено";Statuses[i].Tag=null;Statuses[i].ForeColor=Theme.Muted;Times[i].Text="—";}}
        public void ResetHistory(string[] names){History.Reset(names);}public void Progress(string name){History.Progress(name);}public void Record(CandidateResult result){History.Record(result);}
    }
}
