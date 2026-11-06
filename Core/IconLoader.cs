using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlashDesktop.Core;

/// <summary>
/// Icon extraction + on-disk cache (S2). Extract once via IShellItemImageFactory,
/// store a downsampled PNG (<=32KB each, total <5MB, LRU eviction). After the first
/// run every load comes from disk => zero Shell calls on secondary startup.
/// Red line 2: cache is the ONLY reason we tolerate one-time extraction; it never
/// re-extracts while running.
/// </summary>
public sealed class IconLoader
{
    /// <summary>Count of real Shell extractions. Self-test asserts it stays 0 on a warm run.</summary>
    public static int ShellExtractCalls;

    private const long MaxTotalBytes = 5L * 1024 * 1024;
    private const int ExtractSize = 128;
    private const int DecodeSize = 64;

    private readonly string _cacheDir;

    public IconLoader(string dataDir)
    {
        _cacheDir = Path.Combine(dataDir, "icon-cache");
        try { Directory.CreateDirectory(_cacheDir); } catch { /* read-only: no cache */ }
    }

    public string CacheKeyFor(string sourcePath)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            long tick = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sourcePath + "|" + tick));
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            return "x" + sourcePath.GetHashCode().ToString("x");
        }
    }

    /// <summary>Load from cache if present, else extract + cache. Returns a frozen BitmapSource or null.</summary>
    public BitmapSource? LoadOrExtract(string sourcePath)
    {
        var key = CacheKeyFor(sourcePath);
        var file = Path.Combine(_cacheDir, key + ".png");

        if (File.Exists(file))
        {
            try
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // LRU touch
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(file);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = DecodeSize;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { /* fall through to extract */ }
        }

        var src = Extract(sourcePath, ExtractSize);
        if (src is not null)
        {
            try { SavePng(src, file); EvictIfNeeded(); }
            catch { /* cache write failure is non-fatal */ }
        }
        return src;
    }

    private BitmapSource? Extract(string sourcePath, int size)
    {
        ShellExtractCalls++; // real Shell call

        // For shortcuts resolve to the target so we show the real app icon,
        // not the generic .lnk overlay.
        string path = sourcePath;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ResolveLinkTarget(path);
            if (!string.IsNullOrWhiteSpace(target)) path = target;
        }

        return ExtractViaSHGetFileInfo(path);
    }

    private static string? ResolveLinkTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            Marshal.ReleaseComObject(link);
            var t = sb.ToString();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    private BitmapSource? ExtractViaSHGetFileInfo(string path)
    {
        var shfi = new SHFILEINFO();
        // SHGFI_ICON | SHGFI_LARGEICON -> large (32/48px) icon handle for the file/link target.
        IntPtr res = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private static void SavePng(BitmapSource src, string file)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private void EvictIfNeeded()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDir, "*.png");
            long total = 0;
            foreach (var f in files) total += new FileInfo(f).Length;
            if (total <= MaxTotalBytes) return;

            Array.Sort(files, (a, b) =>
                File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            foreach (var f in files)
            {
                if (total <= MaxTotalBytes - 512 * 1024) break;
                try { total -= new FileInfo(f).Length; File.Delete(f); }
                catch { /* in use: skip */ }
            }
        }
        catch { /* never block the UI over cache hygiene */ }
    }

    public long CacheBytes()
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(_cacheDir, "*.png"))
                total += new FileInfo(f).Length;
            return total;
        }
        catch { return 0; }
    }

    // ---- COM (icon extraction via SHGetFileInfo — reliable for .lnk/files/folders) ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
            IntPtr pfd, uint fFlags);
    }

    [ComImport, Guid("0000010b-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}
