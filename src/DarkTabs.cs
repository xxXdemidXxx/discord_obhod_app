using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ZapretDesktop {
    public sealed class DarkTabs : UserControl {
        readonly FlowLayoutPanel navigation;
        readonly Panel content;
        readonly List<Button> buttons = new List<Button>();
        public readonly List<Panel> TabPages = new List<Panel>();
        int selected = -1;
        public int SelectedIndex {
            get { return selected; }
            set {
                if (value < 0 || value >= TabPages.Count) return;
                selected = value;
                for (int i = 0; i < TabPages.Count; i++) {
                    TabPages[i].Visible = i == value;
                    buttons[i].BackColor = i == value ? Color.FromArgb(25, 31, 45) : BackColor;
                    buttons[i].ForeColor = i == value ? Color.FromArgb(116, 220, 183) : Color.FromArgb(160, 174, 195);
                }
                TabPages[value].BringToFront();
            }
        }
        public DarkTabs() {
            DoubleBuffered = true; BackColor = Color.FromArgb(16, 20, 30);
            navigation = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false, Padding = new Padding(0), BackColor = BackColor };
            content = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            Controls.Add(content); Controls.Add(navigation);
        }
        public void AddPages(Panel[] pages) {
            foreach (var page in pages) {
                int index = TabPages.Count; TabPages.Add(page); page.Dock = DockStyle.Fill; content.Controls.Add(page);
                var button = new Button { Text = page.Text, Font = new Font("Segoe UI", 10), FlatStyle = FlatStyle.Flat,
                    Size = new Size(TextRenderer.MeasureText(page.Text, new Font("Segoe UI", 10)).Width + 30, 40),
                    Margin = new Padding(0, 0, 5, 0), Cursor = Cursors.Hand };
                button.FlatAppearance.BorderSize = 0; button.Click += (s,e) => SelectedIndex = index;
                buttons.Add(button); navigation.Controls.Add(button);
            }
            SelectedIndex = 0;
        }
    }
}
