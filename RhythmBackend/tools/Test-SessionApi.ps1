#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][uri]$BaseUrl
)
$ErrorActionPreference = 'Stop'
if (($BaseUrl.Scheme -ne 'https' -and -not ($BaseUrl.Scheme -eq 'http' -and $BaseUrl.IsLoopback)) -or
    $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment -or $BaseUrl.AbsolutePath -ne '/') {
    throw 'Use an HTTPS API origin, or HTTP localhost for development.'
}
$origin = $BaseUrl.GetLeftPart([System.UriPartial]::Authority)
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(150)
$sessionId = [guid]::NewGuid().ToString('N')
function New-Capability {
    [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).Replace('+', '-').Replace('/', '_')
}
$readKey = New-Capability
$uploadKey = New-Capability
$root = '/v1/sessions/' + $sessionId
$registered = $false
$checks = 0
function Request([string]$Method, [string]$Path, [int]$Expected, [string]$Key = '', $Body = $null, [hashtable]$Headers = @{}) {
    if (-not $Path.StartsWith('/') -or $Path.StartsWith('//')) { throw 'Only same-origin API-relative paths are accepted.' }
    $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $origin + $Path)
    try {
        if ($Key) { $message.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Key) }
        foreach ($name in $Headers.Keys) { $null = $message.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        if ($null -ne $Body) {
            $message.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $client.SendAsync($message).GetAwaiter().GetResult()
        try {
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $Expected) {
                $code = 'unknown'
                try { $code = ($content | ConvertFrom-Json).error.code } catch {}
                # Never echo request bodies, authorization, QR URLs or response text.
                throw "Expected HTTP $Expected; received $([int]$response.StatusCode), error code: $code"
            }
            $script:checks++
            if ($Expected -eq 200 -and $Path.EndsWith('.csv')) {
                if ($response.Content.Headers.ContentType.MediaType -ne 'text/csv' -or -not $response.Headers.CacheControl.NoStore) { throw 'CSV MIME/cache headers failed.' }
                return $content
            }
            if ($Headers.ContainsKey('Access-Control-Request-Method')) {
                if (-not $response.Headers.Contains('Access-Control-Allow-Origin') -or
                    ($response.Headers.GetValues('Access-Control-Allow-Origin') | Select-Object -First 1) -ne 'https://seaweedbrainn.github.io') { throw 'Browser preflight failed.' }
            }
            if ($Expected -eq 204) { return }
            if ($content) { return $content | ConvertFrom-Json }
        } finally { $response.Dispose() }
    } finally { $message.Dispose() }
}
try {
    $null = Request 'GET' '/health' 200
    $registration = @{contract_version=1; session_id=$sessionId; session_key=$readKey; upload_key=$uploadKey}
    $first = Request 'POST' '/v1/sessions' 201 '' $registration
    $registered = $true
    $retry = Request 'POST' '/v1/sessions' 200 '' $registration
    if ($first.created_at -ne $retry.created_at -or $first.expires_at -ne $retry.expires_at) { throw 'Registration retry changed timestamps.' }
    $empty = Request 'GET' ($root + '/runs') 200 $readKey
    if ($empty.runs.Count -ne 0) { throw 'New session was not empty.' }
    $series = "game_id,2`nsong_id,103`nchart_id,1`nbpm,120`nsample_interval_sec,1`ngame_time,percent_accuracy,avg_latency,bpm,nps,latency_spread`n0,,,120,0,`n1,100,-12,120,1,`n"
    $summary = '02,103,3,2000,120,1,1000000,-12'
    $runIds = @()
    foreach ($song in @(103, 103, 104)) {
        $runId = [guid]::NewGuid().ToString('N')
        $runIds += $runId
        $payload = @{contract_version=1; finished_at_utc=[DateTime]::UtcNow.ToString('O'); game_id=2; song_id=$song; chart_id=1; sample_interval_sec=1; autoplay=$false; latency_basis='successful_hits_only_v1'; successful_hit_count=1; summary_exceeds_format_range=$false; series_csv=$series.Replace('song_id,103', "song_id,$song"); summary_csv=$summary.Replace('02,103,', "02,$song,")}
        $path = $root + '/runs/' + $runId
        $receipt = Request 'PUT' $path 201 $uploadKey $payload
        $again = Request 'PUT' $path 200 $uploadKey $payload
        if ($receipt.sequence -ne $again.sequence -or $receipt.completed_at -ne $again.completed_at) { throw 'Upload retry changed the receipt.' }
        $changed = $payload.Clone()
        $changed.summary_csv += "`n"
        $null = Request 'PUT' $path 409 $uploadKey $changed
    }
    $page = Request 'GET' ($root + '/runs?limit=2') 200 $readKey
    if ($page.runs.Count -ne 2 -or -not $page.has_more) { throw 'Pagination failed.' }
    $last = Request 'GET' ($root + '/runs?after_sequence=' + $page.next_after_sequence + '&limit=2') 200 $readKey
    if ($last.runs.Count -ne 1 -or $last.has_more) { throw 'Final page failed.' }
    foreach ($run in @($page.runs) + @($last.runs)) {
        $receivedSeries = Request 'GET' $run.files.series 200 $readKey
        $receivedSummary = Request 'GET' $run.files.summary 200 $readKey
        if ($receivedSeries -cne $series.Replace('song_id,103', "song_id,$($run.song_id)") -or
            $receivedSummary -cne $summary.Replace('02,103,', "02,$($run.song_id),")) { throw 'Downloaded CSV bytes/text differ from the original.' }
    }
    $null = Request 'GET' ($root + '/runs') 401
    $null = Request 'GET' ($root + '/runs') 403 $uploadKey
    $null = Request 'DELETE' $root 403 $readKey
    $null = Request 'PUT' ($root + '/runs/' + $runIds[0]) 403 $readKey $payload
    $null = Request 'OPTIONS' ($root + '/runs/' + $runIds[0]) 204 '' $null @{
        Origin='https://seaweedbrainn.github.io'; 'Access-Control-Request-Method'='PUT'; 'Access-Control-Request-Headers'='authorization,content-type'
    }
    $null = Request 'DELETE' $root 204 $uploadKey
    $registered = $false
    $null = Request 'GET' ($root + '/runs') 410 $readKey
    $null = Request 'GET' $page.runs[0].files.series 410 $readKey
    $null = Request 'GET' $page.runs[0].files.summary 410 $readKey
    Write-Output "Passed $checks disposable-session HTTP checks. Three attempts, including a replay, were retrieved using one read capability. No Gemini or hardware test was performed."
} finally {
    if ($registered) {
        try { $null = Request 'DELETE' $root 204 $uploadKey } catch { Write-Warning 'Disposable-session revocation could not be confirmed; normal expiry still applies.' }
    }
    $client.Dispose()
    $handler.Dispose()
    $readKey = $null
    $uploadKey = $null
}
