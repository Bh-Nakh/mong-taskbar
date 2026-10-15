using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TaskbarGroup
{
    public class ShellLinkInfo
    {
        public string Target = "";
        public string Arguments = "";
        public string WorkingDirectory = "";
        public string IconPath = "";
        public int IconIndex;
        public string Description = "";
        /// <summary>System.AppUserModel.ID — 작업표시줄이 버튼을 구분하는 값.</summary>
        public string AppId = "";
    }

    /// <summary>IShellLink + IPropertyStore 로 .lnk 를 읽고 쓴다
    /// (WScript.Shell 로는 AppUserModelID 를 넣을 수 없어서 직접 COM 사용).</summary>
    public static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr p;
            public IntPtr p2;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private const ushort VT_EMPTY = 0;
        private const ushort VT_LPWSTR = 31;

        private static PROPERTYKEY AppIdKey()
        {
            PROPERTYKEY k = new PROPERTYKEY();
            k.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
            k.pid = 5;
            return k;
        }

        public static ShellLinkInfo Read(string path)
        {
            object o = new CShellLink();
            try
            {
                ((IPersistFile)o).Load(path, 0);
                IShellLinkW link = (IShellLinkW)o;
                ShellLinkInfo info = new ShellLinkInfo();
                StringBuilder sb = new StringBuilder(1024);
                try { link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0); info.Target = sb.ToString(); } catch { }
                sb.Length = 0;
                try { link.GetArguments(sb, sb.Capacity); info.Arguments = sb.ToString(); } catch { }
                sb.Length = 0;
                try { link.GetWorkingDirectory(sb, sb.Capacity); info.WorkingDirectory = sb.ToString(); } catch { }
                sb.Length = 0;
                try { int idx; link.GetIconLocation(sb, sb.Capacity, out idx); info.IconPath = sb.ToString(); info.IconIndex = idx; } catch { }
                sb.Length = 0;
                try { link.GetDescription(sb, sb.Capacity); info.Description = sb.ToString(); } catch { }

                try
                {
                    IPropertyStore ps = (IPropertyStore)o;
                    PROPERTYKEY key = AppIdKey();
                    PROPVARIANT pv;
                    ps.GetValue(ref key, out pv);
                    if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero) info.AppId = Marshal.PtrToStringUni(pv.p);
                    PropVariantClear(ref pv);
                }
                catch { }
                return info;
            }
            finally
            {
                Marshal.ReleaseComObject(o);
            }
        }

        public static void Write(string path, ShellLinkInfo info)
        {
            object o = new CShellLink();
            try
            {
                IShellLinkW link = (IShellLinkW)o;
                link.SetPath(info.Target ?? "");
                link.SetArguments(info.Arguments ?? "");
                link.SetWorkingDirectory(info.WorkingDirectory ?? "");
                link.SetDescription(info.Description ?? "");
                if (!string.IsNullOrEmpty(info.IconPath)) link.SetIconLocation(info.IconPath, info.IconIndex);

                if (!string.IsNullOrEmpty(info.AppId))
                {
                    IPropertyStore ps = (IPropertyStore)o;
                    PROPERTYKEY key = AppIdKey();
                    PROPVARIANT pv = new PROPVARIANT();
                    pv.vt = VT_LPWSTR;
                    pv.p = Marshal.StringToCoTaskMemUni(info.AppId);
                    try
                    {
                        ps.SetValue(ref key, ref pv);
                        ps.Commit();
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }

                ((IPersistFile)o).Save(path, true);
            }
            finally
            {
                Marshal.ReleaseComObject(o);
            }
        }
    }
}
