using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskbarGroup
{
    public static class IconHelper
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

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_SYSICONINDEX = 0x4000;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        private const int SHIL_EXTRALARGE = 2;  // 48px
        private const int SHIL_JUMBO = 4;       // 256px
        private const int ILD_TRANSPARENT = 0x1;

        public static Icon GetFileIcon(string path, bool large)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                SHFILEINFO shfi = new SHFILEINFO();
                uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
                IntPtr res = SHGetFileInfo(path, 0, ref shfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    Icon ic = (Icon)Icon.FromHandle(shfi.hIcon).Clone();
                    DestroyIcon(shfi.hIcon);
                    return ic;
                }
            }
            catch { }
            return null;
        }

        /// <summary>셸이 쓰는 큰 아이콘(256 → 없으면 48)을 내용 영역만 잘라서 돌려준다.
        /// 작은 아이콘만 가진 파일은 256 캔버스 왼쪽 위에 작게 들어 있으므로 잘라내야 한다.</summary>
        public static Bitmap GetBestIconBitmap(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                SHFILEINFO shfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(path, 0, ref shfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_SYSICONINDEX);
                if (res == IntPtr.Zero) return null;

                Bitmap jumbo = CropToContent(GetFromImageList(SHIL_JUMBO, shfi.iIcon));
                if (jumbo != null && jumbo.Width >= 64) return jumbo;
                Bitmap xl = CropToContent(GetFromImageList(SHIL_EXTRALARGE, shfi.iIcon));
                if (xl == null) return jumbo;
                if (jumbo == null || xl.Width > jumbo.Width) { if (jumbo != null) jumbo.Dispose(); return xl; }
                xl.Dispose();
                return jumbo;
            }
            catch { return null; }
        }

        private static Bitmap GetFromImageList(int list, int index)
        {
            IImageList il;
            Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (SHGetImageList(list, ref iid, out il) != 0 || il == null) return null;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (il.GetIcon(index, ILD_TRANSPARENT, ref h) != 0 || h == IntPtr.Zero) return null;
                using (Icon ic = Icon.FromHandle(h))
                    return ic.ToBitmap();
            }
            finally
            {
                if (h != IntPtr.Zero) DestroyIcon(h);
                Marshal.ReleaseComObject(il);
            }
        }

        private static Bitmap CropToContent(Bitmap b)
        {
            if (b == null) return null;
            int w = b.Width, h = b.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), row, 0, w);
                    for (int x = 0; x < w; x++)
                    {
                        if (((row[x] >> 24) & 0xFF) > 12)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            finally { b.UnlockBits(d); }

            if (maxX < 0) { b.Dispose(); return null; }
            // 정사각형으로 맞춰서 잘라야 원래 비율/여백 감각이 유지된다
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            int side = Math.Max(cw, ch);
            Bitmap r = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(r))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(b, new Rectangle((side - cw) / 2, (side - ch) / 2, cw, ch),
                    new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
            }
            b.Dispose();
            return r;
        }

        public static string ReadShortcutIconLocation(string lnkPath)
        {
            try
            {
                ShellLinkInfo i = ShellLink.Read(lnkPath);
                if (string.IsNullOrEmpty(i.IconPath)) return "";
                return i.IconPath + "," + i.IconIndex;
            }
            catch { return ""; }
        }

        public static string ReadShortcutTarget(string lnkPath)
        {
            try { return ShellLink.Read(lnkPath).Target ?? ""; }
            catch { return ""; }
        }

        public static string EnsureIndex(string iconLocation)
        {
            if (string.IsNullOrEmpty(iconLocation)) return "";
            if (iconLocation.IndexOf(',') >= 0) return iconLocation;
            return iconLocation + ",0";
        }

        public static string StripIndex(string iconLocation)
        {
            if (string.IsNullOrEmpty(iconLocation)) return "";
            int i = iconLocation.LastIndexOf(',');
            if (i >= 0 && i < iconLocation.Length - 1)
            {
                int idx;
                if (int.TryParse(iconLocation.Substring(i + 1).Trim(), out idx))
                    return iconLocation.Substring(0, i);
            }
            return iconLocation;
        }

        public static string GetIconLocationForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.ToLowerInvariant().EndsWith(".lnk"))
            {
                string loc = ReadShortcutIconLocation(path);
                if (!string.IsNullOrEmpty(loc))
                {
                    string p = Environment.ExpandEnvironmentVariables(StripIndex(loc));
                    if (p.Trim().Length > 0) return EnsureIndex(loc);
                }
                string tgt = ReadShortcutTarget(path);
                if (!string.IsNullOrEmpty(tgt)) return EnsureIndex(tgt);
                return path;
            }
            return EnsureIndex(path);
        }

        public static void Launch(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Target))
                throw new ArgumentException("실행 대상이 비어 있습니다.");
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = e.Target;
            psi.UseShellExecute = true;
            bool isLnk = e.Target.ToLowerInvariant().EndsWith(".lnk");
            if (!isLnk)
            {
                if (!string.IsNullOrEmpty(e.Arguments)) psi.Arguments = e.Arguments;
                if (!string.IsNullOrEmpty(e.WorkingDirectory) && Directory.Exists(e.WorkingDirectory))
                    psi.WorkingDirectory = e.WorkingDirectory;
                else
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(e.Target);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                    }
                    catch { }
                }
            }
            Process.Start(psi);
        }

        /// <summary>작업표시줄에 고정. 이미 고정돼 있으면 아무것도 하지 않고 true.
        /// isPinned: 고정 여부 확인 함수 (고정 폴더를 보고 판단).</summary>
        public static bool TryPinToTaskbar(string lnkPath, Func<bool> isPinned)
        {
            if (isPinned()) return true;
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return false;
                dynamic shell = Activator.CreateInstance(t);
                dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
                if (folder == null) return false;
                dynamic item = folder.ParseName(Path.GetFileName(lnkPath));
                if (item == null) return false;

                // 1) 메뉴에 "작업 표시줄에 고정" 이 보이는 환경 (예전 Windows)
                dynamic verbs = item.Verbs();
                int count = verbs.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic v = verbs.Item(i);
                    string name = v.Name;
                    if (name == null) continue;
                    string n = name.Replace("&", "");
                    if (n.IndexOf("작업 표시줄에 고정", StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("pin to taskbar", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        v.DoIt();
                        if (WaitPinned(isPinned)) return true;
                        break;
                    }
                }

                // 2) Windows 10 1809 이후: 메뉴에서 고정 항목을 숨겼으므로, 고정 명령 핸들러를
                //    임시로 등록해서 그 동작을 직접 부른다 (사용자 레지스트리에만 잠깐 쓰고 지움)
                return PinViaHandler(lnkPath, isPinned);
            }
            catch { }
            return false;
        }

        // ---------- 작업표시줄 고정/해제 (Windows 내부 COM: TaskbandPin / IPinnedList3) ----------
        // Windows 10 1809 이후에는 메뉴 동작이 막혀 있어서 Firefox 등이 쓰는 방식으로 고정 목록을 직접 수정한다.
        // 문서화되지 않은 인터페이스라 Windows 업데이트로 바뀔 수 있다 — 실패하면 호출한 쪽이 수동 안내로 넘어간다.

        [ComImport, Guid("0dd79ae2-d156-45d4-9eeb-3b549769e940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPinnedList3
        {
            [PreserveSig] int M3(); [PreserveSig] int M4(); [PreserveSig] int M5(); [PreserveSig] int M6();
            [PreserveSig] int M7(); [PreserveSig] int M8(); [PreserveSig] int M9(); [PreserveSig] int M10();
            [PreserveSig] int M11(); [PreserveSig] int M12(); [PreserveSig] int M13(); [PreserveSig] int M14();
            [PreserveSig] int M15();
            [PreserveSig] int Modify(IntPtr unpin, IntPtr pin, int caller);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint attrIn, out uint attrOut);
        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);

        private static readonly Guid ClsidTaskbandPin = new Guid("90aa3a4e-1cba-4233-b8bb-535773d48449");

        /// <summary>고정 목록을 수정한다. pin 이면 lnkPath 를 고정, 아니면 lnkPath(고정 폴더 안의 사본)를 해제.</summary>
        private static bool ModifyPinned(string lnkPath, bool pin)
        {
            // 이 호출은 작업표시줄을 담당하는 explorer 프로세스 안에서만 효과가 있다 → 보조 DLL 에게 시킨다
            bool? viaExplorer = Placement.PinViaExplorer(lnkPath, pin);
            if (viaExplorer.HasValue) return viaExplorer.Value;

            bool ok = false;
            // COM 객체는 STA 스레드에서 만들고, 응답이 없을 때를 대비해 시간 제한을 둔다
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    uint attr;
                    if (SHParseDisplayName(lnkPath, IntPtr.Zero, out pidl, 0, out attr) != 0 || pidl == IntPtr.Zero) return;
                    Type type = Type.GetTypeFromCLSID(ClsidTaskbandPin);
                    object obj = Activator.CreateInstance(type);
                    IPinnedList3 list = (IPinnedList3)obj;
                    int hr = pin ? list.Modify(IntPtr.Zero, pidl, int.MaxValue) : list.Modify(pidl, IntPtr.Zero, int.MaxValue);
                    ok = hr >= 0;
                    Marshal.ReleaseComObject(obj);
                }
                catch { }
                finally { if (pidl != IntPtr.Zero) CoTaskMemFree(pidl); }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(8000)) return false;
            return ok;
        }

        private static bool PinViaHandler(string lnkPath, Func<bool> isPinned)
        {
            if (!ModifyPinned(lnkPath, true)) return false;
            return WaitPinned(isPinned);
        }

        /// <summary>고정 폴더 안의 바로가기 사본(pinnedCopyPath)을 작업표시줄에서 뺀다.</summary>
        public static bool TryUnpinFromTaskbar(string pinnedCopyPath)
        {
            try { return ModifyPinned(pinnedCopyPath, false); }
            catch { return false; }
        }

        private static bool WaitPinned(Func<bool> isPinned)
        {
            for (int i = 0; i < 20; i++)
            {
                if (isPinned()) return true;
                System.Threading.Thread.Sleep(150);
            }
            return false;
        }
    }
}
