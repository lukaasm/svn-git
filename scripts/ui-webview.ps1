# Browser automation for the private test app's WebView2. Never attaches to the installed app.
function New-TestWebViewPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function Connect-TestWebView([int]$Port) {
    $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -TimeoutSec 2
    $target = $targets | Where-Object { $_.url -like 'https://sg.app/monaco.html*' } | Select-Object -First 1
    if (!$target) { return $null }
    $client = [Net.WebSockets.ClientWebSocket]::new()
    $timeout = [Threading.CancellationTokenSource]::new(10000)
    try { $null = $client.ConnectAsync([Uri]$target.webSocketDebuggerUrl, $timeout.Token).GetAwaiter().GetResult() }
    catch { $client.Dispose(); throw }
    finally { $timeout.Dispose() }
    return $client
}
function Invoke-TestWebViewProtocol($Client, [string]$Method, [hashtable]$Parameters) {
    $timeout = [Threading.CancellationTokenSource]::new(10000)
    $stream = [IO.MemoryStream]::new()
    try {
        $payload = @{ id = 1; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 8 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $null = $Client.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(65536)
        do {
            $stream.SetLength(0)
            do {
                $received = $Client.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
                if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Test WebView disconnected.' }
                $stream.Write($buffer, 0, $received.Count)
            } while (!$received.EndOfMessage)
            $response = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
        } while ($response.id -ne 1)
        if ($response.error -or $response.result.exceptionDetails) { throw ($response | ConvertTo-Json -Depth 10) }
        return $response.result
    } finally { $timeout.Dispose(); $stream.Dispose() }
}
function Invoke-TestWebView($Client, [string]$Expression) {
    $response = Invoke-TestWebViewProtocol $Client 'Runtime.evaluate' @{ expression = $Expression; returnByValue = $true; awaitPromise = $true }
    return $response.result.value
}
function Save-TestWebView($Client, [string]$Path) {
    # PrintWindow can retain an older WebView GPU surface on a private desktop. Capture its compositor too.
    $response = Invoke-TestWebViewProtocol $Client 'Page.captureScreenshot' @{ format = 'png'; captureBeyondViewport = $false }
    [IO.File]::WriteAllBytes($Path, [Convert]::FromBase64String($response.data))
}
