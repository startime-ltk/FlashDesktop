using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FlashDesktop.Core;

namespace FlashDesktop;

internal static class AppPaths
{
    public static readonly string BaseDir = AppContext.BaseDirectory;
    public static readonly string DataDir = Path.Combine(BaseDir, "Data");
    public static readonly string ItemsFile = Path.Combine(DataDir, "items.json");
    public static readonly string LayoutFile = Path.Combine(DataDir, "layout.json");
    public static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
    public static readonly string CountsFile = Path.Combine(DataDir, "counts.json");
    public static readonly string ErrorLog = Path.Combine(DataDir, "error.log");
    public static readonly string StartupLog = Path.Combine(DataDir, "startup.log");
    public static readonly string SelfTestFile = Path.Combine(DataDir, "selftest.json");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
    }
}

internal static class Program
{
    private const string MutexName = @"Local\FlashDesktop.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        AppPaths.EnsureDataDir();

        // Global exception guard: never show a crash dialog, never touch system state.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SafeLog(AppPaths.ErrorLog, "Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            SafeLog(AppPaths.ErrorLog, "Unobserved: " + e.Exception);
            e.SetObserved();
        };

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // A second instance must not create a second set of card windows.
            // S1: silently exit; S2 can add a "show panel" IPC message here.
            return;
        }

        var watch = Stopwatch.StartNew();
        bool selfTest = Array.Exists(args, a =>
            string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));

        try
        {
            var store = new ItemStore(AppPaths.ItemsFile);
            store.LoadFromDisk();
            var settings = Settings.Load(AppPaths.SettingsFile);
            var icons = new IconLoader(AppPaths.DataDir);

            // First run: index the Start Menu + UWP and seed the five categories.
            if (store.Empty)
            {
                var scanned = new AppIndexer().Scan();
                store.Seed(scanned);
            }

            var ctx = new CardContext(store, icons, settings, AppPaths.SettingsFile);
            var state = new DesktopState();
            var layout = LayoutConfig.Load(AppPaths.LayoutFile);
            var layer = new WindowLayer(layout, state, ctx);

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.DispatcherUnhandledException += (_, e) =>
            {
                SafeLog(AppPaths.ErrorLog, "Dispatcher: " + e.Exception);
                e.Handled = true; // never black-screen the desktop
            };

            var tray = new FlashDesktop.UI.TrayIcon(state, layer, app, ctx);
            layer.Show();

            watch.Stop();
            SafeLog(AppPaths.StartupLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} startup(ms)={watch.ElapsedMilliseconds} items={store.Items.Count} cats={ctx.Categories().Count}");

            if (selfTest)
            {
                // One-shot deferred check; NOT a timer, nothing resident.
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SelfTest.RunAndWrite(layer, tray, state, layout, ctx, watch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        SafeLog(AppPaths.ErrorLog, "SelfTest: " + ex);
                    }
                    finally
                    {
                        tray.Dispose();
                        layer.Hide();
                        app.Shutdown();
                    }
                }), DispatcherPriority.ApplicationIdle);
            }

            app.Run();
            tray.Dispose();
            layer.Hide();
        }
        catch (Exception ex)
        {
            SafeLog(AppPaths.ErrorLog, "Fatal: " + ex);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* process is exiting */ }
        }
    }

    internal static void SafeLog(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the app or the desktop.
        }
    }
}
