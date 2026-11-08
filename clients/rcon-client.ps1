# Minimal Source-RCON client for ScumRcon.
#
#   .\rcon-client.ps1                      interactive: asks for the password, then a prompt
#   .\rcon-client.ps1 -Command ListPlayers one command, then exits
#
# The password is typed at a hidden prompt. It is never put in a file or on the command line.
# (For automated tests only, it can be taken from the environment variable RCON_PASSWORD.)

param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 28015,
    [string]$User = 'admin',
    [string]$Command = ''
)

$ErrorActionPreference = 'Stop'

function New-Packet([int]$Id, [int]$Type, [string]$Body) {
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($Body)
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([int32](10 + $bodyBytes.Length))
    $bw.Write([int32]$Id)
    $bw.Write([int32]$Type)
    $bw.Write($bodyBytes)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Flush()
    , $ms.ToArray()
}

function Read-Exact($Stream, [int]$Count) {
    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $n = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($n -le 0) { throw 'The server closed the connection.' }
        $offset += $n
    }
    , $buffer
}

function Read-Packet($Stream) {
    $size = [BitConverter]::ToInt32((Read-Exact $Stream 4), 0)
    if ($size -lt 10 -or $size -gt 4096) { throw "Unexpected packet size $size." }
    $payload = Read-Exact $Stream $size
    [pscustomobject]@{
        Id   = [BitConverter]::ToInt32($payload, 0)
        Type = [BitConverter]::ToInt32($payload, 4)
        Body = [Text.Encoding]::UTF8.GetString($payload, 8, $size - 10)
    }
}

$plain = $env:RCON_PASSWORD
if (-not $plain) {
    $secure = Read-Host "Password for '$User'" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$client = New-Object Net.Sockets.TcpClient
try {
    $client.Connect($HostName, $Port)
    $stream = $client.GetStream()
    $stream.ReadTimeout = 30000

    # Login: the password field is "user:password"
    $login = New-Packet 1 3 "${User}:${plain}"
    $stream.Write($login, 0, $login.Length)
    $plain = $null

    $ok = $false
    while ($true) {
        $p = Read-Packet $stream
        if ($p.Type -eq 2) { $ok = ($p.Id -ne -1); break }
    }
    if (-not $ok) { Write-Host 'Login failed (wrong user or password, or this address is blocked).' -ForegroundColor Red; exit 1 }
    Write-Host "Logged in as $User." -ForegroundColor Green

    $nextId = 10
    function Send-Command([string]$Text) {
        $script:nextId++
        $pkt = New-Packet $script:nextId 2 $Text
        $stream.Write($pkt, 0, $pkt.Length)
        $stream.ReadTimeout = 30000
        $first = Read-Packet $stream
        $text = $first.Body
        $stream.ReadTimeout = 400          # long answers arrive as several packets
        try { while ($true) { $text += (Read-Packet $stream).Body } } catch [System.IO.IOException] { }
        $stream.ReadTimeout = 30000
        $text
    }

    if ($Command) {
        Send-Command $Command
    }
    else {
        while ($true) {
            $line = Read-Host 'rcon'
            if (-not $line -or $line -in 'quit', 'exit') { break }
            Send-Command $line
        }
    }
}
finally {
    $client.Close()
}
