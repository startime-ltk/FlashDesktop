using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FlashDesktop.Core;

/// <summary>
/// First-run collection (S2): scan the Start Menu (.lnk, both per-user and
/// all-users) plus the UWP AppsFolder, predict a category for each, and return
/// reference entries. Pure read, no system state changed, no files moved.
/// UWP enumeration is best-effort; any failure degrades to start-menu-only.
/// </summary>
public sealed class AppIndexer
{
    public const string CatOffice = "办公";
    public const string CatDev = "开发";
    public const string CatTool = "工具";
    public const string CatGame = "娱乐";
    public const string CatOther = "其他";

    public static IReadOnlyList<string> Categories =>
        new[] { CatOffice, CatDev, CatTool, CatGame, CatOther };

    public IReadOnlyList<ItemEntry> Scan()
    {
        var result = new List<ItemEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), result, seen);
        var common = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu");
        ScanStartMenu(common, result, seen);

        try { ScanAppsFolder(result, seen); }
        catch { /* best-effort UWP */ }

        return result;
    }

    // 手动递归遍历，避免单个不可访问的子目录（如「程序」junction）炸掉整个扫描。
    private static void ScanStartMenu(string root, List<ItemEntry> out_, HashSet<string> seen)
    {
        if (!Directory.Exists(root)) return;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lnk"); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var lnk in files)
            {
                string name;
                try { name = Path.GetFileNameWithoutExtension(lnk); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;

                string target = "";
                try
                {
                    var link = (IShellLinkW)Activator.CreateInstance(
                        Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
                    ((IPersistFile)link).Load(lnk, 0);
                    var sb = new StringBuilder(1024);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                    target = sb.ToString();
                }
                catch { /* unreadable shortcut -> still index by its own name */ }

                out_.Add(new ItemEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OriginalPath = lnk,
                    DisplayName = name,
                    Category = PredictCategory(lnk, target),
                    Kind = "lnk",
                    ManualOrder = out_.Count,
                    UsageCount = 0
                });
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    stack.Push(sub);
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible branch */ }
            catch (IOException) { /* skip inaccessible branch */ }
        }
    }

    private static string PredictCategory(string lnkPath, string target)
    {
        var dir = Path.GetDirectoryName(lnkPath) ?? "";
        var folder = Path.GetFileName(dir);
        var hay = (folder + " " + target + " " + Path.GetFileNameWithoutExtension(lnkPath)).ToLower();

        if (Hit(folder, new[] { "office", "wps", "adobe", "文档", "办公" }) ||
            Hit(hay, new[] { "office", "word", "excel", "powerpoint", "wps", "outlook",
                "docx", "xlsx", "pptx", "photoshop", "illustrator", "premiere", "indesign" }))
            return CatOffice;

        if (Hit(folder, new[] { "visual studio", "jetbrains", "python", "node", "java", "开发",
                "developer", "android studio", "idea", "code" }) ||
            Hit(hay, new[] { "visual studio", "jetbrains", "vscode", "python", "node.exe", "npm",
                "java", "jdk", "android studio", "gradle", "cmake", "rust", "git", "docker",
                "mysql", "sql server", "android sdk" }))
            return CatDev;

        if (Hit(folder, new[] { "game", "游戏", "steam" }) ||
            Hit(hay, new[] { "steam", "wegame", "epic games", "battlenet", "uplay", "origin" }))
            return CatGame;

        if (Hit(folder, new[] { "工具", "utility", "utilities", "accessories", "附件", "system",
                "腾讯软件", "系统工具" }))
            return CatTool;

        return CatOther;
    }

    private static bool Hit(string text, string[] keys) =>
        Array.Exists(keys, k => text.Contains(k, StringComparison.Ordinal));

    // ---- UWP AppsFolder (best-effort) -------------------------------------

    private static void ScanAppsFolder(List<ItemEntry> out_, HashSet<string> seen)
    {
        var fid = new Guid("1e87508d-89c2-42f0-8a7e-645a0f2003bd"); // FOLDERID_AppsFolder
        int hr = SHGetKnownFolderItem(ref fid, 0, IntPtr.Zero, typeof(IShellItem).GUID, out IntPtr ppv);
        if (hr != 0) return;
        var item = Marshal.GetObjectForIUnknown(ppv) as IShellItem;
        if (item is null) return;

        var bhId = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d8"); // BHID_SFObject
        var iidFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        hr = item.BindToHandler(IntPtr.Zero, ref bhId, ref iidFolder, out IntPtr ppFolder);
        if (hr != 0) return;
        var folder = Marshal.GetObjectForIUnknown(ppFolder) as IShellFolder;
        if (folder is null) return;

        hr = folder.EnumObjects(IntPtr.Zero, 0x40 | 0x80, out IntPtr enumPtr); // NONFOLDERS | INCLUDEHIDDEN
        if (hr != 0) return;
        var en = Marshal.GetObjectForIUnknown(enumPtr) as IEnumIDList;
        if (en is null) return;

        while (en.Next(1, out IntPtr pidl, out uint fetched) == 0 && fetched == 1)
        {
            try
            {
                if (SHGetNameFromIDList(pidl, 0, out IntPtr namePtr) == 0 && namePtr != IntPtr.Zero)
                {
                    var disp = Marshal.PtrToStringUni(namePtr);
                    CoTaskMemFree(namePtr);
                    if (!string.IsNullOrWhiteSpace(disp) && seen.Add(disp))
                    {
                        string aumid = "";
                        if (SHGetNameFromIDList(pidl, unchecked((int)0x80028000), out IntPtr parsePtr) == 0 && parsePtr != IntPtr.Zero)
                        {
                            var parse = Marshal.PtrToStringUni(parsePtr);
                            CoTaskMemFree(parsePtr);
                            if (!string.IsNullOrWhiteSpace(parse) &&
                                parse.Contains("AppsFolder", StringComparison.OrdinalIgnoreCase))
                            {
                                int i = parse.IndexOf("AppsFolder\\", StringComparison.OrdinalIgnoreCase);
                                aumid = parse.Substring(i + "AppsFolder\\".Length);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(aumid))
                        {
                            out_.Add(new ItemEntry
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                OriginalPath = aumid,
                                DisplayName = disp,
                                Category = PredictCategory(disp, ""),
                                Kind = "uwp",
                                ManualOrder = out_.Count,
                                UsageCount = 0
                            });
                        }
                    }
                }
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            }
        }
    }

    // ---- COM plumbing ------------------------------------------------------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
            IntPtr pfd, uint fFlags);
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
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save(IntPtr pszFileName, bool fRemember);
        void SaveCompleted(IntPtr pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppidl);
        [PreserveSig] int GetDisplayName(int sigdn, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttributes);
        [PreserveSig] int Compare(IntPtr psiItem, uint hint, out int piOrder);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwndOwner, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetKnownFolderItem(
        ref Guid rfid, uint dwFlags, IntPtr hToken,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
