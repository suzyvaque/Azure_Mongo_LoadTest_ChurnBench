<#
.SYNOPSIS
  Create (or update) the read-only monitoring user `bmt_monitor` with the built-in `clusterMonitor`
  role on the `admin` database, so preflight and the post-run metric pull can call `serverStatus` /
  `connPoolStats` without the app user (which lacks clusterMonitor and fails today — see
  PreflightRunner.cs serverStatus attempt).

.DESCRIPTION
  Connects to a running, authenticated MongoDB endpoint as an admin user (default `bmt_admin`) and runs
  `createUser` for `bmt_monitor`; if the user already exists it is brought to the desired state with
  `updateUser` (role reset to exactly [clusterMonitor@admin] + password reset). Fully IDEMPOTENT — safe
  to re-run.

  Run it once per endpoint that must expose server metrics:
    * each mongos router  -> cluster-wide serverStatus / connection view
    * each shard mongod   -> per-shard serverStatus (use -DirectConnection for a replica-set member)
  On a sharded cluster, a user created via a mongos is stored on the config servers and propagated to the
  shards automatically; creating it directly on a shard mongod is only needed for standalone / direct
  per-shard access.

  Intended to run on the DB VM via `az vm run-command invoke ... --parameters` (same pattern as
  Reset-MongoPassword.ps1 / Raise-MongoMaxConn.ps1). Secrets are passed as parameters, never committed;
  the resulting bmt_monitor credential goes into the BMT_CONN_MONGO_MONITOR machine env var on the
  generator VMs.

.PARAMETER AdminUser      Existing admin user to authenticate as (needs userAdmin/root). Default bmt_admin.
.PARAMETER AdminPass      Password for -AdminUser. Required.
.PARAMETER MonitorUser    Monitoring user to create/update. Default bmt_monitor.
.PARAMETER MonitorPass    Password to set for the monitoring user. Required.
.PARAMETER Uri            mongodb:// URI of the endpoint WITHOUT credentials (host:port[/...]).
                          Default mongodb://127.0.0.1:27017.
.PARAMETER DirectConnection  Add ?directConnection=true (use when targeting one replica-set member/shard mongod).
.PARAMETER AuthDb         Auth database for -AdminUser. Default admin.

.EXAMPLE
  # On a mongos router (cluster-wide), via az vm run-command:
  .\New-MongoMonitorUser.ps1 -AdminPass '<admin-pw>' -MonitorPass '<monitor-pw>' -Uri 'mongodb://127.0.0.1:27017'

.EXAMPLE
  # Directly on a shard mongod (replica-set member):
  .\New-MongoMonitorUser.ps1 -AdminPass '<admin-pw>' -MonitorPass '<monitor-pw>' `
      -Uri 'mongodb://127.0.0.1:27018' -DirectConnection
#>
param(
    [string]$AdminUser = 'bmt_admin',
    [Parameter(Mandatory)] [string]$AdminPass,
    [string]$MonitorUser = 'bmt_monitor',
    [Parameter(Mandatory)] [string]$MonitorPass,
    [string]$Uri = 'mongodb://127.0.0.1:27017',
    [switch]$DirectConnection,
    [string]$AuthDb = 'admin'
)
$ErrorActionPreference = 'Stop'
function Log($m){ Write-Output ("[monitor-user] {0}" -f $m) }

# ---- locate mongosh (same discovery as Reset-MongoPassword.ps1) ----
$mongosh = (Get-Command mongosh.exe -ErrorAction SilentlyContinue).Source
if (-not $mongosh) {
    $mongosh = (Get-ChildItem 'C:\Program Files','C:\Users','C:\setup' -Recurse -Filter 'mongosh.exe' -Depth 6 -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $mongosh) { throw 'mongosh.exe not found' }
Log ("mongosh: $mongosh")

# ---- build a credentialed URI for the admin connection ----
# (URI-encode nothing here; callers pass simple host:port. Auth is supplied via mongosh -u/-p to keep the
#  password out of the URI string and off the process command line where possible.)
$connUri = $Uri
if ($DirectConnection) {
    $connUri += ($Uri -match '\?') ? '&directConnection=true' : '?directConnection=true'
}
Log ("target: $connUri  (auth as $AdminUser@$AuthDb, creating $MonitorUser with clusterMonitor)")

# ---- idempotent create-or-update, run on the server ----
# Role is set to EXACTLY [{role:'clusterMonitor', db:'admin'}]; password is (re)set every run.
$js = @'
var admin = db.getSiblingDB('admin');
var mu = MONITOR_USER;
var mp = MONITOR_PASS;
var role = [{ role: 'clusterMonitor', db: 'admin' }];
var exists = admin.getUser(mu) != null;
if (exists) {
  admin.updateUser(mu, { pwd: mp, roles: role });
  print('updated ' + mu + ' (roles reset to clusterMonitor@admin, password reset)');
} else {
  admin.createUser({ user: mu, pwd: mp, roles: role });
  print('created ' + mu + ' with clusterMonitor@admin');
}
// verify
var check = admin.getUser(mu);
print('verify roles=' + JSON.stringify(check ? check.roles : null));
'@

# Inject the monitor user/pass as JSON string literals so special characters are safe, without echoing them.
$js = $js.Replace('MONITOR_USER', ($MonitorUser | ConvertTo-Json)).Replace('MONITOR_PASS', ($MonitorPass | ConvertTo-Json))

$jsFile = Join-Path $env:TEMP ('_monitor-user-{0}.js' -f ([guid]::NewGuid().ToString('N')))
Set-Content -Path $jsFile -Value $js -Encoding ascii
try {
    & $mongosh $connUri --quiet `
        -u $AdminUser -p $AdminPass --authenticationDatabase $AuthDb `
        --file $jsFile 2>&1 | ForEach-Object { Write-Output ("  " + $_) }
    $code = $LASTEXITCODE
} finally {
    Remove-Item $jsFile -Force -ErrorAction SilentlyContinue
}

if ($code -ne 0) { throw "mongosh exited with code $code — monitor user not confirmed" }
Log 'DONE'
