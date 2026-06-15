using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Bmt.Core;
using Bmt.Core.Configuration;
using Bmt.Core.Connections;
using Bmt.Core.Errors;
using Bmt.Core.Indexing;
using Bmt.Core.Models;
using Bmt.Core.Resilience;
using Bmt.Preflight.Net;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.Preflight;

/// <summary>
/// Runs the ten mandatory preflight checks (test_instruction.md §6.3) against one <c>--target</c> and
/// returns a structured <see cref="PreflightReport"/>. Any <see cref="PreflightStatus.Fail"/> means the
/// timed run MUST NOT start. The unique-vs-non-unique ReqId-index divergence (cosmos-ru) and the
/// distinct-ReqId guarantee are recorded explicitly in the report (check 2).
/// </summary>
public sealed class PreflightRunner
{
    private readonly BmtConfig _config;
    private readonly TargetKey _target;
    private readonly bool _warmup;
    private readonly bool _verifyDistinct;
    private readonly string _connectionString;

    private IReadOnlyList<EndPoint> _targetEndpoints = Array.Empty<EndPoint>();
    private IReadOnlyList<IPAddress> _targetIps = Array.Empty<IPAddress>();
    private int _targetPort;

    public PreflightRunner(BmtConfig config, TargetKey target, bool warmup, bool verifyDistinct)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _target = target;
        _warmup = warmup;
        _verifyDistinct = verifyDistinct;
        _connectionString = TargetConnection.ResolveConnectionString(target);
    }

    public async Task<PreflightReport> RunAsync(CancellationToken ct)
    {
        var report = new PreflightReport
        {
            Target = TargetConnection.CliName(_target),
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            MaskedConnectionString = ConnectionStringMasker.Mask(_connectionString),
        };

        ConsoleLog.Info($"Preflight target={report.Target} db={BmtConstants.DatabaseName}");
        ConsoleLog.Info($"Connection: {report.MaskedConnectionString}");

        var client = AdminClientFactory.Create(_target, _connectionString);
        var db = client.GetDatabase(BmtConstants.DatabaseName);
        try
        {
            // Establish one connection up front so the cluster description (used by checks 3 & 9)
            // is populated, and to surface auth/TLS failures early.
            await PingAndResolveAsync(client, report, ct).ConfigureAwait(false);

            await Add(report, 1, "Dataset present & complete", () => CheckDatasetAsync(db, report, ct), ct).ConfigureAwait(false);
            await Add(report, 2, "ReqId indexes present", () => CheckIndexesAsync(db, report, ct), ct).ConfigureAwait(false);
            await Add(report, 3, "Network path is private", () => CheckNetworkAsync(report, ct), ct).ConfigureAwait(false);
            await Add(report, 4, "Connectivity & auth smoke test", () => CheckConnectivityAsync(ct), ct).ConfigureAwait(false);
            await Add(report, 5, "calc_output writable", () => CheckOutputWritableAsync(db, ct), ct).ConfigureAwait(false);
            await Add(report, 6, "Server/throughput config", () => CheckServerConfigAsync(db, report, ct), ct).ConfigureAwait(false);
            await Add(report, 7, "Client host headroom", () => CheckHostHeadroomAsync(report, ct), ct).ConfigureAwait(false);
            await Add(report, 8, "Clock & time sync", () => CheckClockAsync(ct), ct).ConfigureAwait(false);
            await Add(report, 9, "Clean starting state", () => CheckCleanStateAsync(ct), ct).ConfigureAwait(false);
            await Add(report, 10, "Data-cache warm-up", () => CheckWarmupAsync(db, report, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            MongoClientReleaser.Release(client);
        }

        return report;
    }

    private static async Task Add(PreflightReport report, int n, string name, Func<Task<PreflightCheckResult>> body, CancellationToken ct)
    {
        PreflightCheckResult result;
        try
        {
            result = await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = PreflightCheckResult.Fail(n, name, $"Unexpected error: {ex.Message}", BmtErrorType.Unknown);
        }

        report.Checks.Add(result);
        var line = $"check {result.Number,2} [{result.Status.ToString().ToUpperInvariant(),4}] {result.Name}: {result.Detail}";
        switch (result.Status)
        {
            case PreflightStatus.Fail:
                ConsoleLog.Error(line);
                break;
            case PreflightStatus.Warn:
                ConsoleLog.Warn(line);
                break;
            default:
                ConsoleLog.Info(line);
                break;
        }
    }

    private async Task PingAndResolveAsync(MongoClient client, PreflightReport report, CancellationToken ct)
    {
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct)
            .ConfigureAwait(false);

        _targetEndpoints = client.Cluster.Description.Servers.Select(s => s.EndPoint).ToList();
        var ips = new List<IPAddress>();
        foreach (var ep in _targetEndpoints)
        {
            _targetPort = ep switch { IPEndPoint ip => ip.Port, DnsEndPoint dns => dns.Port, _ => _targetPort };
            ips.AddRange(await IpClassifier.ResolveAsync(ep, ct).ConfigureAwait(false));
        }

        _targetIps = ips;
        report.ServerConfig.CosmosExpectedRuPerSec = _target == TargetKey.CosmosRu ? _config.Preflight.CosmosExpectedRuPerSec : null;
        report.ServerConfig.DocumentDbExpectedTier = _target == TargetKey.DocumentDb ? _config.Preflight.DocumentDbExpectedTier : null;
        report.ServerConfig.MongoExpectedMaxIncomingConnections =
            _target == TargetKey.MongoVm ? _config.Preflight.MongoExpectedMaxIncomingConnections : null;
    }

    // 1. Dataset present & complete (exactly 100,000 docs, queryable by ReqId).
    private async Task<PreflightCheckResult> CheckDatasetAsync(IMongoDatabase db, PreflightReport report, CancellationToken ct)
    {
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        var count = await input.CountDocumentsAsync(FilterDefinition<CalcInputDoc>.Empty, cancellationToken: ct).ConfigureAwait(false);
        report.InputDocumentCount = count;

        if (count == 0)
        {
            return PreflightCheckResult.Fail(1, "Dataset present & complete",
                $"calc_input is empty (expected {BmtConstants.RequiredInputDocCount:N0}). Run prepare-data first.",
                BmtErrorType.DataSetMissing);
        }

        if (count != BmtConstants.RequiredInputDocCount)
        {
            return PreflightCheckResult.Fail(1, "Dataset present & complete",
                $"calc_input has {count:N0} docs, expected exactly {BmtConstants.RequiredInputDocCount:N0}.",
                BmtErrorType.DataSetMissing);
        }

        var sample = await input.Find(FilterDefinition<CalcInputDoc>.Empty).Limit(1).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (sample is null || string.IsNullOrEmpty(sample.ReqId) || string.IsNullOrEmpty(sample.Input))
        {
            return PreflightCheckResult.Fail(1, "Dataset present & complete",
                "Sample document missing required ReqId/Input fields.", BmtErrorType.DataSetMissing);
        }

        var byReqId = await input.Find(Builders<CalcInputDoc>.Filter.Eq(x => x.ReqId, sample.ReqId)).Limit(1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (byReqId is null)
        {
            return PreflightCheckResult.Fail(1, "Dataset present & complete",
                $"Sample ReqId '{sample.ReqId}' not queryable by the ReqId field.", BmtErrorType.DataSetMissing);
        }

        return PreflightCheckResult.Pass(1, "Dataset present & complete",
            $"calc_input has exactly {count:N0} docs; sample ReqId '{sample.ReqId}' queryable.");
    }

    // 2. ReqId indexes present on both collections + record uniqueness divergence + distinct guarantee.
    private async Task<PreflightCheckResult> CheckIndexesAsync(IMongoDatabase db, PreflightReport report, CancellationToken ct)
    {
        var inputIdx = await ListIndexesAsync(db, BmtConstants.CalcInputCollection, ct).ConfigureAwait(false);
        var outputIdx = await ListIndexesAsync(db, BmtConstants.CalcOutputCollection, ct).ConfigureAwait(false);

        var inputPresent = ReqIdIndex.ExistsIn(inputIdx);
        var outputPresent = ReqIdIndex.ExistsIn(outputIdx);

        var policy = report.IndexPolicy;
        policy.InputIndexPresent = inputPresent;
        policy.OutputIndexPresent = outputPresent;

        if (!inputPresent || !outputPresent)
        {
            return PreflightCheckResult.Fail(2, "ReqId indexes present",
                $"ReqId index present on calc_input={inputPresent}, calc_output={outputPresent} (both required). " +
                "Without it every find/remove by ReqId is a full scan.",
                BmtErrorType.IndexMissing);
        }

        var inputUnique = ReqIdIndex.IsUniqueIn(inputIdx);
        var expectedUnique = ReqIdIndex.UniqueForTarget(_target); // cosmos-ru = false, others = true
        policy.InputIndexUnique = inputUnique;
        policy.UniquenessDivergesFromCanonical = !inputUnique; // canonical (requirement #3) = unique on calc_input

        // Distinct-ReqId guarantee: ReqId == _id by construction, and _id carries a system-enforced
        // UNIQUE index on every backend (incl. cosmos-ru). So distinct ReqId == distinct _id == count
        // even where the ReqId index itself is non-unique. Verify ReqId == _id on a sample to make the
        // guarantee active rather than assumed.
        var (sampled, mismatches) = await SampleReqIdEqualsIdAsync(db, ct).ConfigureAwait(false);
        var guarantee = mismatches == 0
            ? $"ReqId == _id verified on {sampled:N0} sampled docs; _id is system-unique on all backends, " +
              "so distinct ReqId == distinct _id == document count even where the ReqId index is non-unique."
            : $"WARNING: {mismatches:N0}/{sampled:N0} sampled docs have ReqId != _id — distinct-ReqId invariant may not hold.";
        policy.DistinctReqIdGuarantee = guarantee;

        if (_verifyDistinct)
        {
            var (distinct, total) = await CountDistinctReqIdAsync(db, ct).ConfigureAwait(false);
            policy.DistinctReqIdGuarantee += $" Full verification: {distinct:N0} distinct ReqId / {total:N0} docs.";
            if (distinct != total)
            {
                return PreflightCheckResult.Fail(2, "ReqId indexes present",
                    $"Distinct ReqId verification FAILED: {distinct:N0} distinct / {total:N0} docs (duplicates present).",
                    BmtErrorType.QueryFailure);
            }
        }

        var uniqueText = inputUnique ? "unique" : "non-unique";
        var divergeText = policy.UniquenessDivergesFromCanonical
            ? " — DIVERGES from canonical unique policy (accepted on cosmos-ru: it cannot reliably hold a unique index; " +
              "distinct ReqId still guaranteed via the system-unique _id, ReqId == _id)"
            : string.Empty;

        // A uniqueness setting that does not match this target's expected policy is a real warning
        // (e.g. a non-unique input index on mongo-vm/documentdb where unique was expected).
        if (inputUnique != expectedUnique)
        {
            return PreflightCheckResult.Warn(2, "ReqId indexes present",
                $"calc_input index is {uniqueText} but {(expectedUnique ? "unique" : "non-unique")} was expected for this target. " +
                $"{divergeText} {guarantee}");
        }

        if (mismatches != 0)
        {
            return PreflightCheckResult.Warn(2, "ReqId indexes present",
                $"Indexes present (calc_input: {uniqueText}, calc_output: non-unique){divergeText}. {guarantee}");
        }

        return PreflightCheckResult.Pass(2, "ReqId indexes present",
            $"calc_input: {uniqueText}, calc_output: non-unique{divergeText}. {guarantee}");
    }

    // 3. Network path is private (resolved endpoints are private IPs, not the public internet).
    private Task<PreflightCheckResult> CheckNetworkAsync(PreflightReport report, CancellationToken ct)
    {
        _ = ct;
        if (_targetEndpoints.Count == 0 || _targetIps.Count == 0)
        {
            return Task.FromResult(PreflightCheckResult.Warn(3, "Network path is private",
                "Could not resolve any server endpoint to verify the network path."));
        }

        var details = new StringBuilder();
        var anyPublic = false;
        foreach (var ep in _targetEndpoints)
        {
            var host = IpClassifier.HostOf(ep);
            details.Append(host).Append("=[");
            details.AppendJoin(", ", _targetIps.Select(ip => $"{ip}({(IpClassifier.IsPrivate(ip) ? "private" : "PUBLIC")})"));
            details.Append("] ");
        }

        anyPublic = _targetIps.Any(ip => !IpClassifier.IsPrivate(ip));
        var detail = details.ToString().TrimEnd();

        if (!anyPublic)
        {
            return Task.FromResult(PreflightCheckResult.Pass(3, "Network path is private",
                $"All resolved endpoints are private: {detail}"));
        }

        if (_config.Preflight.RequirePrivateNetwork)
        {
            return Task.FromResult(PreflightCheckResult.Fail(3, "Network path is private",
                $"A resolved endpoint is on a PUBLIC address: {detail}. Use a Private Endpoint / VNet-peered path.",
                BmtErrorType.ConnectionFailure));
        }

        return Task.FromResult(PreflightCheckResult.Warn(3, "Network path is private",
            $"Public endpoint detected but RequirePrivateNetwork=false: {detail}"));
    }

    // 4. Connectivity & auth smoke test over the REAL per-Task connection path.
    private async Task<PreflightCheckResult> CheckConnectivityAsync(CancellationToken ct)
    {
        try
        {
            var factory = new TaskConnectionFactory(_target, _connectionString);
            using var conn = factory.Create();
            var doc = await conn.CalcInput.Find(FilterDefinition<CalcInputDoc>.Empty).Limit(1).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var reqId = doc?.ReqId ?? "__preflight_no_doc__";
            await conn.CalcInput.Find(Builders<CalcInputDoc>.Filter.Eq(x => x.ReqId, reqId)).Limit(1)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            return PreflightCheckResult.Pass(4, "Connectivity & auth smoke test",
                $"Opened a fresh per-Task connection, ran find by ReqId ('{reqId}'), and closed cleanly.");
        }
        catch (Exception ex)
        {
            var type = ExceptionClassifier.Classify(ex, _target);
            return PreflightCheckResult.Fail(4, "Connectivity & auth smoke test",
                $"Connect/auth/query failed ({type}): {ex.Message}", type);
        }
    }

    // 5. calc_output writable (remove + insert + find round-trip on a throwaway ReqId, then clean up).
    private async Task<PreflightCheckResult> CheckOutputWritableAsync(IMongoDatabase db, CancellationToken ct)
    {
        var output = db.GetCollection<CalcOutputDoc>(BmtConstants.CalcOutputCollection);
        var probeId = $"__preflight_probe_{Guid.NewGuid():N}";
        var filter = Builders<CalcOutputDoc>.Filter.Eq(x => x.ReqId, probeId);
        try
        {
            await CosmosAware(() => output.DeleteManyAsync(filter, ct), ct).ConfigureAwait(false);
            await CosmosAware(() => output.InsertOneAsync(new CalcOutputDoc
            {
                Id = probeId,
                ReqId = probeId,
                StartTime = DateTime.UtcNow.ToString("O"),
                EndTime = DateTime.UtcNow.ToString("O"),
                Output = "preflight-probe",
                OutputFormatCd = new BsonDocument("fmt", "probe"),
            }, cancellationToken: ct), ct).ConfigureAwait(false);

            var found = await output.Find(filter).Limit(1).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                return PreflightCheckResult.Fail(5, "calc_output writable",
                    "Inserted probe document was not found by ReqId.", BmtErrorType.QueryFailure);
            }

            return PreflightCheckResult.Pass(5, "calc_output writable",
                "remove+insert+find round-trip on a throwaway ReqId succeeded.");
        }
        catch (Exception ex)
        {
            var type = ExceptionClassifier.Classify(ex, _target);
            return PreflightCheckResult.Fail(5, "calc_output writable",
                $"Write round-trip failed ({type}): {ex.Message}", type);
        }
        finally
        {
            try
            {
                await CosmosAware(() => output.DeleteManyAsync(filter, ct), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn($"check 5: probe cleanup failed for {probeId}: {ex.Message}");
            }
        }
    }

    // 6. Server/throughput config matches spec; record values for the report config summary.
    private async Task<PreflightCheckResult> CheckServerConfigAsync(IMongoDatabase db, PreflightReport report, CancellationToken ct)
    {
        var admin = db.Client.GetDatabase("admin");
        try
        {
            var build = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: ct)
                .ConfigureAwait(false);
            report.ServerConfig.ServerVersion = build.GetValue("version", BsonNull.Value).IsString
                ? build["version"].AsString
                : null;
        }
        catch (Exception ex)
        {
            return PreflightCheckResult.Warn(6, "Server/throughput config",
                $"Could not read buildInfo (server still reachable via other checks): {ex.Message}");
        }

        switch (_target)
        {
            case TargetKey.CosmosRu:
                return PreflightCheckResult.Pass(6, "Server/throughput config",
                    $"cosmos-ru v{report.ServerConfig.ServerVersion}; provisioned throughput expected at the fixed " +
                    $"{_config.Preflight.CosmosExpectedRuPerSec:N0} RU/s (§4) — the tool never changes it. " +
                    "Confirm the account RU/s in Azure before the run.");

            case TargetKey.DocumentDb:
                return PreflightCheckResult.Pass(6, "Server/throughput config",
                    $"documentdb v{report.ServerConfig.ServerVersion}; expected tier {_config.Preflight.DocumentDbExpectedTier} " +
                    "(verify in Azure; not introspectable over the wire).");

            case TargetKey.MongoVm:
                return await CheckMongoConnLimitAsync(admin, report, ct).ConfigureAwait(false);

            default:
                return PreflightCheckResult.Pass(6, "Server/throughput config", $"v{report.ServerConfig.ServerVersion}");
        }
    }

    private async Task<PreflightCheckResult> CheckMongoConnLimitAsync(IMongoDatabase admin, PreflightReport report, CancellationToken ct)
    {
        int? ceiling = null;
        try
        {
            var status = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1), cancellationToken: ct)
                .ConfigureAwait(false);
            if (status.TryGetValue("connections", out var c) && c is BsonDocument conns)
            {
                var current = conns.GetValue("current", 0).ToInt32();
                var available = conns.GetValue("available", 0).ToInt32();
                ceiling = current + available;
                report.ServerConfig.MongoLiveConnectionCeiling = ceiling;
            }
        }
        catch (Exception ex)
        {
            // serverStatus needs the clusterMonitor role; the app user typically only has readWrite on
            // bmt_db. The server is already confirmed up (buildInfo), so this is a non-blocking PASS:
            // the connection ceiling is just unavailable to record here.
            return PreflightCheckResult.Pass(6, "Server/throughput config",
                $"mongo-vm v{report.ServerConfig.ServerVersion} is up; connection ceiling not readable " +
                $"(serverStatus needs clusterMonitor): {ex.Message.Split('.', ',')[0]}." +
                (_config.Preflight.MongoExpectedMaxIncomingConnections is { } expMax
                    ? $" Expected maxIncomingConnections {expMax:N0} — verify in mongod.conf."
                    : string.Empty));
        }

        var expected = _config.Preflight.MongoExpectedMaxIncomingConnections;
        if (expected is { } exp && ceiling is { } cap && cap < exp)
        {
            return PreflightCheckResult.Warn(6, "Server/throughput config",
                $"mongo-vm connection ceiling ≈ {cap:N0} is below expected maxIncomingConnections {exp:N0}.");
        }

        return PreflightCheckResult.Pass(6, "Server/throughput config",
            $"mongo-vm v{report.ServerConfig.ServerVersion}; live connection ceiling ≈ {ceiling?.ToString("N0") ?? "?"}" +
            (expected is { } e ? $" (expected ≥ {e:N0})" : string.Empty) + ".");
    }

    // 7. Client host headroom — ephemeral ports + TcpTimedWaitDelay vs. the scenario churn targets.
    private async Task<PreflightCheckResult> CheckHostHeadroomAsync(PreflightReport report, CancellationToken ct)
    {
        var h = await HostHeadroom.ReadAsync(ct).ConfigureAwait(false);
        var capacity = h.TcpTimedWaitDelaySeconds > 0 ? (double)h.EphemeralPortCount / h.TcpTimedWaitDelaySeconds : 0;
        report.HostHeadroom = new HostHeadromSummary
        {
            EphemeralPortStart = h.EphemeralPortStart,
            EphemeralPortCount = h.EphemeralPortCount,
            TcpTimedWaitDelaySeconds = h.TcpTimedWaitDelaySeconds,
            TcpTimedWaitDelayIsDefault = h.TcpTimedWaitDelayIsDefault,
            ChurnCapacityPerSec = Math.Round(capacity, 1),
        };

        var concTarget = _config.Preflight.ConcurrentConnectionTarget;
        var churnTarget = _config.Preflight.ConnectionChurnPerSecTarget;
        var twText = h.TcpTimedWaitDelayIsDefault ? $"{h.TcpTimedWaitDelaySeconds}s (default)" : $"{h.TcpTimedWaitDelaySeconds}s";
        var baseDetail = $"ephemeral ports {h.EphemeralPortStart}..{h.EphemeralPortStart + h.EphemeralPortCount - 1} " +
                         $"(count {h.EphemeralPortCount:N0}), TIME_WAIT {twText} -> churn capacity ~{capacity:N0} conn/s.";

        if (h.EphemeralPortCount == 0)
        {
            return PreflightCheckResult.Warn(7, "Client host headroom",
                "Could not read the ephemeral port range (netsh). Verify section 7.3 TCP tuning manually.");
        }

        if (h.EphemeralPortCount < concTarget)
        {
            return PreflightCheckResult.Fail(7, "Client host headroom",
                $"{baseDetail} Port count {h.EphemeralPortCount:N0} < concurrent target {concTarget:N0} -- the host cannot " +
                "sustain the burst. Widen MaxUserPort (section 7.3).", BmtErrorType.ClientPortExhaustion);
        }

        if (capacity < churnTarget)
        {
            return PreflightCheckResult.Warn(7, "Client host headroom",
                $"{baseDetail} Capacity < churn target {churnTarget:N0} conn/s -- widen the port range or lower " +
                "TcpTimedWaitDelay (section 7.3); port-exhaustion will be reported separately if it occurs.");
        }

        return PreflightCheckResult.Pass(7, "Client host headroom",
            $"{baseDetail} Meets concurrent >= {concTarget:N0} and churn >= {churnTarget:N0} conn/s.");
    }

    // 8. Clock & time sync (NTP).
    private async Task<PreflightCheckResult> CheckClockAsync(CancellationToken ct)
    {
        var clock = await ClockStatus.ReadAsync(ct).ConfigureAwait(false);
        if (clock.Synced)
        {
            return PreflightCheckResult.Pass(8, "Clock & time sync",
                $"Clock synced (source '{clock.Source}', last sync {clock.LastSync}).");
        }

        return PreflightCheckResult.Warn(8, "Clock & time sync",
            $"Clock may be unsynced (source '{clock.Source}'). Latency/rate metrics need NTP — run w32tm /resync.");
    }

    // 9. Clean starting state — results dir writable, disk free, no leftover connections.
    private async Task<PreflightCheckResult> CheckCleanStateAsync(CancellationToken ct)
    {
        var dir = Path.GetFullPath(_config.Preflight.ResultsDirectory);
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".preflight_write_probe_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", ct).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return PreflightCheckResult.Fail(9, "Clean starting state",
                $"Results directory '{dir}' is not writable: {ex.Message}", BmtErrorType.Unknown);
        }

        var min = _config.Preflight.MinFreeDiskBytes;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
            if (drive.AvailableFreeSpace < min)
            {
                return PreflightCheckResult.Fail(9, "Clean starting state",
                    $"Only {drive.AvailableFreeSpace / (1024 * 1024):N0} MiB free on {drive.Name}; need ≥ {min / (1024 * 1024):N0} MiB.",
                    BmtErrorType.Unknown);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Warn($"check 9: could not read disk free space: {ex.Message}");
        }

        var leftover = CountLeftoverConnections();
        var detail = $"results dir '{dir}' writable; disk ok; {leftover} existing TCP connection(s) to the target.";
        if (leftover > 100)
        {
            return PreflightCheckResult.Warn(9, "Clean starting state",
                $"{detail} High leftover-connection count suggests a prior aborted run is still draining — wait for TIME_WAIT to clear.");
        }

        return PreflightCheckResult.Pass(9, "Clean starting state", detail);
    }

    // 10. Data-cache warm-up — verify (or perform with --warmup) the untimed warm pass (§6.5).
    private async Task<PreflightCheckResult> CheckWarmupAsync(IMongoDatabase db, PreflightReport report, CancellationToken ct)
    {
        var dir = Path.GetFullPath(_config.Preflight.ResultsDirectory);
        Directory.CreateDirectory(dir);
        var sentinel = Path.Combine(dir, $".warmup-{report.Target}.done");

        if (_warmup)
        {
            var swept = await WarmUpAsync(db, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(sentinel, DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);
            return PreflightCheckResult.Pass(10, "Data-cache warm-up",
                $"Warm-up sweep read {swept:N0} docs (untimed, this process torn down before the timed run); sentinel written.");
        }

        if (File.Exists(sentinel))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(sentinel);
            if (age <= TimeSpan.FromMinutes(_config.Preflight.WarmupMaxAgeMinutes))
            {
                return PreflightCheckResult.Pass(10, "Data-cache warm-up",
                    $"Warm-up sentinel present and fresh ({age.TotalMinutes:N0} min old).");
            }

            return PreflightCheckResult.Warn(10, "Data-cache warm-up",
                $"Warm-up sentinel is stale ({age.TotalMinutes:N0} min > {_config.Preflight.WarmupMaxAgeMinutes} min). Re-run with --warmup.");
        }

        return PreflightCheckResult.Warn(10, "Data-cache warm-up",
            "No warm-up performed. Re-run preflight with --warmup (or run the warm-up pass) before the timed run so " +
            "mongo-vm starts from the same warm-cache state as the managed services (§6.5).");
    }

    private async Task<long> WarmUpAsync(IMongoDatabase db, CancellationToken ct)
    {
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        var limit = _config.Preflight.SampleSize;
        var read = 0L;
        using var cursor = await input.Find(FilterDefinition<CalcInputDoc>.Empty).Limit(limit).ToCursorAsync(ct).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            read += cursor.Current.Count();
        }

        return read;
    }

    private static async Task<List<BsonDocument>> ListIndexesAsync(IMongoDatabase db, string collection, CancellationToken ct)
    {
        var coll = db.GetCollection<BsonDocument>(collection);
        using var cursor = await coll.Indexes.ListAsync(ct).ConfigureAwait(false);
        return await cursor.ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<(int sampled, int mismatches)> SampleReqIdEqualsIdAsync(IMongoDatabase db, CancellationToken ct)
    {
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        var projection = Builders<CalcInputDoc>.Projection.Include(x => x.Id).Include(x => x.ReqId);
        var docs = await input.Find(FilterDefinition<CalcInputDoc>.Empty)
            .Project<CalcInputDoc>(projection)
            .Limit(_config.Preflight.SampleSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var mismatches = docs.Count(d => !string.Equals(d.ReqId, d.Id, StringComparison.Ordinal));
        return (docs.Count, mismatches);
    }

    private async Task<(long distinct, long total)> CountDistinctReqIdAsync(IMongoDatabase db, CancellationToken ct)
    {
        var input = db.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        var total = await input.CountDocumentsAsync(FilterDefinition<CalcInputDoc>.Empty, cancellationToken: ct).ConfigureAwait(false);
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument("_id", "$ReqId")),
            new BsonDocument("$count", "distinct"),
        };
        using var cursor = await input.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct).ConfigureAwait(false);
        var result = await cursor.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var distinct = result is null ? 0L : result.GetValue("distinct", 0).ToInt64();
        return (distinct, total);
    }

    private int CountLeftoverConnections()
    {
        if (_targetIps.Count == 0 || _targetPort == 0)
        {
            return 0;
        }

        try
        {
            var targetSet = _targetIps.Select(ip => ip.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Count(c => c.RemoteEndPoint.Port == _targetPort &&
                            targetSet.Contains(c.RemoteEndPoint.Address.ToString()) &&
                            (c.State == TcpState.Established || c.State == TcpState.TimeWait));
        }
        catch
        {
            return 0;
        }
    }

    private Task CosmosAware(Func<Task> op, CancellationToken ct) =>
        _target == TargetKey.CosmosRu ? CosmosRetry.ExecuteAsync(op, cancellationToken: ct) : op();
}
