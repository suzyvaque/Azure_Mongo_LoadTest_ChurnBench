<#
.SYNOPSIS
  Reset a MongoDB user's password on a single-member replica set when the password is lost.
.DESCRIPTION
  Forgotten-password recovery: stops the service, starts a temporary standalone mongod (no auth) on
  the same dbPath bound to localhost, runs updateUser for each user, then restarts the service (auth on).
  Intended to run on the DB VM via `az vm run-command invoke ... --parameters`.
  Example: -NewPass <pw> -ServiceName MongoDB      -DbPath E:\mongo\data     (rs0 shard / mongo-vm users)
           -NewPass <pw> -ServiceName mongo-configsvr -DbPath E:\mongo\configdb -Users bmtapp,shardadmin
#>
param(
    [Parameter(Mandatory)] [string]$NewPass,
    [Parameter(Mandatory)] [string]$ServiceName,
    [Parameter(Mandatory)] [string]$DbPath,
    [string]$Users = 'bmt_bench,bmt_admin',
    [int]$TempPort = 27099
)
$ErrorActionPreference = 'Stop'
function Log($m){ Write-Output ("[reset] {0}" -f $m) }

# locate mongod + mongosh
$mongod = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.exe'
$mongosh = (Get-Command mongosh.exe -ErrorAction SilentlyContinue).Source
if (-not $mongosh) {
    $mongosh = (Get-ChildItem 'C:\Program Files','C:\Users','C:\setup' -Recurse -Filter 'mongosh.exe' -Depth 6 -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $mongosh) { throw 'mongosh.exe not found' }
Log ("mongosh: $mongosh")
Log ("mongod:  $mongod  (exists=$(Test-Path $mongod))")

Log "stopping service $ServiceName"
Stop-Service $ServiceName -Force
$svc = Get-Service $ServiceName
$svc.WaitForStatus('Stopped','00:01:00')
Start-Sleep -Seconds 3

$tempLog = "E:\mongo\log\reset-temp-$TempPort.log"
Log "starting temp standalone mongod on 127.0.0.1:$TempPort (no auth) dbPath=$DbPath"
$p = Start-Process -FilePath $mongod -PassThru -WindowStyle Hidden -ArgumentList @(
    '--dbpath', "`"$DbPath`"", '--port', "$TempPort", '--bind_ip', '127.0.0.1',
    '--logpath', "`"$tempLog`"", '--setParameter', 'disableLogicalSessionCacheRefresh=true'
)
# wait for it to accept connections
$ok = $false
for ($i=0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    $t = Test-NetConnection -ComputerName 127.0.0.1 -Port $TempPort -WarningAction SilentlyContinue
    if ($t.TcpTestSucceeded) { $ok = $true; break }
    if ($p.HasExited) { break }
}
if (-not $ok) {
    Log "temp mongod failed to open port; tail log:"
    if (Test-Path $tempLog) { Get-Content $tempLog -Tail 25 | ForEach-Object { Write-Output ("  " + $_) } }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Service $ServiceName
    throw 'temp mongod did not start'
}
Log "temp mongod up; updating users"

$js = @"
var admin = db.getSiblingDB('admin');
var existing = admin.getUsers().users.map(function(u){return u.user;});
print('existing_admin_users=' + JSON.stringify(existing));
function setpw(u){ try { admin.updateUser(u, { pwd: '$NewPass' }); print('updated ' + u); } catch(e){ print('SKIP ' + u + ': ' + e); } }
'$Users'.split(',').forEach(function(u){ setpw(u.trim()); });
"@
$jsFile = "E:\mongo\log\_reset.js"
Set-Content -Path $jsFile -Value $js -Encoding ascii
& $mongosh "mongodb://127.0.0.1:$TempPort/admin" --quiet --file $jsFile 2>&1 | ForEach-Object { Write-Output ("  " + $_) }
Remove-Item $jsFile -Force -ErrorAction SilentlyContinue

Log "shutting down temp mongod"
try { & $mongosh "mongodb://127.0.0.1:$TempPort/admin" --quiet --eval "db.adminCommand({shutdown:1})" 2>$null } catch {}
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

Log "restarting service $ServiceName"
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running','00:01:00')
Log "DONE"
