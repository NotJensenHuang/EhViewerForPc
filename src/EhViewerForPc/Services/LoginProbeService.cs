using System.Diagnostics;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Services;

/// <summary>
/// Diagnostic for the browser login path: launches a headless Chromium with a
/// throwaway profile, connects over the DevTools protocol and reads the cookie
/// jar. It proves the plumbing (browser discovery, debug port, CDP, cookie
/// parsing) without needing an account; the actual login is manual by design.
/// </summary>
public static class LoginProbeService
{
    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs)
    {
        var result = new Dictionary<string, object?>(pairs.Length);
        foreach ((string key, object? value) in pairs) result[key] = value;
        return result;
    }

    public static async Task<int> RunAsync(AppServices services, string? outputPath)
    {
        string path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(AppContext.BaseDirectory, "login-probe.json")
            : outputPath;

        var report = new Dictionary<string, object?>
        {
            ["utc"] = DateTime.UtcNow.ToString("O"),
            ["platform"] = Environment.OSVersion.ToString(),
        };

        int exitCode = 0;

        try
        {
            (string Name, string Path)? browser = BrowserLoginSession.FindBrowser();
            if (browser is null)
            {
                report["browser"] = null;
                report["error"] = "未找到 Microsoft Edge 或 Google Chrome";
                exitCode = 1;
            }
            else
            {
                report["browser"] = Obj(("name", browser.Value.Name), ("path", browser.Value.Path));

                var watch = Stopwatch.StartNew();
                using var session = await BrowserLoginSession.LaunchAsync(
                        new BrowserLoginOptions
                        {
                            Url = "https://e-hentai.org/",
                            Headless = true,
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                report["launched"] = true;
                report["browserName"] = session.BrowserName;
                report["port"] = session.Port;

                string? title = await session.TryGetTitleAsync(CancellationToken.None).ConfigureAwait(false);
                report["title"] = title;

                Dictionary<string, string> cookies = await session
                    .ReadAuthCookiesAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                report["authCookieCount"] = cookies.Count;
                report["authCookieNames"] = cookies.Keys.ToArray();

                // Simulate a finished login: inject the two cookies the forums
                // sets, then confirm the reader picks them up again. This is the
                // exact code path the login window relies on after the user
                // submits the form.
                await session.SetCookiesAsync(
                        new Dictionary<string, string>
                        {
                            ["ipb_member_id"] = "1",
                            ["ipb_pass_hash"] = "probe",
                        },
                        ".e-hentai.org",
                        CancellationToken.None)
                    .ConfigureAwait(false);

                await Task.Delay(400).ConfigureAwait(false);
                Dictionary<string, string> detected = await session
                    .ReadAuthCookiesAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                report["injectedDetected"] = detected.Count;
                report["injectedNames"] = detected.Keys.ToArray();

                // The same cookies must be rejected by the site, proving the gate
                // between "cookies captured" and "logged in" actually works.
                EhLoginResult fake = await services.Online
                    .CompleteBrowserSignInAsync(detected, CancellationToken.None)
                    .ConfigureAwait(false);
                report["fakeCookiesRejected"] = !fake.Success;
                report["fakeCookiesMessage"] = fake.Message;
                report["elapsedMs"] = watch.ElapsedMilliseconds;
            }
        }
        catch (Exception ex)
        {
            report["error"] = ex.Message;
            report["errorType"] = ex.GetType().Name;
            exitCode = 1;
        }

        await DiagnosticReport.WriteAsync(path, report).ConfigureAwait(false);
        return exitCode;
    }
}
