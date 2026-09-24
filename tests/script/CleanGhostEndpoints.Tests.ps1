trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# -CleanGhostEndpoints stops both audio services, removes keys, and starts the services
# again. Whatever happens in between, they must come back, and the summary may only claim
# what happened. The step reads a fake device tree under the test key (never the real
# MMDevices); RegDel, reg.exe and the service cmdlets are stubs.
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Initialize-RegDel { }
function Get-Service { [pscustomobject]@{ Status = 'Running' } }

$root = "$TestKey\ghosts"
function New-Ghost {
    New-Item -Path "$root\Render\{ghost}\Properties" -Force | Out-Null
    New-ItemProperty -Path "$root\Render\{ghost}" -Name DeviceState -Value 4 -PropertyType DWord -Force | Out-Null
    New-Item -Path "$root\Capture" -Force | Out-Null
}
New-Ghost
$fake = (Get-ShippedFunction 'Clean-GhostEndpoints').
    Replace("'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'", "'$root\Render'").
    Replace("'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'", "'$root\Capture'")
Assert (([regex]::Matches($fake, [regex]::Escape($root))).Count -eq 2) 'both device roots point at the fake tree, not the real MMDevices'
. ([scriptblock]::Create($fake))
# RegDel stub: "ok" removes nothing (so the fake ghost survives and every run ends in the
# "removed none" branch), "fail" returns an error, "throw" fails in a way nobody anticipated
Add-Type -TypeDefinition 'public static class RegDel { public static string Mode = "ok"; public static int Calls; public static string Delete(string s) { Calls++; if (Mode == "throw") throw new System.InvalidOperationException("stub: unexpected failure in RegDel"); if (Mode == "fail") return "delete-err:5"; return "OK"; } }'
function reg.exe { $global:Calls.Add('reg.exe ' + ($args -join ' ')); if ($args[0] -eq 'export') { Set-Content -LiteralPath $args[2] -Value 'x' }; $global:LASTEXITCODE = 0 }
function Run-Step([string[]]$FailStart = @()) {
    if (-not (Test-Path "$root\Render\{ghost}")) { New-Ghost }
    Reset-Log; $global:FailStart = $FailStart
    $script:threw = $null
    try { Clean-GhostEndpoints } catch { $script:threw = $_.Exception.Message }
}
function Summary { @($global:Reports | Where-Object { $_ -match 'ghost endpoint\(s\)' }) -join ' | ' }

Write-Host '  -- AudioEndpointBuilder will not start again --'
Run-Step -FailStart @('AudioEndpointBuilder')
Assert ((Count-Reports '`[FAIL`] Could not restart AudioEndpointBuilder*') -eq 1) 'the failed restart is a FAIL'
Assert ((Summary) -notmatch 'audio services restarted') 'the summary does not claim the services restarted'

Write-Host '  -- both start again --'
Run-Step
Assert ((Summary) -match 'audio services restarted') 'the normal case still says restarted'

Write-Host '  -- a removal fails in a way nobody anticipated (RegDel throws) --'
[RegDel]::Mode = 'throw'
Run-Step
Assert ($null -ne $script:threw) ('the step did stop there ({0})' -f $script:threw)
Assert (($global:Calls.IndexOf('Start-Service AudioEndpointBuilder')) -gt ($global:Calls.IndexOf('Stop-Service AudioEndpointBuilder'))) 'AudioEndpointBuilder was started again anyway'
Assert ($global:Calls -contains 'Start-Service Audiosrv') '... and Audiosrv, which was running before'

Write-Host '  -- the fallback delete fails and reg.exe writes an error line (a real process) --'
# Windows PowerShell 5.1 under $ErrorActionPreference = 'Stop' turns a REDIRECTED stderr
# line of a native command into a terminating error. A PowerShell function cannot write
# native stderr, so this uses a small compiled reg.exe, first on PATH.
Remove-Item -LiteralPath function:\reg.exe
$stubDir = New-TestScratch 'regstub'
$stubLog = Join-Path $stubDir 'calls.txt'
Add-Type -OutputType ConsoleApplication -OutputAssembly (Join-Path $stubDir 'reg.exe') -TypeDefinition @'
using System; using System.IO;
public static class RegStub {
    public static int Main(string[] a) {
        string log = Environment.GetEnvironmentVariable("AMF_REGSTUB_LOG");
        if (log != null) File.AppendAllText(log, string.Join(" ", a) + "\r\n");
        if (a.Length > 2 && a[0].ToLowerInvariant() == "export") { File.WriteAllText(a[2], "Windows Registry Editor Version 5.00\r\n"); return 0; }
        if (a.Length > 0 && a[0].ToLowerInvariant() == "delete") { Console.Error.WriteLine("ERROR: Access is denied."); return 1; }
        return 0;
    }
}
'@
$env:AMF_REGSTUB_LOG = $stubLog
$env:PATH = $stubDir + ';' + $env:PATH
$ErrorActionPreference = 'Stop'          # as in the shipped script
[RegDel]::Mode = 'fail'
Run-Step
$ErrorActionPreference = 'Continue'
$stubCalls = if (Test-Path -LiteralPath $stubLog) { @(Get-Content -LiteralPath $stubLog) } else { @() }
Assert (@($stubCalls | Where-Object { $_ -like 'delete *{ghost}* /f' }).Count -eq 1) 'the compiled stub really ran the fallback delete'
Assert ($null -eq $script:threw) ('the step was not aborted by the error line ({0})' -f $script:threw)
Assert ((Count-Reports '`[WARN`] could not remove*delete-err:5*') -eq 1) 'the removal that failed is reported as such'
Assert ($global:Calls -contains 'Start-Service AudioEndpointBuilder' -and $global:Calls -contains 'Start-Service Audiosrv') 'both services were started again'

Write-Host '  -- nowhere else can the same thing happen --'
$src = [IO.File]::ReadAllText((Get-KitFile 'Fix-AudioMixer.ps1'))
$code = @(($src -split "`r?`n") | Where-Object { $_.Trim() -notmatch '^#' } | ForEach-Object { $_ -replace '\s+#\s.*$', '' })
$redirects = @($code | Where-Object { $_ -match '2>\$null|2>&1' })
Assert ($redirects.Count -ge 1) ('found the stderr redirects in the code ({0})' -f $redirects.Count)
Assert (@($redirects | Where-Object { $_ -notmatch "ErrorActionPreference = 'Continue'" }).Count -eq 0) 'no stderr redirect on a native command runs under the Stop preference'

Complete-Test
