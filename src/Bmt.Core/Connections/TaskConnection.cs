using Bmt.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Bmt.Core.Connections;

/// <summary>
/// A single-use connection for ONE Task. Owns exactly one <see cref="MongoClient"/> (created fresh
/// by <see cref="TaskConnectionFactory"/>) and disposes it on release so the underlying cluster and
/// its (max-size-1) pool are torn down. NEVER cache, share, or reuse a <see cref="TaskConnection"/>,
/// its client, database, collections, sessions, or cursors across Tasks (test_instruction.md §2.2/§2.3).
/// </summary>
public sealed class TaskConnection : IDisposable
{
    private readonly MongoClient _client;
    private bool _disposed;

    internal TaskConnection(MongoClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Database = _client.GetDatabase(BmtConstants.DatabaseName);
        CalcInput = Database.GetCollection<CalcInputDoc>(BmtConstants.CalcInputCollection);
        CalcOutput = Database.GetCollection<CalcOutputDoc>(BmtConstants.CalcOutputCollection);
        CalcInputRaw = Database.GetCollection<BsonDocument>(BmtConstants.CalcInputCollection);
        CalcOutputRaw = Database.GetCollection<BsonDocument>(BmtConstants.CalcOutputCollection);
    }

    public IMongoDatabase Database { get; }

    public IMongoCollection<CalcInputDoc> CalcInput { get; }

    public IMongoCollection<CalcOutputDoc> CalcOutput { get; }

    /// <summary>Raw BSON view of <c>calc_input</c> (used by preflight/index-catalog checks).</summary>
    public IMongoCollection<BsonDocument> CalcInputRaw { get; }

    /// <summary>Raw BSON view of <c>calc_output</c>.</summary>
    public IMongoCollection<BsonDocument> CalcOutputRaw { get; }

    /// <summary>
    /// Disposes the owned <see cref="MongoClient"/>, releasing the cluster and its connection.
    /// This is the "connection actually released after each request" guarantee of §2.3.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MongoClientReleaser.Release(_client);
    }
}
