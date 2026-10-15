using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarGroup
{
    /// <summary>팝업에 보이는 항목 아이콘. 한 번 제대로 꺼낸 아이콘은 icons\cache 에 PNG 로 저장해 두고 계속 쓴다.
    /// Windows 아이콘 캐시가 실패 상태(빈 문서 아이콘)를 기억해도 영향을 받지 않게 하기 위함.</summary>
    public static class EntryIconCache
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
            IntPtr[] icons, uint[] ids, uint count, uint flags);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        public const int Size = 40;

        private static string CacheDir
        {
            get
            {
                string d = Path.Combine(GroupStore.IconsDir, "cache");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>항목 아이콘 (Size x Size). 못 꺼내면 null.</summary>
        public static Bitmap Get(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Target)) return null;
            string cachePath = null;
            try
            {
                cachePath = Path.Combine(CacheDir, Key(e) + ".png");
                if (File.Exists(cachePath))
                {
                    using (Image img = Image.FromFile(cachePath))
                        return new Bitmap(img);
                }
            }
            catch { }

            bool good;
            Bitmap b = Extract(e, out good);
            // 제대로 꺼낸 것만 저장 — 빈 문서 아이콘 같은 실패 결과는 다음에 다시 시도한다
            if (b != null && good && cachePath != null)
            {
                try { b.Save(cachePath, ImageFormat.Png); } catch { }
            }
            return b;
        }

        /// <summary>대상 경로 + 아이콘 위치. 파일을 다시 확인하지 않는다 —
        /// 깨졌거나 바뀌었으면 "아이콘·정보 다시 읽기" 로 저장본을 비운다.</summary>
        private static string Key(Entry e)
        {
            string raw = (e.Target ?? "") + "|" + (e.IconLocation ?? "") + "|" + Size + "|v3";
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string _genericSig;

        /// <summary>작은 크기로 줄인 픽셀 지문 — 두 아이콘 그림이 같은지 비교용.</summary>
        private static string Signature(Bitmap b)
        {
            using (Bitmap small = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(b, new Rectangle(0, 0, 16, 16));
                }
                StringBuilder sb = new StringBuilder();
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        sb.Append((char)('a' + (c.A >> 5))).Append((char)('a' + ((c.R + c.G + c.B) / 3 >> 5)));
                    }
                return sb.ToString();
            }
        }

        /// <summary>Windows 가 아이콘을 못 읽었을 때 보여주는 "빈 문서" 아이콘의 지문.</summary>
        private static string GenericSignature
        {
            get
            {
                if (_genericSig == null)
                {
                    _genericSig = "";
                    try
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "taskbargroup-blank.tbg-unknown");
                        if (!File.Exists(tmp)) File.WriteAllText(tmp, "");
                        Bitmap g = IconHelper.GetBestIconBitmap(tmp);
                        if (g != null) using (g) _genericSig = Signature(g);
                    }
                    catch { }
                }
                return _genericSig;
            }
        }

        private static bool IsGeneric(Bitmap b)
        {
            string gs = GenericSignature;
            return gs.Length > 0 && Signature(b) == gs;
        }

        private static Bitmap Extract(Entry e, out bool good)
        {
            good = false;

            // 1) 파일에서 직접 꺼낸다 — Windows 아이콘 캐시를 거치지 않으므로 캐시가 깨져도 영향 없음
            foreach (string loc in Candidates(e))
            {
                Bitmap b = ExtractFromFile(loc);
                if (b != null) { good = true; return Fit(b); }
            }

            // 2) 직접 못 읽는 항목(스토어 앱 등)은 셸 아이콘. 빈 문서 아이콘이면 실패로 본다.
            try
            {
                Bitmap b = IconHelper.GetBestIconBitmap(e.Target);
                if (b != null)
                {
                    good = !IsGeneric(b);
                    return Fit(b);   // 실패여도 일단 보여주되 저장은 안 함 (다음에 다시 시도)
                }
            }
            catch { }
            return null;
        }

        private static System.Collections.Generic.List<string> Candidates(Entry e)
        {
            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(e.IconLocation)) list.Add(e.IconLocation);
            if (e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ShellLinkInfo li = ShellLink.Read(e.Target);
                    if (!string.IsNullOrEmpty(li.IconPath)) list.Add(li.IconPath + "," + li.IconIndex);
                    if (!string.IsNullOrEmpty(li.Target)) list.Add(li.Target + ",0");
                }
                catch { }
            }
            else
            {
                list.Add(e.Target + ",0");
            }
            return list;
        }

        /// <summary>"경로,번호" 형식의 아이콘 위치에서 48px 아이콘을 직접 꺼낸다.</summary>
        private static Bitmap ExtractFromFile(string loc)
        {
            try
            {
                string path = Environment.ExpandEnvironmentVariables(IconHelper.StripIndex(loc)).Trim('"');
                int index = 0;
                int comma = loc.LastIndexOf(',');
                if (comma > 0) int.TryParse(loc.Substring(comma + 1).Trim(), out index);
                if (!File.Exists(path)) return null;
                // 바로가기 파일 자체에서 꺼내면 화살표가 붙으므로 건너뛴다 (바로가기의 아이콘/대상으로 대신 시도)
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

                IntPtr[] icons = new IntPtr[1];
                uint[] ids = new uint[1];
                uint n = PrivateExtractIcons(path, index, 48, 48, icons, ids, 1, 0);
                if (n > 0 && n != 0xFFFFFFFF && icons[0] != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon ic = Icon.FromHandle(icons[0]))
                            return ic.ToBitmap();
                    }
                    finally { DestroyIcon(icons[0]); }
                }
                Icon assoc = Icon.ExtractAssociatedIcon(path);
                if (assoc != null) using (assoc) return assoc.ToBitmap();
            }
            catch { }
            return null;
        }

        private static Bitmap Fit(Bitmap src)
        {
            Bitmap r = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(r))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, Size, Size));
            }
            src.Dispose();
            return r;
        }
    }
}
