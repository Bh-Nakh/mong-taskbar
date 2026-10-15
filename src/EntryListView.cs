using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarGroup
{
    // Details 뷰에서는 기본 InsertionMark 가 동작하지 않으므로
    // 드롭 삽입 위치선을 직접 그려주는 ListView.
    public class EntryListView : ListView
    {
        private const int WM_PAINT = 0x000F;
        private int _dropLineIndex = -1;

        public EntryListView()
        {
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        /// <summary>삽입될 위치의 항목 인덱스. Items.Count 이면 맨 끝, -1 이면 표시 안 함.</summary>
        public int DropLineIndex
        {
            get { return _dropLineIndex; }
            set
            {
                if (_dropLineIndex == value) return;
                _dropLineIndex = value;
                this.Invalidate();
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && _dropLineIndex >= 0)
            {
                using (Graphics g = Graphics.FromHwnd(this.Handle))
                {
                    DrawDropLine(g);
                }
            }
        }

        private void DrawDropLine(Graphics g)
        {
            int y;
            if (this.Items.Count == 0)
            {
                y = 3;
            }
            else if (_dropLineIndex >= this.Items.Count)
            {
                y = this.Items[this.Items.Count - 1].Bounds.Bottom;
            }
            else
            {
                y = this.Items[_dropLineIndex].Bounds.Top;
            }

            if (y < 2) y = 2;
            if (y > this.ClientSize.Height - 3) y = this.ClientSize.Height - 3;

            int left = 2;
            int right = this.ClientSize.Width - 3;
            if (right <= left) return;

            using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 2f))
            {
                g.DrawLine(p, left, y, right, y);
                g.DrawLine(p, left, y - 4, left, y + 4);
                g.DrawLine(p, right, y - 4, right, y + 4);
            }
        }
    }
}
