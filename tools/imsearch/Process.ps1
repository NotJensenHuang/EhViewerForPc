# Shared process helpers: no shell, no console window, no foreground interaction.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Start-ImsearchProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPrefix, [hashtable]$EnvironmentVariables = @{})
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = (Get-Location).Path
    $startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    foreach ($key in $EnvironmentVariables.Keys) { $startInfo.Environment[$key] = $EnvironmentVariables[$key] }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start backend process.' }
    try { $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
    return [pscustomobject]@{ Process=$process; OutputTask=$process.StandardOutput.ReadToEndAsync(); ErrorTask=$process.StandardError.ReadToEndAsync(); LogPrefix=$LogPrefix }
}

function Complete-ImsearchProcess {
    param($Run, [int]$TimeoutSeconds = 300)
    try {
        if (-not $Run.Process.WaitForExit($TimeoutSeconds * 1000)) {
            $Run.Process.Kill($true)
            $Run.Process.WaitForExit()
            throw 'Backend process timed out.'
        }
        $code = $Run.Process.ExitCode
        $Run.OutputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Run.LogPrefix + '.stdout.log') -Encoding utf8
        $Run.ErrorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Run.LogPrefix + '.stderr.log') -Encoding utf8
        return $code
    }
    finally { $Run.Process.Dispose() }
}

function Invoke-ImsearchProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPrefix, [int]$TimeoutSeconds = 300, [hashtable]$EnvironmentVariables = @{})
    $run = Start-ImsearchProcess -Executable $Executable -Arguments $Arguments -LogPrefix $LogPrefix -EnvironmentVariables $EnvironmentVariables
    return Complete-ImsearchProcess -Run $run -TimeoutSeconds $TimeoutSeconds
}
