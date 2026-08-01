using MongoDB.Bson;
using MongoDB.Driver;

var conn = Environment.GetEnvironmentVariable("BMT_CONN", EnvironmentVariableTarget.Machine)
        ?? Environment.GetEnvironmentVariable("BMT_CONN", EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable("BMT_CONN", EnvironmentVariableTarget.Process);
if (string.IsNullOrWhiteSpace(conn)) { Console.WriteLine("ERR: BMT_CONN not set"); return 1; }

var mode = args.Length > 0 ? args[0] : "status";
var settings = MongoClientSettings.FromConnectionString(conn);
settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
var client = new MongoClient(settings);
var admin = client.GetDatabase("admin");
var bmt = client.GetDatabase("bmt_db");

BsonDocument Run(IMongoDatabase db, string json) =>
    db.RunCommand<BsonDocument>(BsonDocument.Parse(json));

try
{
    switch (mode)
    {
        case "status":
        {
            var ls = Run(admin, "{listShards:1}");
            var shards = ls["shards"].AsBsonArray;
            Console.WriteLine($"shards={shards.Count}");
            foreach (var s in shards) Console.WriteLine("  " + s.ToJson());
            foreach (var coll in new[] { "calc_input", "calc_output" })
            {
                Console.WriteLine($"== collStats {coll} ==");
                var cs = Run(bmt, "{collStats:\"" + coll + "\"}");
                bool sharded = cs.Contains("sharded") && cs["sharded"].ToBoolean();
                long count = cs.Contains("count") ? cs["count"].ToInt64() : -1;
                Console.WriteLine($"  sharded={sharded} count={count}");
                if (cs.Contains("shards"))
                {
                    var sd = cs["shards"].AsBsonDocument;
                    foreach (var n in sd.Names)
                    {
                        var d = sd[n].AsBsonDocument;
                        long c = d.Contains("count") ? d["count"].ToInt64() : -1;
                        long sz = d.Contains("size") ? d["size"].ToInt64() : -1;
                        Console.WriteLine($"    shard {n}: count={c} size={sz}");
                    }
                }
                else Console.WriteLine("    (no per-shard breakdown => unsharded / single shard)");
            }
            break;
        }
        case "start-balancer":
        {
            var json = args.Length > 1
                ? "{balancerStart:1, strategy:\"" + args[1] + "\"}"
                : "{balancerStart:1}";
            Console.WriteLine("cmd: " + json);
            Console.WriteLine(Run(admin, json).ToJson()); break;
        }
        case "balancer-status":
            Console.WriteLine(Run(admin, "{balancerStatus:1}").ToJson()); break;
        case "stop-balancer":
            Console.WriteLine(Run(admin, "{balancerStop:1}").ToJson()); break;
        case "drop":
            Console.WriteLine(Run(bmt, "{drop:\"" + args[1] + "\"}").ToJson()); break;
        case "explain": // explain <coll> : scatter find, report targeted shards
        {
            var coll = args[1];
            var ex = Run(bmt, "{explain:{find:\"" + coll + "\",filter:{}},verbosity:\"queryPlanner\"}");
            var qp = ex.Contains("queryPlanner") ? ex["queryPlanner"].AsBsonDocument : null;
            if (qp != null && qp.Contains("winningPlan"))
            {
                var wp = qp["winningPlan"].AsBsonDocument;
                if (wp.Contains("shards"))
                {
                    var sh = wp["shards"].AsBsonArray;
                    Console.WriteLine($"targeted shards = {sh.Count}");
                    foreach (var s in sh) Console.WriteLine("  shard: " + (s.AsBsonDocument.Contains("shardName") ? s["shardName"].ToString() : s.ToJson()));
                }
                else Console.WriteLine("winningPlan has NO 'shards' array => single-shard / unsharded target");
            }
            Console.WriteLine("---full explain---");
            Console.WriteLine(ex.ToJson());
            break;
        }
        case "shard": // shard <coll> <keyField>
        {
            var coll = args[1]; var key = args[2];
            var json = "{shardCollection:\"bmt_db." + coll + "\", key:{" + key + ":\"hashed\"}}";
            Console.WriteLine("cmd: " + json);
            Console.WriteLine(Run(admin, json).ToJson());
            break;
        }
        case "chunks": // chunks <coll> : chunk count per physical shard from config metadata
        {
            var ns = "bmt_db." + args[1];
            var config = client.GetDatabase("config");
            // find the collection's uuid in config.collections
            BsonValue uuid = BsonNull.Value;
            try {
                var collDoc = config.GetCollection<BsonDocument>("collections")
                    .Find(new BsonDocument("_id", ns)).FirstOrDefault();
                if (collDoc != null && collDoc.Contains("uuid")) uuid = collDoc["uuid"];
                Console.WriteLine("collections entry: " + (collDoc?.ToJson() ?? "NULL"));
            } catch (Exception e) { Console.WriteLine("config.collections read err: " + e.Message); }
            // group chunks by shard, try both uuid and ns filters
            foreach (var filt in new[] { new BsonDocument("uuid", uuid), new BsonDocument("ns", ns) })
            {
                try {
                    var pipeline = new BsonDocument[] {
                        new BsonDocument("$match", filt),
                        new BsonDocument("$group", new BsonDocument("_id", "$shard").Add("n", new BsonDocument("$sum", 1)))
                    };
                    var res = config.GetCollection<BsonDocument>("chunks").Aggregate<BsonDocument>(pipeline).ToList();
                    if (res.Count > 0) {
                        Console.WriteLine("chunks-by-shard (filter " + filt.ToJson() + "):");
                        foreach (var r in res) Console.WriteLine($"  shard={r["_id"]} chunks={r["n"]}");
                        break;
                    }
                } catch (Exception e) { Console.WriteLine("config.chunks err (" + filt.ToJson() + "): " + e.Message); }
            }
            break;
        }
        case "cmd": // cmd <db> <json>
            Console.WriteLine(Run(client.GetDatabase(args[1]), args[2]).ToJson()); break;
        case "dist": // dist <coll> : per-physical-shard doc counts via $collStats aggregation
        {
            var coll = args[1];
            var pipeline = new BsonDocument[] {
                BsonDocument.Parse("{$collStats:{count:{},storageStats:{}}}"),
                BsonDocument.Parse("{$project:{shard:1, count:1, storageBytes:\"$storageStats.size\"}}")
            };
            var cur = bmt.GetCollection<BsonDocument>(coll).Aggregate<BsonDocument>(pipeline);
            int n = 0;
            foreach (var d in cur.ToList())
            {
                n++;
                var shard = d.Contains("shard") ? d["shard"].ToString() : "(no shard field)";
                var cnt = d.Contains("count") ? d["count"].ToString() : "?";
                var sz = d.Contains("storageBytes") ? d["storageBytes"].ToString() : "?";
                Console.WriteLine($"  shard={shard} count={cnt} storageBytes={sz}");
            }
            Console.WriteLine($"physical shards holding data = {n}");
            break;
        }
        default:
            Console.WriteLine($"unknown mode: {mode}"); return 2;
    }
}
catch (Exception ex)
{
    Console.WriteLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
    return 3;
}
return 0;
