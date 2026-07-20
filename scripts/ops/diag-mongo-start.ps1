$cfg = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.cfg'
$exe = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.exe'
$out = "$env:TEMP\mongod-test.out"
$err = "$env:TEMP\mongod-test.err"
Remove-Item $out, $err -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList @('--config', "`"$cfg`"") `
    -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
Start-Sleep -Seconds 6
if (-not $p.HasExited) {
    Write-Host "RESULT: mongod STILL RUNNING after 6s (TLS config OK) - stopping test instance"
    Stop-Process -Id $p.Id -Force
} else {
    Write-Host "RESULT: mongod EXITED early, code=$($p.ExitCode)"
}
Write-Host '---STDERR---'
Get-Content $err -ErrorAction SilentlyContinue
Write-Host '---STDOUT (tail)---'
Get-Content $out -ErrorAction SilentlyContinue -Tail 20
Write-Host '---LOG (tail, post-attempt)---'
Get-Content 'E:\mongo\log\mongod.log' -Tail 12
