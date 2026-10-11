using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class SiteEntries : ResponsiveControl {
        readonly SlimScroll scroll=new SlimScroll();string[] entries=new string[0];int offset;
        public event Action<string> RemoveRequested;
        public string EmptyText="Своих правил пока нет.\nДля YouTube и Discord добавлять ничего не нужно.";
        public SiteEntries(){DoubleBuffered=true;BackColor=Theme.Input;Font=new Font("Segoe UI",10);scroll.Dock=DockStyle.Right;scroll.BackColor=BackColor;Controls.Add(scroll);scroll.Changed+=value=>{offset=value;Invalidate();};}
        public void SetEntries(string text){entries=text.Replace("\r","").Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0 && !x.StartsWith("#")).ToArray();Sync();Invalidate();}
        void Sync(){int page=Math.Max(1,Height/S(44));offset=Math.Min(offset,Math.Max(0,entries.Length-page));scroll.SetRange(entries.Length-page,page,offset);}
        protected override void OnResize(EventArgs e){base.OnResize(e);Sync();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(entries.Length==0){TextRenderer.DrawText(e.Graphics,EmptyText,Font,new Rectangle(S(24),S(24),Width-S(48),Height-S(48)),Theme.Muted,TextFormatFlags.Top|TextFormatFlags.WordBreak);return;}int row=S(44),count=(int)Math.Ceiling(Height/(double)row);for(int i=offset;i<Math.Min(entries.Length,offset+count);i++){int y=(i-offset)*row;TextRenderer.DrawText(e.Graphics,entries[i],Font,new Rectangle(S(16),y,Width-S(78),row),Theme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);TextRenderer.DrawText(e.Graphics,"×",Font,new Rectangle(Width-S(52),y,S(32),row),Enabled?Theme.Muted:Theme.Border,TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter);using(var pen=new Pen(Theme.Border))e.Graphics.DrawLine(pen,S(16),y+row-1,Width-S(18),y+row-1);}}
        protected override void OnMouseMove(MouseEventArgs e){Cursor=Enabled && e.X>Width-S(56)?Cursors.Hand:Cursors.Default;base.OnMouseMove(e);}
        protected override void OnMouseClick(MouseEventArgs e){int index=offset+e.Y/S(44);if(Enabled && e.Button==MouseButtons.Left && e.X>=Width-S(56) && e.X<Width-scroll.Width && index<entries.Length && RemoveRequested!=null)RemoveRequested(entries[index]);base.OnMouseClick(e);}
        protected override void OnMouseWheel(MouseEventArgs e){scroll.Value=offset-e.Delta/120*3;}
    }
    public sealed class DomainEditor : ResponsiveControl {
        readonly Label title,description,status,hint;readonly DarkSelect list;
        readonly DarkEditor text;readonly SiteEntries entries;readonly TextBox input;readonly SurfacePanel inputFrame;
        readonly Button save,discard,add,advanced;string engine,baseline="";
        int loaded;bool loading,editingText;bool allowed=true;
        public event Action<int,string> SaveRequested;
        public bool Dirty{get{return text.Editor.Text!=baseline;}}
        public DomainEditor(){
            BackColor=Theme.Background;
            title=Theme.Label("Правила для сайтов",18,true);
            description=Theme.Label("Настройка обработки трафика отдельных сайтов.\nIP и страна не меняются; ограничения самого сервиса не снимаются.",10);description.ForeColor=Theme.Muted;
            list=new DarkSelect();list.Items.AddRange(new object[]{"Обрабатывать эти сайты","Не обрабатывать эти сайты","IP-исключения · дополнительно"});list.SelectedIndex=0;list.SelectedIndexChanged+=(s,e)=>ChangeList();
            text=new DarkEditor{Visible=false};entries=new SiteEntries();
            text.Editor.TextChanged+=(s,e)=>{entries.SetEntries(text.Editor.Text);if(!loading){status.Text=Dirty?"Нажмите «Сохранить», чтобы применить изменения":"";status.ForeColor=Theme.Muted;RefreshSave();}};
            inputFrame=new SurfacePanel{BackColor=Theme.Input,Padding=Padding.Empty};input=new TextBox{Name="site-input",BackColor=Theme.Input,ForeColor=Theme.Text,BorderStyle=BorderStyle.None,Font=new Font("Segoe UI",11)};inputFrame.Controls.Add(input);inputFrame.Resize+=(s,e)=>input.SetBounds(S(12),Math.Max(0,(inputFrame.Height-input.PreferredHeight)/2),Math.Max(1,inputFrame.Width-S(24)),input.PreferredHeight);
            input.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){AddSite();e.SuppressKeyPress=true;}};
            add=Theme.Button("Добавить",true);add.Click+=(s,e)=>AddSite();
            entries.RemoveRequested+=site=>{text.Editor.Text=String.Join(Environment.NewLine,text.Editor.Text.Replace("\r","").Split('\n').Where(x=>!String.Equals(x.Trim(),site,StringComparison.OrdinalIgnoreCase)));};
            advanced=Theme.Button("Редактировать списком");advanced.Click+=(s,e)=>{editingText=!editingText;advanced.Text=editingText?"Показать сайты":"Редактировать списком";text.Visible=editingText;entries.Visible=!editingText;PerformLayout();};
            save=Theme.Button("Сохранить",true);save.Click+=(s,e)=>{if(SaveRequested!=null)SaveRequested(loaded,text.Editor.Text);};
            discard=Theme.Button("Отменить изменения");discard.Click+=(s,e)=>Discard();
            status=Theme.Label("",9);status.ForeColor=Theme.Muted;
            hint=Theme.Label("Введите адрес сайта, например example.com, и нажмите «Добавить».",9);hint.ForeColor=Theme.Muted;
            Controls.AddRange(new Control[]{title,description,list,inputFrame,add,advanced,text,entries,hint,save,discard,status});
        }
        void AddSite(){if(!allowed)return;try{string site=DomainLists.FromInput(input.Text,loaded);if(text.Editor.Text.Replace("\r","").Split('\n').Any(x=>String.Equals(x.Trim(),site,StringComparison.OrdinalIgnoreCase)))throw new System.IO.InvalidDataException("Этот адрес уже добавлен.");text.Editor.Text=text.Editor.Text.TrimEnd()+Environment.NewLine+site+Environment.NewLine;input.Clear();input.Focus();}catch(Exception ex){SetError(ex.Message);}}
        public void LoadEngine(string path){engine=path;loading=true;loaded=Math.Max(0,list.SelectedIndex);text.Editor.Text=DomainLists.Read(path,loaded);baseline=text.Editor.Text;loading=false;status.Text="";entries.SetEntries(text.Editor.Text);RefreshSave();UpdateExplanation();}
        void UpdateExplanation(){
            description.Text=loaded==0?"Настройка обработки трафика отдельных сайтов.\nIP и страна не меняются; ограничения самого сервиса не снимаются.":loaded==1?"Исключите сайт из обработки, если она мешает его работе.":"Дополнительные исключения по IP. Если не уверены, оставьте список пустым.";
            hint.Text=loaded==2?"Введите IP-адрес или сеть, например 192.168.1.0/24.":"Введите адрес сайта, например example.com, и нажмите «Добавить».";
            entries.EmptyText=loaded==0?"Своих правил пока нет.\nДля YouTube и Discord добавлять ничего не нужно.":"Исключений пока нет.\nДобавлять их необязательно.";entries.Invalidate();
        }
        void ChangeList(){if(engine==null || loading)return;if(Dirty){loading=true;list.SelectedIndex=loaded;loading=false;status.Text="Сначала сохраните или отмените изменения.";return;}loaded=list.SelectedIndex;input.Clear();LoadEngine(engine);}
        public void Saved(){LoadEngine(engine);status.Text="Сохранено";status.ForeColor=Theme.Success;}
        public void SetAllowed(bool canSave,bool connected){allowed=canSave;text.Editor.ReadOnly=!canSave;list.Enabled=input.Enabled=add.Enabled=entries.Enabled=canSave;save.Text=connected?"Сохранить и применить":"Сохранить";RefreshSave();}
        void RefreshSave(){save.Enabled=allowed && Dirty;discard.Enabled=allowed && Dirty;}
        public void SetError(string message){status.Text=message;status.ForeColor=Theme.Danger;}
        public void Discard(){LoadEngine(engine);}
        public TextBox TextEditor{get{return text.Editor;}}
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);if(title==null)return;
            title.SetBounds(0,0,Width,S(38));description.SetBounds(0,S(43),Width,S(45));
            list.SetBounds(0,S(101),Math.Min(Width,S(420)),S(40));
            hint.SetBounds(0,S(154),Math.Max(S(100),Width-S(255)),S(38));advanced.SetBounds(Width-S(230),S(148),S(230),S(36));
            inputFrame.SetBounds(0,S(202),Width-S(152),S(40));add.SetBounds(Width-S(138),S(202),S(138),S(40));
            var bounds=new Rectangle(0,S(262),Width,Math.Max(S(80),Height-S(366)));text.Bounds=entries.Bounds=bounds;
            save.SetBounds(0,Height-S(56),S(230),S(40));discard.SetBounds(S(244),Height-S(56),S(220),S(40));status.SetBounds(0,Height-S(92),Width,S(30));
            inputFrame.Visible=add.Visible=!editingText;
        }
    }
}
