using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
namespace ZapretDesktop {
    public static class Theme {
        public static readonly float SystemScale=ReadScale();static float ReadScale(){using(var g=Graphics.FromHwnd(IntPtr.Zero))return g.DpiX/96f;}
        public static Color Background=Color.FromArgb(49,51,56), Surface=Color.FromArgb(43,45,49), Input=Color.FromArgb(30,31,34), Border=Color.FromArgb(63,65,71), Text=Color.FromArgb(242,243,245), Muted=Color.FromArgb(181,186,193), Accent=Color.FromArgb(88,101,242), Success=Color.FromArgb(59,165,92), Warning=Color.FromArgb(240,178,50), Danger=Color.FromArgb(237,66,69);
        public static Color ButtonSurface=Color.FromArgb(64,66,73);
        public static string Current="graphite";
        internal static readonly VisualPalette[] Presets={
            new VisualPalette("graphite","Графит",Color.FromArgb(49,51,56),Color.FromArgb(43,45,49),Color.FromArgb(30,31,34),Color.FromArgb(63,65,71),Color.FromArgb(242,243,245),Color.FromArgb(181,186,193),Color.FromArgb(88,101,242),Color.FromArgb(64,66,73)),
            new VisualPalette("midnight","Полночь",Color.FromArgb(20,27,43),Color.FromArgb(26,35,53),Color.FromArgb(13,19,32),Color.FromArgb(49,64,88),Color.FromArgb(236,242,252),Color.FromArgb(166,185,211),Color.FromArgb(91,135,246),Color.FromArgb(43,57,79)),
            new VisualPalette("obsidian","Обсидиан",Color.FromArgb(18,21,23),Color.FromArgb(25,30,32),Color.FromArgb(11,14,16),Color.FromArgb(47,58,60),Color.FromArgb(235,245,242),Color.FromArgb(163,186,180),Color.FromArgb(42,151,132),Color.FromArgb(40,54,53))
        };
        internal static VisualPalette Palette(string id){return Presets.FirstOrDefault(p=>p.Id==id)??Presets[0];}
        internal static void Use(string id){var p=Palette(id);Current=p.Id;Background=p.Background;Surface=p.Surface;Input=p.Input;Border=p.Border;Text=p.Text;Muted=p.Muted;Accent=p.Accent;ButtonSurface=p.Button;}
        internal static void Recolor(Control root,VisualPalette previous){
            var old=new[]{previous.Background,previous.Surface,previous.Input,previous.Border,previous.Text,previous.Muted,previous.Accent,previous.Button};
            var next=new[]{Background,Surface,Input,Border,Text,Muted,Accent,ButtonSurface};
            RecolorControl(root,old,next);root.Invalidate(true);
        }
        static Color Map(Color color,Color[] old,Color[] next){if(color==Color.Transparent)return color;for(int i=0;i<old.Length;i++)if(color.ToArgb()==old[i].ToArgb())return next[i];return color;}
        static void RecolorControl(Control control,Color[] old,Color[] next){
            control.BackColor=Map(control.BackColor,old,next);control.ForeColor=Map(control.ForeColor,old,next);
            foreach(Control child in control.Controls)RecolorControl(child,old,next);
        }
        public static GraphicsPath Round(Rectangle r,int radius) {int d=radius*2;var p=new GraphicsPath();p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
        public static Label Label(string text,float size=10,bool bold=false) {return new Label{Text=text,AutoEllipsis=true,ForeColor=Theme.Text,BackColor=Color.Transparent,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular)};}
        public static FlatAction Button(string text,bool primary=false) {return new FlatAction{Text=text,Size=new Size(180,40),BackColor=primary?Accent:ButtonSurface,ForeColor=Theme.Text,FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand,Font=new Font("Segoe UI",10)};}
        public static string StrategyName(string name) {return name=="general.bat"?"Основная · general":name.Replace("general (","").Replace(").bat","").Replace(".bat","");}
    }
    public sealed class SurfacePanel : Panel {
        public SurfacePanel(){DoubleBuffered=true;BackColor=Theme.Surface;Padding=new Padding((int)(20*Theme.SystemScale));}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var p=Theme.Round(new Rectangle(0,0,Width-1,Height-1),10))using(var pen=new Pen(Theme.Border)) {e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.DrawPath(pen,p);}}
    }
    public sealed class SlimScroll : Control {
        int value,maximum,page=1;bool dragging;int dragY,dragValue;
        public event Action<int> Changed;
        public int Value{get{return value;}set{int next=Math.Max(0,Math.Min(maximum,value));if(next==this.value)return;this.value=next;Invalidate();if(Changed!=null)Changed(next);}}
        public void SetRange(int max,int visible,int position){maximum=Math.Max(0,max);page=Math.Max(1,visible);value=Math.Max(0,Math.Min(maximum,position));Visible=maximum>0;Invalidate();}
        public SlimScroll(){Width=(int)(10*Theme.SystemScale);DoubleBuffered=true;Cursor=Cursors.Hand;BackColor=Theme.Surface;}
        Rectangle Thumb {get{int h=Math.Min(Height,Math.Max(28,Height*page/(maximum+page)));return new Rectangle(2,maximum==0?0:(Height-h)*value/maximum,6,h);}}
        protected override void OnPaint(PaintEventArgs e){using(var b=new SolidBrush(Color.FromArgb(79,84,92)))using(var p=Theme.Round(Thumb,3)){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillPath(b,p);}}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(Thumb.Contains(e.Location)){dragging=true;dragY=e.Y;dragValue=value;Capture=true;}else Value=value+(e.Y<Thumb.Top?-page:page);}
        protected override void OnMouseMove(MouseEventArgs e){if(dragging)Value=dragValue+(e.Y-dragY)*maximum/Math.Max(1,Height-Thumb.Height);}
        protected override void OnMouseUp(MouseEventArgs e){dragging=false;Capture=false;}
        protected override void OnMouseWheel(MouseEventArgs e){Value=value-(e.Delta/120)*3;}
    }
    public sealed class DarkSelect : Control {
        public readonly List<object> Items=new List<object>();int selected=-1;bool hover;ToolStripDropDown popup;
        public event EventHandler SelectedIndexChanged;
        public Func<object,string> Format;
        public int SelectedIndex{get{return selected;}set{int next=value>=0 && value<Items.Count?value:-1;bool change=next!=selected;selected=next;Invalidate();if(change && SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);}}
        public object SelectedItem{get{return selected>=0 && selected<Items.Count?Items[selected]:null;}set{SelectedIndex=Items.IndexOf(value);}}
        public DarkSelect(){DoubleBuffered=true;TabStop=true;Size=new Size(250,40);BackColor=Theme.Input;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);Cursor=Cursors.Hand;}
        internal string Display(object value){return Format==null?Convert.ToString(value).Replace(".bat",""):Format(value);}
        protected override void OnPaint(PaintEventArgs e){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var p=Theme.Round(new Rectangle(0,0,Width-1,Height-1),5))using(var b=new SolidBrush(BackColor))using(var pen=new Pen(Focused?Theme.Accent:hover?Color.FromArgb(92,95,102):Theme.Border)){e.Graphics.FillPath(b,p);e.Graphics.DrawPath(pen,p);}TextRenderer.DrawText(e.Graphics,SelectedItem==null?"Выберите вариант":Display(SelectedItem),Font,new Rectangle(12,0,Width-42,Height),Enabled?ForeColor:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);using(var pen=new Pen(Theme.Muted,2)){int x=Width-22,y=Height/2-2;e.Graphics.DrawLines(pen,new[]{new Point(x-4,y),new Point(x,y+4),new Point(x+4,y)});}}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
        protected override void OnClick(EventArgs e){base.OnClick(e);Open();}
        void Open(){if(!Enabled || Items.Count==0)return;if(popup!=null){ClosePopup();return;}Focus();var list=new SelectList(this);var host=new ToolStripControlHost(list){Margin=Padding.Empty,Padding=Padding.Empty,AutoSize=false,Size=list.Size};popup=new ToolStripDropDown{Padding=new Padding(1),BackColor=Theme.Border,AutoSize=true};popup.Items.Add(host);popup.Closed+=(s,e)=>{var old=(ToolStripDropDown)s;popup=null;Invalidate();if(IsHandleCreated && !Disposing && !IsDisposed)BeginInvoke(new Action(()=>old.Dispose()));};popup.Show(this,new Point(0,Height+4));list.Focus();}
        void ClosePopup(){var old=popup;if(old!=null){old.Close();old.Dispose();}}
        internal void Choose(int index){SelectedIndex=index;ClosePopup();Focus();}
        protected override bool IsInputKey(Keys key){return key==Keys.Up || key==Keys.Down || base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space || e.KeyCode==Keys.Enter || e.KeyCode==Keys.F4){Open();e.Handled=true;}else if(e.KeyCode==Keys.Down){SelectedIndex=Math.Min(Items.Count-1,selected+1);e.Handled=true;}else if(e.KeyCode==Keys.Up){SelectedIndex=Math.Max(0,selected-1);e.Handled=true;}base.OnKeyDown(e);}
        protected override void Dispose(bool disposing){if(disposing)ClosePopup();base.Dispose(disposing);}
        sealed class SelectList : Control {
            readonly DarkSelect owner;readonly SlimScroll scroll;int offset,highlight;int row=(int)(36*Theme.SystemScale);
            public SelectList(DarkSelect owner){this.owner=owner;Size=new Size(owner.Width,Math.Min(9,owner.Items.Count)*row+8);Font=owner.Font;BackColor=Theme.Input;DoubleBuffered=true;highlight=Math.Max(0,owner.SelectedIndex);scroll=new SlimScroll{Dock=DockStyle.Right,BackColor=BackColor};Controls.Add(scroll);scroll.Changed+=v=>{offset=v;Invalidate();};EnsureVisible();}
            void EnsureVisible(){int visible=Math.Max(1,(Height-8)/row);if(highlight<offset)offset=highlight;if(highlight>=offset+visible)offset=highlight-visible+1;scroll.SetRange(owner.Items.Count-visible,visible,offset);Invalidate();}
            protected override void OnPaint(PaintEventArgs e){int visible=(Height-8)/row;for(int i=offset;i<Math.Min(owner.Items.Count,offset+visible);i++){var r=new Rectangle(4,4+(i-offset)*row,Width-18,row);if(i==highlight)using(var b=new SolidBrush(Theme.Accent))e.Graphics.FillRectangle(b,r);TextRenderer.DrawText(e.Graphics,owner.Display(owner.Items[i]),Font,new Rectangle(r.X+10,r.Y,r.Width-16,r.Height),Theme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);}}
            protected override void OnMouseMove(MouseEventArgs e){highlight=Math.Max(0,Math.Min(owner.Items.Count-1,offset+(e.Y-4)/row));Invalidate();}
            protected override void OnMouseClick(MouseEventArgs e){owner.Choose(highlight);}
            protected override void OnMouseWheel(MouseEventArgs e){scroll.Value=offset-(e.Delta/120)*3;}
            protected override bool IsInputKey(Keys key){return key==Keys.Down || key==Keys.Up || base.IsInputKey(key);}
            protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Down)highlight=Math.Min(owner.Items.Count-1,highlight+1);else if(e.KeyCode==Keys.Up)highlight=Math.Max(0,highlight-1);else if(e.KeyCode==Keys.Enter){owner.Choose(highlight);return;}else if(e.KeyCode==Keys.Escape){owner.ClosePopup();return;}EnsureVisible();e.Handled=true;}
        }
    }
}
