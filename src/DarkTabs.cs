using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class DarkTabs : ResponsiveControl {
        readonly Panel navigation,content;readonly List<Button> buttons=new List<Button>();
        public readonly List<Panel> TabPages=new List<Panel>();int selected=-1;
        public int SelectedIndex {get{return selected;}set{if(value<0 || value>=TabPages.Count)return;selected=value;for(int i=0;i<TabPages.Count;i++){TabPages[i].Visible=i==value;buttons[i].BackColor=i==value?Theme.Accent:Theme.Surface;buttons[i].ForeColor=i==value?Theme.Text:Theme.Muted;}TabPages[value].BringToFront();}}
        public DarkTabs(){DoubleBuffered=true;BackColor=Theme.Background;navigation=new Panel{Dock=DockStyle.Top,Height=S(58),BackColor=Theme.Surface,Padding=new Padding(24,9,24,9)};content=new Panel{Dock=DockStyle.Fill,BackColor=BackColor};Controls.Add(content);Controls.Add(navigation);navigation.Resize+=(s,e)=>LayoutButtons();}
        void LayoutButtons(){int x=S(24);foreach(var button in buttons){button.SetBounds(x,S(9),Math.Max(S(106),TextRenderer.MeasureText(button.Text,button.Font).Width+S(28)),S(38));x+=button.Width+S(8);}}
        public void SetNotice(int index,bool visible){string text=TabPages[index].Text+(visible?" •":"");if(buttons[index].Text==text)return;buttons[index].Text=text;LayoutButtons();}
        public void AddPages(Panel[] pages){foreach(var page in pages){int index=TabPages.Count;TabPages.Add(page);page.Dock=DockStyle.Fill;page.BackColor=BackColor;content.Controls.Add(page);var button=Theme.Button(page.Text);button.Click+=(s,e)=>SelectedIndex=index;buttons.Add(button);navigation.Controls.Add(button);}LayoutButtons();SelectedIndex=0;}
    }
}
