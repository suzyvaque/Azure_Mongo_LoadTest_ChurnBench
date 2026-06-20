$ErrorActionPreference = 'Stop'
$cfg = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.cfg'

# ---- 1. Reuse or create a self-signed server cert (IP SAN 10.3.0.4) in LocalMachine\My ----
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq 'CN=vm-dbtest-hpc-1' } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Subject 'CN=vm-dbtest-hpc-1' `
        -TextExtension @('2.5.29.17={text}IPAddress=10.3.0.4&DNS=vm-dbtest-hpc-1&DNS=localhost') `
        -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -Type SSLServerAuthentication
}
$thumb = $cert.Thumbprint
Write-Host "Thumbprint: $thumb"

# Export the public cert so the client (VM1) can trust it if desired.
$caPath = 'E:\mongo\tls\mongod-ca.cer'
New-Item -ItemType Directory -Force -Path (Split-Path $caPath) | Out-Null
$certB64 = [Convert]::ToBase64String($cert.RawData)
$sb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $certB64.Length; $i += 64) {
    [void]$sb.AppendLine($certB64.Substring($i, [Math]::Min(64, $certB64.Length - $i)))
}
[System.IO.File]::WriteAllText($caPath, "-----BEGIN CERTIFICATE-----`n$($sb.ToString().TrimEnd())`n-----END CERTIFICATE-----`n")
Write-Host "CA cert exported: $caPath"

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
    mode: allowTLS
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
Write-Host 'DONE-ENABLE-TLS'
