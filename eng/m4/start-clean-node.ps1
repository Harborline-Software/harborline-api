#requires -Version 7.4
<#
.SYNOPSIS
Starts one disposable M4 node from an already approved self-contained Windows publish.
.DESCRIPTION
Does not publish, delete, narrow authorization, or automate a browser. Leaves the node running,
including on a failed proof, and reports its PID for deliberate follow-up. RunDirectory must not
exist. Secrets are SecureString/PSCredential values in Windows DPAPI-protected CLIXML, readable
only by this Windows user on this machine. Do not print the imported state or its decrypted values.
Selected-account verification manually replays cookies on loopback: API authority proof, not a
browser HTTPS/Secure-cookie proof. The later legacy session is explicitly the desktop operator.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This M4 bootstrap requires Windows DPAPI.' }

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath
if (!(Test-Path -LiteralPath $publishPath -PathType Container)) { throw 'PublishDirectory is not a directory.' }
$runPath = [IO.Path]::GetFullPath($RunDirectory)
if (Test-Path -LiteralPath $runPath) { throw 'RunDirectory already exists; refusing to reuse it.' }
if ($runPath.StartsWith($publishPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunDirectory must be separate from the published artifact.'
}
$nodeExe = Join-Path $publishPath 'Harborline.Api.LocalNodeHost.exe'
$cliExe = Join-Path $publishPath 'harborline-node.exe'
foreach ($required in @($nodeExe, $cliExe, (Join-Path $publishPath 'coreclr.dll'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw 'PublishDirectory is not a complete self-contained Windows artifact.' }
}
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw 'Requested port is occupied; refusing to inspect an existing node.'
}

$null = New-Item -ItemType Directory -Path $runPath
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = Get-Acl -LiteralPath $runPath
$acl.SetOwner($owner)
$acl.SetAccessRuleProtection($true, $false)
$rule = [Security.AccessControl.FileSystemAccessRule]::new(
    $owner, [Security.AccessControl.FileSystemRights]::FullControl,
    [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
    [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $runPath -AclObject $acl
$dataPath = Join-Path $runPath 'data'
$null = New-Item -ItemType Directory -Path $dataPath
$stdoutPath = Join-Path $runPath 'host.stdout.log'
$stderrPath = Join-Path $runPath 'host.stderr.log'
$statePath = Join-Path $runPath 'runtime-state.clixml'
$evidencePath = Join-Path $runPath 'bootstrap-evidence.json'
$baseUri = [uri]"http://127.0.0.1:$Port/"
$nodeProcess = $null
$stage = 'generate-credentials'
$cookieState = @{ Cookies = @{}; Antiforgery = $null }
$evidence = [ordered]@{
    schemaVersion = 1
    purpose = 'M4 clean-node bootstrap; not full UI acceptance'
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    status = 'preparing'
    runDirectory = $runPath
    publishDirectory = $publishPath
    baseUrl = $baseUri.AbsoluteUri.TrimEnd('/')
    processId = $null
    labels = @('INSTALLER ESTABLISHMENT', 'INSTALLATION ACCOUNT', 'DESKTOP OPERATOR WORKFLOW')
}

function Protect-Value([string]$Value) {
    ConvertTo-SecureString -String $Value -AsPlainText -Force
}

function Save-Evidence {
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8
}

function Assert-NodeProcess {
    $nodeProcess.Refresh()
    if ($nodeProcess.HasExited) { throw 'The node exited during bootstrap.' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    if (!$listeners.Count -or @($listeners | Where-Object OwningProcess -ne $nodeProcess.Id).Count) {
        throw 'The selected listener does not belong exclusively to this node process.'
    }
}

function Invoke-NodeRequest {
    param([string]$Path, [string]$Method = 'GET', [object]$Body = $null,
        [switch]$AccountAudience, [string]$Bearer = $null)
    if (!$Path.StartsWith('/') -or $Path.StartsWith('//')) { throw 'Only fixed node-relative paths are supported.' }
    Assert-NodeProcess
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), [uri]::new($baseUri, $Path))
    try {
        if ($AccountAudience) {
            if ($Bearer) { throw 'Selected and desktop credentials must never be combined.' }
            if ($cookieState.Cookies.Count) {
                $cookieHeader = ($cookieState.Cookies.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join '; '
                $null = $request.Headers.TryAddWithoutValidation('Cookie', $cookieHeader)
            }
            if ($cookieState.Antiforgery) {
                $null = $request.Headers.TryAddWithoutValidation('X-Harborline-Antiforgery', $cookieState.Antiforgery)
            }
        }
        if ($Bearer) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Bearer) }
        if ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $evidence.lastProbe = @{path=$Path; statusCode=[int]$response.StatusCode; audience=$(if ($AccountAudience) {'selected-account'} elseif ($Bearer) {'desktop-operator'} else {'pre-auth'})}
            if (!$response.IsSuccessStatusCode) { throw "Node request refused (HTTP $([int]$response.StatusCode), $Path)." }
            if ($AccountAudience) {
                if ($response.Headers.Contains('Set-Cookie')) {
                    foreach ($cookie in $response.Headers.GetValues('Set-Cookie')) {
                        $pair = ($cookie -split ';', 2)[0] -split '=', 2
                        if ($pair.Count -ne 2) { throw 'Malformed account cookie.' }
                        if ($pair[1]) { $cookieState.Cookies[$pair[0]] = $pair[1] }
                        else { $cookieState.Cookies.Remove($pair[0]) }
                    }
                }
                if ($response.Headers.Contains('X-Harborline-Antiforgery')) {
                    $cookieState.Antiforgery = @($response.Headers.GetValues('X-Harborline-Antiforgery'))[0]
                }
            }
            if ([string]::IsNullOrWhiteSpace($content)) { return $null }
            return ($content | ConvertFrom-Json)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

$httpHandler = [Net.Http.HttpClientHandler]::new()
$httpHandler.UseCookies = $false
$httpHandler.AllowAutoRedirect = $false
$httpHandler.UseProxy = $false
$http = [Net.Http.HttpClient]::new($httpHandler)
$http.Timeout = [TimeSpan]::FromSeconds(8)

try {
    $password = 'M4-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + '!a'
    $rootSeed = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $bootstrapToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [byte[]]$seedBytes = [Convert]::FromHexString($rootSeed)
    [byte[]]$genesisBytes = $seedBytes[0..15]
    $expectedTeam = ([guid]::new($genesisBytes)).ToString('D')
    $founder = [Management.Automation.PSCredential]::new('founder', (Protect-Value $password))
    $state = [pscustomobject]@{
        SchemaVersion = 1; RunDirectory = $runPath; PublishDirectory = $publishPath
        DataDirectory = $dataPath; BaseUrl = $baseUri.AbsoluteUri.TrimEnd('/'); Port = $Port
        ProcessId = $null; Founder = $founder; RootSeedHex = (Protect-Value $rootSeed)
        BootstrapToken = (Protect-Value $bootstrapToken); FounderPasswordHash = $null
        DesktopToken = $null; DesktopExpiresAt = $null
    }
    $state | Export-Clixml -LiteralPath $statePath -Depth 5

    $stage = 'hash-founder-password'
    $hashInfo = [Diagnostics.ProcessStartInfo]::new($nodeExe)
    $hashInfo.ArgumentList.Add('hash-web-password')
    $hashInfo.WorkingDirectory = $publishPath
    $hashInfo.UseShellExecute = $false
    $hashInfo.CreateNoWindow = $true
    $hashInfo.RedirectStandardInput = $true
    $hashInfo.RedirectStandardOutput = $true
    $hashInfo.RedirectStandardError = $true
    $hasher = [Diagnostics.Process]::Start($hashInfo)
    try {
        $hashOutput = $hasher.StandardOutput.ReadToEndAsync()
        $hashErrors = $hasher.StandardError.ReadToEndAsync()
        $hasher.StandardInput.WriteLine($password)
        $hasher.StandardInput.Close()
        if (!$hasher.WaitForExit(30000)) { $hasher.Kill(); throw 'Password hashing timed out.' }
        $hash = $hashOutput.GetAwaiter().GetResult().Trim()
        $null = $hashErrors.GetAwaiter().GetResult()
        if ($hasher.ExitCode -ne 0 -or !$hash.StartsWith('$argon2id$')) { throw 'Founder password hashing failed.' }
    }
    finally { $hasher.Dispose() }
    $state.FounderPasswordHash = Protect-Value $hash
    $state | Export-Clixml -LiteralPath $statePath -Depth 5

    $stage = 'start-node'
    $nodeEnvironment = @{
        LocalNode__RootSeedHex=$rootSeed; LocalNode__SessionToken=$bootstrapToken
        LocalNode__DataDirectory=$dataPath; LocalNode__HealthPort=[string]$Port
        LocalNode__WebClient__Enabled='true'; LocalNode__WebClient__FounderUsername='founder'
        LocalNode__WebClient__FounderPasswordHash=$hash; LocalNode__WebClient__FounderDisplayName='M4 synthetic founder'
        LocalNode__MultiTeam__Enabled='false'; LocalNode__TeamId=$expectedTeam; LocalNode__Lan__Enabled='false'
        LocalNode__NodeId=[guid]::NewGuid().ToString('D'); LocalNode__WebClient__BundleRoot=$null
        ASPNETCORE_URLS=$baseUri.AbsoluteUri.TrimEnd('/'); DOTNET_ENVIRONMENT='Production'
        ASPNETCORE_ENVIRONMENT='Production'; Logging__EventLog__LogLevel__Default='None'
    }
    $nodeProcess = Start-Process -FilePath $nodeExe -WorkingDirectory $publishPath -WindowStyle Hidden -PassThru `
        -Environment $nodeEnvironment -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $state.ProcessId = $nodeProcess.Id
    $state | Export-Clixml -LiteralPath $statePath -Depth 5
    $evidence.processId = $nodeProcess.Id
    Save-Evidence

    $stage = 'readiness'
    $readiness = [Diagnostics.Stopwatch]::StartNew()
    $healthText = $null
    while ($readiness.Elapsed.TotalSeconds -lt 120) {
        $nodeProcess.Refresh()
        if ($nodeProcess.HasExited) { throw 'Node exited before readiness.' }
        $remaining = [TimeSpan]::FromSeconds([Math]::Max(0.1, 120 - $readiness.Elapsed.TotalSeconds))
        $timeout = [Threading.CancellationTokenSource]::new($remaining)
        try {
            $healthText = $http.GetStringAsync([uri]::new($baseUri, '/health'), $timeout.Token).GetAwaiter().GetResult()
            if ($healthText.StartsWith('Healthy') -and $healthText.Contains($expectedTeam)) { break }
        }
        catch { $healthText = $null }
        finally { $timeout.Dispose() }
        if ($readiness.Elapsed.TotalSeconds -lt 119) { Start-Sleep -Milliseconds 1000 }
    }
    if (!$healthText -or !$healthText.StartsWith('Healthy') -or !$healthText.Contains($expectedTeam)) {
        throw 'Healthy seed-derived genesis was not observed within 120 seconds.'
    }
    Assert-NodeProcess
    $evidence.health = @{ healthy=$true; expectedGenesisTeamId=$expectedTeam; text=$healthText.Trim() }

    $stage = 'installer-evidence'
    $logLines = @(Get-Content -LiteralPath $stdoutPath,$stderrPath)
    $installerLines = @($logLines | Where-Object { $_ -match 'administrator\.established_by_installer' })
    $accountLines = @($logLines | Where-Object { $_ -match 'installation-bootstrap\.established|founder-membership\.attached' })
    if (!$installerLines.Count -or !($installerLines -match 'provenance=bootstrap') -or
        !($accountLines -match 'installation-bootstrap\.established') -or !($accountLines -match 'founder-membership\.attached')) {
        throw 'Fresh durable installer/account establishment evidence is incomplete.'
    }
    $evidence.installer = @{ label='INSTALLER ESTABLISHMENT'; lines=$installerLines; accountBootstrapLines=$accountLines }

    $stage = 'selected-account-proof'
    $null = Invoke-NodeRequest '/api/session/antiforgery' -AccountAudience
    $challenge = Invoke-NodeRequest '/api/session/account-challenge' 'POST' @{username='founder';password=$password} -AccountAudience
    if ($challenge.classification -ne 'single') { throw 'Fresh founder does not have exactly one candidate tenant.' }
    $selected = Invoke-NodeRequest '/api/session/select' 'POST' @{tenantId=$null} -AccountAudience
    $identity = Invoke-NodeRequest '/api/session/whoami' -AccountAudience
    if ($identity.standing -ne 'founder' -or @($identity.permissions) -notcontains 'grant:permissions' -or
        !$identity.accountId -or !$identity.partyId -or $identity.tenant.id -ne $selected.tenantId) {
        throw 'Canonical founder Administrator evidence is incomplete.'
    }
    $evidence.installationAccount = @{
        label='INSTALLATION ACCOUNT'; transport='loopback API tooling with explicit cookie replay; not browser HTTPS proof'
        classification=$challenge.classification; accountId=$identity.accountId; partyId=$identity.partyId
        tenantId=$identity.tenant.id; standing=$identity.standing; permissions=@($identity.permissions)
    }

    $stage = 'desktop-founder-login'
    $login = Invoke-NodeRequest '/api/session/login' 'POST' @{username='founder';password=$password}
    if ($login.user -ne 'local' -or [string]::IsNullOrWhiteSpace($login.token)) { throw 'Desktop founder login returned no local session.' }
    $me = Invoke-NodeRequest '/api/session/me' -Bearer $login.token
    if ($me.user -ne 'local') { throw 'Desktop whoami did not identify the local operator.' }
    $state.DesktopToken = Protect-Value $login.token
    $state.DesktopExpiresAt = $login.expiresAt
    $state | Export-Clixml -LiteralPath $statePath -Depth 5
    $evidence.desktopOperator = @{label='DESKTOP OPERATOR WORKFLOW'; user=$me.user; displayName=$me.displayName; expiresAt=$me.expiresAt}
    $evidence.status = 'passed'
    $evidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Evidence
    [pscustomobject]@{ Status='passed'; RunDirectory=$runPath; Port=$Port; ProcessId=$nodeProcess.Id; EvidencePath=$evidencePath; StatePath=$statePath; StdoutPath=$stdoutPath; StderrPath=$stderrPath }
}
catch {
    $evidence.status = 'failed'
    $evidence.failedStage = $stage
    $evidence.errorType = $_.Exception.GetType().FullName
    $evidence.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Evidence
    Write-Output ([pscustomobject]@{Status='failed'; RunDirectory=$runPath; Port=$Port; ProcessId=$evidence.processId; EvidencePath=$evidencePath; StatePath=$statePath; StdoutPath=$stdoutPath; StderrPath=$stderrPath})
    throw "M4 bootstrap failed during '$stage'. Read the sanitized evidence and node logs. Any started node was left running; no state was deleted."
}
finally {
    $http.Dispose()
    $password = $null; $rootSeed = $null; $bootstrapToken = $null; $hash = $null
    $cookieState.Cookies.Clear(); $cookieState.Antiforgery = $null
    if ($null -ne (Get-Variable nodeEnvironment -ErrorAction SilentlyContinue)) { $nodeEnvironment.Clear() }
}
