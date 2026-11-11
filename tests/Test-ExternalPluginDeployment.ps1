#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$HostPublishDirectory,
    [Parameter(Mandatory)][string]$PluginPublishDirectory
)

# Run against independently published artifacts; never installs a service or changes machine settings.
$ErrorActionPreference = 'Stop'
$hostPayload = (Resolve-Path -LiteralPath $HostPublishDirectory).Path
$pluginPayload = (Resolve-Path -LiteralPath $PluginPublishDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $hostPayload 'Vestigo.Abstractions.dll'))) {
    throw 'Host publish payload is missing the public contracts.'
}
if (Test-Path -LiteralPath (Join-Path $hostPayload 'plugins')) {
    throw 'Host publish payload must not contain plugin deployments.'
}
$state = Join-Path ([IO.Path]::GetTempPath()) ('vestigo-deployment-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $state | Out-Null
$security = [Security.AccessControl.DirectorySecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
foreach ($sid in @($currentSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $state -AclObject $security
$deployment = Join-Path $state 'plugins/example'
New-Item -ItemType Directory -Path $deployment -Force | Out-Null
Get-ChildItem -LiteralPath $pluginPayload | Copy-Item -Destination $deployment -Recurse
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
@{ Vestigo = @{
    ListenAddress = '127.0.0.1'; ListenPort = $port; ApiTokens = @($token)
    Plugins = @{ Enabled = $true; TrustMode = 'AdministratorManaged' }
    Modules = @{ ExampleEcho = @{ Enabled = $true } }
} } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'appsettings.json') -Encoding utf8NoBOM

$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $hostPayload 'Vestigo.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['VESTIGO_SETTINGS_DIR'] = $state
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    $base = "http://127.0.0.1:$port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($process.HasExited) { throw "Published host exited: $($stderr.GetAwaiter().GetResult())" }
        try {
            $health = Invoke-WebRequest "$base/health" -ConnectionTimeoutSeconds 1 -OperationTimeoutSeconds 1 -SkipHttpErrorCheck
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Published host did not become healthy.' }
    $anonymous = Invoke-WebRequest "$base/plugins/example-plugin/status" -ConnectionTimeoutSeconds 5 -OperationTimeoutSeconds 10 -SkipHttpErrorCheck
    if ($anonymous.StatusCode -ne 401) { throw 'Plugin HTTP endpoint did not require authentication.' }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
    $status = Invoke-WebRequest "$base/plugins/example-plugin/status" -Headers $headers -ConnectionTimeoutSeconds 5 -OperationTimeoutSeconds 10
    if ($status.Content -ne 'ok') { throw 'Plugin status endpoint failed.' }

    function Invoke-Mcp([int]$id, [string]$method, [hashtable]$parameters) {
        $body = @{ jsonrpc = '2.0'; id = $id; method = $method; params = $parameters } | ConvertTo-Json -Depth 10 -Compress
        $response = Invoke-WebRequest "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -ConnectionTimeoutSeconds 5 -OperationTimeoutSeconds 10
        $content = $response.Content
        if ($content.StartsWith('event:') -or $content.StartsWith('data:')) {
            $content = (($content -split "`n" | Where-Object { $_.StartsWith('data:') }) -replace '^data:\s*', '') -join "`n"
        }
        $json = $content | ConvertFrom-Json
        if ($json.error) { throw "MCP $method returned an error." }
        return $json.result
    }
    $initial = Invoke-Mcp 1 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'Vestigo deployment smoke'; version = '1.0' }
    }
    $headers['MCP-Protocol-Version'] = $initial.protocolVersion
    $tools = Invoke-Mcp 2 'tools/list' @{}
    if (!($tools.tools | Where-Object name -eq 'example_plugin_echo')) { throw 'External MCP tool is missing.' }
    if (!($tools.tools | Where-Object name -eq 'list_jobs')) { throw 'Built-in MCP tools are missing.' }
    $result = Invoke-Mcp 3 'tools/call' @{ name = 'example_plugin_echo'; arguments = @{ value = 'published plugin smoke' } }
    if ($result.isError -or !($result.content | Where-Object text -eq 'published plugin smoke')) { throw 'Published echo tool failed.' }
    Write-Output 'Published win-x64 host: health, authentication, plugin HTTP, built-in tools and independent plugin echo passed.'
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
    # Retain the isolated deployment for diagnostics; it is never a machine installation.
    Write-Output "Deployment diagnostics: $state"
}
