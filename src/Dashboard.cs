using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZapretDesktop {
    public sealed class Dashboard : UserControl {
        readonly Color card = Color.FromArgb(25,31,45), muted = Color.FromArgb(160,174,195), accent = Color.FromArgb(116,220,183);
        readonly ToolTip tips = new ToolTip();
        public ComboBox Profiles, Categories;
        public Button Apply, Scan, Cancel, Check, Dns;
        public Label Summary, ScanSummary;
        public readonly Label[] Statuses = new Label[ServiceChecks.Targets.Length], Times = new Label[ServiceChecks.Targets.Length];
        public readonly DataGridView History;
        public event Action<string> Pick;
        public Dashboard() {
            BackColor = Color.FromArgb(16,20,30); Size = new Size(820,505); Font = new Font("Segoe UI",10);
            Controls.Add(Text("Стратегия обхода",11,Color.White,12,14,328,26));
            Categories = Combo(12,46,322); Categories.Items.AddRange(new object[] {"Все стратегии","Основная","ALT","FAKE TLS AUTO","SIMPLE FAKE","Экспериментальные"});
            Profiles = Combo(12,87,322); Controls.AddRange(new Control[] {Categories,Profiles});
            Apply = Action("Применить и проверить",12,132,322,true);
            Scan = Action("Автоподбор всех стратегий",12,181,235,true); Cancel = Action("Отмена",256,181,78,false);
            Controls.AddRange(new Control[] {Apply,Scan,Cancel});
            ScanSummary = Text("Выбор и проверка — в одном окне.\nАвтоподбор займёт несколько минут.",9,muted,12,232,325,68); Controls.Add(ScanSummary);
            Controls.Add(Text("Сервисы на текущей стратегии",11,Color.White,360,14,445,26));
            Summary = Text("Ещё не проверены",9,muted,360,46,438,40); Controls.Add(Summary);
            for (int i = 0; i < Statuses.Length; i++) {
                var row = new Panel {Location = new Point(360,91+i*32),Size = new Size(440,28),BackColor = card};
                row.Controls.Add(Text(ServiceChecks.Targets[i].Name,9,Color.White,8,5,170,22));
                Statuses[i] = Text("—",9,muted,178,5,195,22); Times[i] = Text("—",8,muted,375,6,60,20);
                Statuses[i].Click += (s,e) => {var label=(Label)s;if(label.Tag!=null && Convert.ToString(label.Tag).Length>0)MessageBox.Show(this,Convert.ToString(label.Tag),"Результат проверки");};
                row.Controls.AddRange(new Control[] {Statuses[i],Times[i]}); Controls.Add(row);
            }
            Check = Action("Проверить сервисы",360,266,206,false); Dns = Action("Диагностика DNS",580,266,220,false); Controls.AddRange(new Control[] {Check,Dns});
            Controls.Add(Text("История автоподбора · двойной щелчок выбирает стратегию",9,muted,12,317,800,22));
            History = new DataGridView {Location = new Point(12,348),Size = new Size(788,144),ReadOnly = true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
                RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=card,BorderStyle=BorderStyle.None,
                EnableHeadersVisualStyles=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,AllowUserToResizeRows=false};
            History.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle {BackColor=card,ForeColor=muted,SelectionBackColor=card,Font=new Font("Segoe UI",9)};
            History.DefaultCellStyle = new DataGridViewCellStyle {BackColor=BackColor,ForeColor=muted,SelectionBackColor=Color.FromArgb(40,52,68),SelectionForeColor=Color.White,Font=new Font("Segoe UI",8)};
            History.GridColor=card; History.RowTemplate.Height=27;
            foreach (var title in new[] {"Стратегия","YouTube","Картинки","Discord","API","GitHub","Итог"}) History.Columns.Add(title,title);
            History.Columns[0].FillWeight=210; History.Columns[6].FillWeight=150;
            foreach (DataGridViewColumn column in History.Columns) column.SortMode=DataGridViewColumnSortMode.NotSortable;
            History.CellDoubleClick += (s,e) => {if(e.RowIndex>=0 && Pick!=null) Pick(Convert.ToString(History.Rows[e.RowIndex].Tag));}; Controls.Add(History);
        }
        new Label Text(string text,int size,Color color,int x,int y,int w,int h) {return new Label {Text=text,Font=new Font("Segoe UI",size),ForeColor=color,Location=new Point(x,y),Size=new Size(w,h)};}
        ComboBox Combo(int x,int y,int w) {
            var combo = new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(x,y),Size=new Size(w,32),BackColor=card,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=25};
            combo.DrawItem += (s,e) => {using(var brush=new SolidBrush(card)) e.Graphics.FillRectangle(brush,e.Bounds); if(e.Index>=0) TextRenderer.DrawText(e.Graphics,combo.Items[e.Index].ToString().Replace(".bat",""),Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),Color.White,TextFormatFlags.VerticalCenter);};return combo;
        }
        Button Action(string text,int x,int y,int w,bool primary) {var button=new FlatAction {Text=text,Location=new Point(x,y),Size=new Size(w,38),BackColor=primary?accent:card,ForeColor=primary?BackColor:Color.White,FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand};button.FlatAppearance.BorderSize=0;return button;}
        void SetTip(Label label,string text) {
            label.AccessibleDescription=text;label.Tag=text;
            // Native ToolTip registration can fail for hidden/off-screen windows. Results must still render.
            try {tips.SetToolTip(label,text);}catch(InvalidOperationException){}catch(ArgumentException){}
        }
        public void SetResult(int index,CheckResult result) {
            Statuses[index].Text=result.Detail.Split('|')[0].Trim(); SetTip(Statuses[index],result.Detail);
            Statuses[index].ForeColor=result.State==1?accent:(result.State==2?Color.FromArgb(239,198,114):Color.FromArgb(242,139,139)); Times[index].Text=result.Milliseconds+" мс";
        }
        public void ClearResults(string reason) {Summary.Text=reason;for(int i=0;i<Statuses.Length;i++){Statuses[i].Text="—";Statuses[i].ForeColor=muted;Times[i].Text="—";SetTip(Statuses[i],"");}}
        public void ResetHistory(string[] strategies) {History.Rows.Clear();foreach(var name in strategies){int i=History.Rows.Add(name.Replace("general ","").Replace(".bat",""),"—","—","—","—","—","Ожидает");History.Rows[i].Tag=name;}}
        public void Progress(string strategy) {foreach(DataGridViewRow row in History.Rows)if(Convert.ToString(row.Tag)==strategy){row.Cells[6].Value="Проверка…";History.FirstDisplayedScrollingRowIndex=row.Index;break;}}
        public void Record(CandidateResult candidate) {
            var row=History.Rows.Cast<DataGridViewRow>().FirstOrDefault(x=>Convert.ToString(x.Tag)==candidate.Strategy); if(row==null)return;
            for(int i=0;i<candidate.Results.Length;i++){var result=candidate.Results[i];row.Cells[i+1].Value=result.State==1?"✓":(result.Failure=="dns"?"DNS":"×");row.Cells[i+1].Style.ForeColor=result.State==1?accent:Color.FromArgb(242,139,139);row.Cells[i+1].ToolTipText=result.Detail;}
            row.Cells[6].Value=candidate.Confirmed?"Выбрана ✓":(candidate.Results.Length>0?candidate.Passed+"/"+ServiceChecks.Targets.Length:"Ошибка"); row.Cells[6].ToolTipText=candidate.Error;
        }
        protected override void Dispose(bool disposing){if(disposing)tips.Dispose();base.Dispose(disposing);}
    }
}
