using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarGroup
{
    /// <summary>그룹 아이콘(그룹 이름을 쓴 버튼 모양)과 앱 자체 아이콘을 .ico 로 만든다.</summary>
    public static class GroupIcon
    {
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        private const string StyleVersion = "v4-two-letters";

        /// <summary>그룹 아이콘 파일 경로를 돌려준다. 내용이 바뀌면 파일 이름(해시)이 바뀌므로
        /// Windows 아이콘 캐시에 막히지 않고 새 아이콘이 반영된다.</summary>
        public static string EnsureGroupIcon(Group g)
        {
            string dir = GroupStore.IconsDir;
            string key = BuildKey(g);
            string path = Path.Combine(dir, "group_" + g.Id + "_" + Hash(key) + ".ico");
            if (!File.Exists(path))
            {
                Bitmap custom = null;
                try
                {
                    // "그룹 아이콘 선택" 으로 직접 고른 아이콘이 있으면 그것을, 없으면 그룹 이름 버튼
                    if (!string.IsNullOrEmpty(g.IconLocation))
                        custom = LoadIconLocation(g.IconLocation);
                    List<Bitmap> frames = new List<Bitmap>();
                    foreach (int s in Sizes)
                        frames.Add(custom != null
                            ? RenderGroup(new List<Bitmap>(), custom, s)
                            : RenderNameButton(IconLetters(g.Name), ColorFor(g), s));
                    WriteIco(path, frames);
                    foreach (Bitmap f in frames) f.Dispose();
                }
                finally
                {
                    if (custom != null) custom.Dispose();
                }
            }

            // 같은 그룹의 예전 아이콘 파일 정리.
            // 관리 화면과 도우미가 동시에 갱신할 수 있으므로 방금 만든 파일은 지우지 않는다
            // (지워진 순간 탐색기가 빈 아이콘을 캐시해 버리는 것을 막기 위함)
            try
            {
                foreach (string old in Directory.GetFiles(dir, "group_" + g.Id + "_*.ico"))
                {
                    if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.Now - File.GetLastWriteTime(old) < TimeSpan.FromMinutes(10)) continue;
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
            return path;
        }

        /// <summary>설정에 없는 그룹의 아이콘 파일 삭제.</summary>
        public static void CleanupOrphans(Config cfg)
        {
            try
            {
                HashSet<string> ids = new HashSet<string>();
                foreach (Group g in cfg.Groups) ids.Add(g.Id);
                foreach (string f in Directory.GetFiles(GroupStore.IconsDir, "group_*.ico"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    string[] parts = n.Split('_');
                    if (parts.Length >= 3 && !ids.Contains(parts[1]))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        private static string BuildKey(Group g)
        {
            int rev = 0;
            try { rev = GroupStore.Load(false).IconRevision; } catch { }
            return StyleVersion + "|" + (g.Name ?? "") + "|" + (g.IconLocation ?? "") + "|" + ColorFor(g).ToArgb() + "|r" + rev;
        }

        private static string Hash(string s)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static Bitmap LoadEntryImage(Entry e)
        {
            Bitmap b = null;
            if (!string.IsNullOrEmpty(e.Target)) b = IconHelper.GetBestIconBitmap(e.Target);
            if (b == null && !string.IsNullOrEmpty(e.IconLocation)) b = LoadIconLocation(e.IconLocation);
            return b;
        }

        private static Bitmap LoadIconLocation(string loc)
        {
            string p = Environment.ExpandEnvironmentVariables(IconHelper.StripIndex(loc));
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
            return IconHelper.GetBestIconBitmap(p);
        }

        // ---------- 그리기 ----------

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Graphics Prepare(Bitmap bmp)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            return g;
        }

        private static void DrawTile(Graphics g, int s, Color top, Color bottom, Color border)
        {
            float inset = s <= 24 ? 0.5f : s * 0.02f;
            RectangleF r = new RectangleF(inset, inset, s - inset * 2 - 0.01f, s - inset * 2 - 0.01f);
            using (GraphicsPath path = RoundRect(r, s * 0.22f))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new RectangleF(0, 0, s, s), top, bottom, LinearGradientMode.Vertical))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(border, Math.Max(1f, s / 40f)))
                    g.DrawPath(pen, path);
            }
        }

        /// <summary>2x2 칸 위치. count 가 1 이면 가운데 큰 칸 하나.</summary>
        private static List<RectangleF> Cells(int s, int count)
        {
            float pad = s <= 20 ? s * 0.13f : s * 0.15f;
            float gap = Math.Max(1f, s * 0.06f);
            List<RectangleF> list = new List<RectangleF>();
            if (count == 1)
            {
                float big = s - pad * 2.4f;
                list.Add(new RectangleF((s - big) / 2, (s - big) / 2, big, big));
                return list;
            }
            float cell = (s - pad * 2 - gap) / 2;
            for (int i = 0; i < 4; i++)
            {
                float x = pad + (i % 2) * (cell + gap);
                float y = pad + (i / 2) * (cell + gap);
                list.Add(new RectangleF(x, y, cell, cell));
            }
            return list;
        }

        private static Bitmap RenderGroup(List<Bitmap> members, Bitmap custom, int s)
        {
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Prepare(bmp))
            {
                DrawTile(g, s, Color.FromArgb(245, 92, 104, 124), Color.FromArgb(245, 48, 56, 72),
                    Color.FromArgb(150, 255, 255, 255));

                if (custom != null)
                {
                    List<RectangleF> c1 = Cells(s, 1);
                    g.DrawImage(custom, c1[0]);
                    return bmp;
                }

                int n = members.Count;
                List<RectangleF> cells = Cells(s, n == 1 ? 1 : 4);
                for (int i = 0; i < cells.Count; i++)
                {
                    RectangleF c = cells[i];
                    Bitmap m = i < n ? members[i] : null;
                    if (m != null)
                    {
                        g.DrawImage(m, c);
                    }
                    else
                    {
                        // 빈 칸: 희미한 자리 표시
                        using (GraphicsPath p = RoundRect(c, c.Width * 0.25f))
                        using (SolidBrush br = new SolidBrush(Color.FromArgb(i < n ? 110 : 45, 255, 255, 255)))
                            g.FillPath(br, p);
                    }
                }
            }
            return bmp;
        }

        // 그룹마다 버튼 색을 달리해서 구분하기 쉽게 (그룹 ID 로 고정)
        private static readonly Color[] Palette =
        {
            Color.FromArgb(37, 99, 235),    // 파랑
            Color.FromArgb(5, 150, 105),    // 초록
            Color.FromArgb(217, 119, 6),    // 주황
            Color.FromArgb(124, 58, 237),   // 보라
            Color.FromArgb(219, 39, 119),   // 분홍
            Color.FromArgb(8, 145, 178),    // 청록
            Color.FromArgb(220, 38, 38),    // 빨강
            Color.FromArgb(101, 163, 13),   // 연두
        };

        /// <summary>그룹 목록 순서(0부터)에 해당하는 그룹 색.</summary>
        public static Color ColorAt(int idx)
        {
            return Palette[((idx % Palette.Length) + Palette.Length) % Palette.Length];
        }

        /// <summary>설정의 그룹 순서대로 색을 돌려 써서 이웃한 그룹끼리 색이 겹치지 않게 한다.</summary>
        private static Color ColorFor(Group g)
        {
            int idx = 0;
            try
            {
                Config cfg = GroupStore.Load(false);
                for (int i = 0; i < cfg.Groups.Count; i++)
                    if (cfg.Groups[i].Id == g.Id) { idx = i; break; }
            }
            catch { }
            return Palette[idx % Palette.Length];
        }

        private static Color Shade(Color c, float f)
        {
            Func<int, int> k = delegate (int v)
            {
                int r = f >= 0 ? (int)(v + (255 - v) * f) : (int)(v * (1 + f));
                return Math.Max(0, Math.Min(255, r));
            };
            return Color.FromArgb(255, k(c.R), k(c.G), k(c.B));
        }

        /// <summary>작은 작업표시줄 아이콘에서도 알아보도록 그룹 이름의 앞 두 글자만 쓴다 (공백은 건너뜀).</summary>
        public static string IconLetters(string name)
        {
            name = (name ?? "").Replace(" ", "");
            if (name.Length == 0) return "?";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            System.Globalization.TextElementEnumerator te = System.Globalization.StringInfo.GetTextElementEnumerator(name);
            for (int i = 0; i < 2 && te.MoveNext(); i++) sb.Append(te.GetTextElement());
            return sb.ToString().ToUpperInvariant();
        }

        /// <summary>이름을 1~3줄로 나누는 후보들 (공백이 있으면 공백 기준, 없으면 글자 수 기준).</summary>
        private static List<string[]> LineLayouts(string name)
        {
            List<string[]> list = new List<string[]>();
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "그룹";
            list.Add(new string[] { name });
            string[] words = name.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                // 공백 기준으로 두 줄 (가장 균형 잡힌 위치)
                int best = 1, bestDiff = int.MaxValue;
                for (int i = 1; i < words.Length; i++)
                {
                    int a = string.Join(" ", words, 0, i).Length;
                    int b = string.Join(" ", words, i, words.Length - i).Length;
                    if (Math.Abs(a - b) < bestDiff) { bestDiff = Math.Abs(a - b); best = i; }
                }
                list.Add(new string[] { string.Join(" ", words, 0, best), string.Join(" ", words, best, words.Length - best) });
            }
            string compact = name.Replace(" ", "");
            if (compact.Length >= 2)
            {
                int h = (compact.Length + 1) / 2;
                list.Add(new string[] { compact.Substring(0, h), compact.Substring(h) });
            }
            if (compact.Length >= 5)
            {
                int t = (compact.Length + 2) / 3;
                int t2 = Math.Min(compact.Length, t * 2);
                list.Add(new string[] { compact.Substring(0, t), compact.Substring(t, t2 - t), compact.Substring(t2) });
            }
            return list;
        }

        /// <summary>그룹 이름을 쓴 버튼 모양 아이콘. 글자가 가장 크게 들어가는 줄 나눔을 고른다.</summary>
        private static Bitmap RenderNameButton(string name, Color baseColor, int s)
        {
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Prepare(bmp))
            {
                g.TextRenderingHint = s <= 32
                    ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                    : System.Drawing.Text.TextRenderingHint.AntiAlias;

                // 버튼 몸통: 위가 밝은 그라데이션 + 아래쪽 그림자로 눌리는 버튼처럼
                float inset = s <= 24 ? 0.5f : s * 0.03f;
                RectangleF body = new RectangleF(inset, inset, s - inset * 2 - 0.01f, s - inset * 2 - Math.Max(1f, s * 0.05f));
                float radius = s * 0.2f;
                RectangleF shadow = body;
                shadow.Offset(0, Math.Max(1f, s * 0.05f));
                using (GraphicsPath sp = RoundRect(shadow, radius))
                using (SolidBrush sb = new SolidBrush(Shade(baseColor, -0.45f)))
                    g.FillPath(sb, sp);
                using (GraphicsPath bp = RoundRect(body, radius))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(
                        new RectangleF(0, body.Top, s, body.Height + 1), Shade(baseColor, 0.25f), Shade(baseColor, -0.1f),
                        LinearGradientMode.Vertical))
                        g.FillPath(br, bp);
                    using (Pen pen = new Pen(Color.FromArgb(140, 255, 255, 255), Math.Max(1f, s / 40f)))
                        g.DrawPath(pen, bp);
                }

                // 글자: 버튼 안쪽 영역에 가장 크게 들어가는 배치
                float pad = Math.Max(1f, s * 0.07f);
                RectangleF area = new RectangleF(body.Left + pad, body.Top + pad * 0.6f,
                    body.Width - pad * 2, body.Height - pad * 1.2f);
                StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

                string[] bestLines = null;
                float bestSize = 0;
                foreach (string[] lines in LineLayouts(name))
                {
                    float lo = 3f, hi = area.Height;
                    for (int it = 0; it < 14; it++)
                    {
                        float mid = (lo + hi) / 2;
                        if (Fits(g, lines, mid, area, fmt)) lo = mid; else hi = mid;
                    }
                    if (lo > bestSize + 0.01f) { bestSize = lo; bestLines = lines; }
                }

                using (Font font = new Font("Malgun Gothic", bestSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush tb = new SolidBrush(Color.White))
                using (SolidBrush ts = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                {
                    float lineH = font.GetHeight(g) * 0.95f;
                    float total = lineH * bestLines.Length;
                    float y = area.Top + (area.Height - total) / 2;
                    float off = Math.Max(0.6f, s / 48f);
                    foreach (string line in bestLines)
                    {
                        SizeF sz = g.MeasureString(line, font, PointF.Empty, fmt);
                        float x = area.Left + (area.Width - sz.Width) / 2;
                        g.DrawString(line, font, ts, x, y + off, fmt);
                        g.DrawString(line, font, tb, x, y, fmt);
                        y += lineH;
                    }
                }
            }
            return bmp;
        }

        private static bool Fits(Graphics g, string[] lines, float px, RectangleF area, StringFormat fmt)
        {
            using (Font font = new Font("Malgun Gothic", px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float lineH = font.GetHeight(g) * 0.95f;
                if (lineH * lines.Length > area.Height) return false;
                foreach (string line in lines)
                    if (g.MeasureString(line, font, PointF.Empty, fmt).Width > area.Width) return false;
                return true;
            }
        }

        private static Bitmap RenderApp(int s)
        {
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Prepare(bmp))
            {
                DrawTile(g, s, Color.FromArgb(255, 72, 142, 255), Color.FromArgb(255, 30, 78, 216),
                    Color.FromArgb(170, 255, 255, 255));
                List<RectangleF> cells = Cells(s, 4);
                Color[] colors =
                {
                    Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(255, 255, 196, 61),
                    Color.FromArgb(255, 110, 231, 183),
                    Color.FromArgb(255, 255, 255, 255)
                };
                for (int i = 0; i < 4; i++)
                {
                    using (GraphicsPath p = RoundRect(cells[i], cells[i].Width * 0.28f))
                    using (SolidBrush br = new SolidBrush(colors[i]))
                        g.FillPath(br, p);
                }
            }
            return bmp;
        }

        public static void SaveAppIcon(string path)
        {
            List<Bitmap> frames = new List<Bitmap>();
            foreach (int s in Sizes) frames.Add(RenderApp(s));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            WriteIco(path, frames);
            foreach (Bitmap f in frames) f.Dispose();
        }

        /// <summary>우리가 만든 .ico 에서 원하는 크기의 PNG 프레임을 읽는다
        /// (System.Drawing.Icon 은 PNG 프레임을 제대로 못 읽는다).</summary>
        public static Bitmap LoadFrame(string icoPath, int size)
        {
            byte[] all = File.ReadAllBytes(icoPath);
            int count = BitConverter.ToUInt16(all, 4);
            int bestOff = -1, bestLen = 0, bestDiff = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                int w = all[e] == 0 ? 256 : all[e];
                int len = BitConverter.ToInt32(all, e + 8);
                int off = BitConverter.ToInt32(all, e + 12);
                int diff = Math.Abs(w - size);
                if (diff < bestDiff) { bestDiff = diff; bestOff = off; bestLen = len; }
            }
            if (bestOff < 0) return null;
            using (MemoryStream ms = new MemoryStream(all, bestOff, bestLen))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        /// <summary>PNG 프레임으로 된 .ico 작성 (Vista 이상에서 모든 크기 지원).</summary>
        private static void WriteIco(string path, List<Bitmap> frames)
        {
            List<byte[]> data = new List<byte[]>();
            foreach (Bitmap b in frames)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    data.Add(ms.ToArray());
                }
            }

            string tmp = path + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)frames.Count);
                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    int s = frames[i].Width;
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(data[i].Length);
                    w.Write(offset);
                    offset += data[i].Length;
                }
                foreach (byte[] d in data) w.Write(d);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
