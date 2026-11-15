namespace EhViewerForPc;

/// <summary>
/// Parsed command line. Kept intentionally small; every mode is designed to run
/// unattended (no dialogs) except the normal library UI.
/// </summary>
public sealed class StartupOptions
{
    public static StartupOptions Current { get; set; } = new();

    public string? Root { get; private set; }
    public string? Gallery { get; private set; }
    public string? DataDirectory { get; private set; }
    public string? OutputPath { get; private set; }
    public string? ProbeFile { get; private set; }
    /// <summary>Gets an empty isolated root for the managed image-search application probe.</summary>
    public string? ImageSearchProbeRoot { get; private set; }
    /// <summary>Gets an empty isolated root for windowless bookmark compatibility verification.</summary>
    public string? BookmarkProbeRoot { get; private set; }

    /// <summary>File measured by the animation probe; no window is created.</summary>
    public string? AnimProbe { get; private set; }

    /// <summary>Optional directory the animation probe writes sample frames to.</summary>
    public string? DumpFrames { get; private set; }
    public bool SelfTest { get; private set; }

    /// <summary>Unattended single-instance activation probe; writes a JSON report.</summary>
    public string? SingleInstanceProbe { get; private set; }

    /// <summary>Second-process role for the probe: signal the running instance, then exit.</summary>
    public bool SingleInstanceProbeSignal { get; private set; }

    /// <summary>Network probe: front page → detail → page list → image URL.</summary>
    public bool OnlineProbe { get; private set; }

    /// <summary>Browser/DevTools probe: launch a headless shell, read cookies.</summary>
    public bool LoginProbe { get; private set; }
    public bool UiSmoke { get; private set; }
    public bool ReaderRegression { get; private set; }
    public bool NavigationRegression { get; private set; }
    public bool UiAudit { get; private set; }
    public bool SkipSnapshot { get; private set; }
    public bool DeepProbe { get; private set; }
    public bool Verbose { get; private set; }

    public void EnsureIsolatedDataDirectory()
    {
        if (UiSmoke && string.IsNullOrWhiteSpace(DataDirectory))
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "EhReaderSmokeData-" + Guid.NewGuid().ToString("N"));
        }

        // 激活探针会真实启动第二个进程并显示窗口，不能读写用户本机状态。
        if (SingleInstanceProbe is not null && string.IsNullOrWhiteSpace(DataDirectory))
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "EhReaderActivationProbe-" + Guid.NewGuid().ToString("N"));
        }
    }

    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--root":
                    options.Root = Next(args, ref i);
                    break;
                case "--gallery":
                    options.Gallery = Next(args, ref i);
                    break;
                case "--data-dir":
                    options.DataDirectory = Next(args, ref i);
                    break;
                case "--out":
                    options.OutputPath = Next(args, ref i);
                    break;
                case "--probe-file":
                    options.ProbeFile = Next(args, ref i);
                    break;
                case "--image-search-probe-root":
                    options.ImageSearchProbeRoot = Next(args, ref i);
                    break;
                case "--bookmark-probe-root":
                    options.BookmarkProbeRoot = Next(args, ref i);
                    break;
                case "--anim-probe":
                    options.AnimProbe = Next(args, ref i);
                    break;
                case "--dump-frames":
                    options.DumpFrames = Next(args, ref i);
                    break;
                case "--self-test":
                    options.SelfTest = true;
                    break;
                case "--single-instance-probe":
                    options.SingleInstanceProbe = Next(args, ref i);
                    break;
                case "--single-instance-probe-signal":
                    options.SingleInstanceProbeSignal = true;
                    break;
                case "--online-probe":
                    options.OnlineProbe = true;
                    break;
                case "--login-probe":
                    options.LoginProbe = true;
                    break;
                case "--reader-regression":
                    options.ReaderRegression = true;
                    options.UiSmoke = true;
                    break;
                case "--navigation-regression":
                    options.NavigationRegression = true;
                    options.UiSmoke = true;
                    break;
                case "--ui-audit":
                    options.UiAudit = true;
                    options.UiSmoke = true;
                    break;
                case "--ui-smoke":
                    options.UiSmoke = true;
                    break;
                case "--skip-snapshot":
                    options.SkipSnapshot = true;
                    break;
                case "--deep-probe":
                    options.DeepProbe = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
            }
        }

        return options;
    }

    private static string? Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) return null;
        index++;
        return args[index];
    }
}
