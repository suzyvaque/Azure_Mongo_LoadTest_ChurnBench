$ErrorActionPreference = 'Stop'
$cfg = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.cfg'

# ============================================================================================
# Enforce TLS on the mongo VM - Option A: FAIR + HONEST posture.
#   * Server: mode=requireTLS (NO plaintext accepted; every client pays the TLS handshake, so the
#     connection-churn cost is comparable to DocumentDB/Cosmos, which are TLS-only).
#   * Cert:   self-signed, but with a proper DNS SAN so clients can VALIDATE it (tls=true, no
#     tlsInsecure). The public cert is exported for distribution to the generator hosts' trust store.
#   * Clients: connect with tls=true and trust mongod-ca.cer (import into LocalMachine\Root; see
#     handoff B3). Do NOT use tlsInsecure=true anymore.
# Set BMT_MONGO_HOST to the DNS name clients use in the connection string (must match the SAN and
# what the driver validates against). Falls back to the machine name.
# ============================================================================================
$CertHostName = if ($env:BMT_MONGO_HOST) { $env:BMT_MONGO_HOST } else { [System.Net.Dns]::GetHostName() }
$CertIpSan    = if ($env:BMT_MONGO_IP)   { $env:BMT_MONGO_IP }   else { '10.3.0.4' }
Write-Host "Cert host (SAN CN/DNS): $CertHostName ; IP SAN: $CertIpSan"

# ---- 1. Reuse or create a self-signed server cert (DNS + IP SAN) in LocalMachine\My ----
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq "CN=$CertHostName" } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Subject "CN=$CertHostName" `
        -TextExtension @("2.5.29.17={text}DNS=$CertHostName&DNS=localhost&IPAddress=$CertIpSan") `
        -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -Type SSLServerAuthentication
}
$thumb = $cert.Thumbprint
Write-Host "Thumbprint: $thumb"

# Export the public cert so each client (generator host) can TRUST it -> enables tls=true WITHOUT
# tlsInsecure. Copy this file to the generators and Import-Certificate into LocalMachine\Root.
$caPath = 'E:\mongo\tls\mongod-ca.cer'
New-Item -ItemType Directory -Force -Path (Split-Path $caPath) | Out-Null
$certB64 = [Convert]::ToBase64String($cert.RawData)
$sb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $certB64.Length; $i += 64) {
    [void]$sb.AppendLine($certB64.Substring($i, [Math]::Min(64, $certB64.Length - $i)))
}
[System.IO.File]::WriteAllText($caPath, "-----BEGIN CERTIFICATE-----`n$($sb.ToString().TrimEnd())`n-----END CERTIFICATE-----`n")
Write-Host "CA cert exported: $caPath  (distribute to generator hosts' LocalMachine\Root)"

# ---- 2. Grant the MongoDB service account read access to the private key ----
$acct = (Get-CimInstance Win32_Service -Filter "Name='MongoDB'").StartName
if ([string]::IsNullOrWhiteSpace($acct) -or $acct -eq 'LocalSystem') { $acct = 'NT AUTHORITY\SYSTEM' }
Write-Host "Service account: $acct"

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$uniqueName = $rsa.Key.UniqueName
$keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$uniqueName"
if (Test-Path $keyFile) {
    $acl = Get-Acl $keyFile
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($acct, 'Read', 'Allow')))
    Set-Acl $keyFile $acl
    Write-Host "Granted private-key read to $acct at $keyFile"
} else {
    Write-Host "WARN: private key file not found: $keyFile"
}

# ---- 3. Rewrite mongod.cfg with a tls block (mode allowTLS, certificateSelector by thumbprint) ----
Copy-Item $cfg "$cfg.bak-$(Get-Date -Format yyyyMMddHHmmss)" -Force
$newCfg = @"
# mongod.conf - BMT (auth enabled, replica set rs0)
storage:
  dbPath: E:\mongo\data
systemLog:
  destination: file
  logAppend: true
  path: E:\mongo\log\mongod.log
net:
  port: 27017
  bindIp: 0.0.0.0
  maxIncomingConnections: 5000
  tls:
    mode: requireTLS
    certificateSelector: thumbprint=$thumb
    CAFile: E:\mongo\tls\mongod-ca.cer
    allowConnectionsWithoutCertificates: true
security:
  authorization: enabled
  keyFile: E:\mongo\keyfile
replication:
  replSetName: rs0
"@
[System.IO.File]::WriteAllText($cfg, $newCfg)

# ---- 4. Restart and verify ----
Restart-Service MongoDB
Start-Sleep -Seconds 6
Write-Host "MongoDB service status: $((Get-Service MongoDB).Status)"
Write-Host '--- last relevant log lines ---'
Get-Content 'E:\mongo\log\mongod.log' -Tail 50 |
    Select-String -Pattern 'TLS', 'transport', 'listening', 'waiting for connections', 'error', 'certificate' -SimpleMatch
Write-Host ''
Write-Host "Clients must now connect with tls=true and trust $caPath (import into LocalMachine\Root)."
Write-Host "Example: BMT_CONN_MONGO = mongodb://<user>:<pass>@${CertHostName}:27017/bmt_db?replicaSet=rs0&authSource=admin&tls=true"
Write-Host 'DONE-ENABLE-TLS'
