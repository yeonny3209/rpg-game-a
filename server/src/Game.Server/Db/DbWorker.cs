using System.Threading.Channels;
using Game.Server.Logic;
using Game.Server.Net;

namespace Game.Server.Db;

/// <summary>
/// Runs every DB job one at a time, in order, off the zone thread (doc 02 §2.3: the tick never waits on I/O).
/// Because loads and saves share one queue, a quick logout → login always reads the save that was just queued.
/// </summary>
public sealed class DbWorker
{
    readonly ICharacterStore _store;
    readonly Channel<Func<Task>> _jobs = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _loop;
    int _pending;

    public int Pending => Volatile.Read(ref _pending);
    public int FailedSaves;
    public string Describe => _store.Describe;

    public DbWorker(ICharacterStore store)
    {
        _store = store;
        _loop = Task.Run(Loop);
    }

    public void Load(string loginName, Func<CharacterRecord> createNew, LoginAuth auth, Action<CharacterRecord, Exception> done)
    {
        Enqueue(async () =>
        {
            try
            {
                if (auth != null) await _store.AuthenticateAsync(loginName, auth);   // throws LoginException when refused
                done(await _store.LoadOrCreateAsync(loginName, createNew), null);
            }
            catch (Exception e) { done(null, e); }
        });
    }

    /// <summary>The record must be a private copy (CharacterRecord.Clone()).</summary>
    public void Save(CharacterRecord snapshot, IReadOnlyList<AuditEntry> audits)
    {
        Enqueue(async () =>
        {
            for (int attempt = 1; ; attempt++)
            {
                try { await _store.SaveAsync(snapshot, audits); return; }
                catch (Exception e) when (attempt < 3)
                {
                    Log.Warn($"save '{snapshot.Name}' failed (try {attempt}): {e.Message}");
                    await Task.Delay(500 * attempt);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref FailedSaves);
                    Log.Error($"save '{snapshot.Name}' FAILED, progress since last save is lost: {e.Message}");
                    return;
                }
            }
        });
    }

    void Enqueue(Func<Task> job)
    {
        Interlocked.Increment(ref _pending);
        if (!_jobs.Writer.TryWrite(job)) { Interlocked.Decrement(ref _pending); Log.Error("DB worker is closed; job dropped"); }
    }

    async Task Loop()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            try { await job(); }
            catch (Exception e) { Log.Error($"db job: {e}"); }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    /// <summary>Waits until every queued job has run (tests, shutdown).</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Pending > 0 && DateTime.UtcNow < until) await Task.Delay(10);
    }

    public async Task CloseAsync(TimeSpan timeout)
    {
        _jobs.Writer.TryComplete();
        await Task.WhenAny(_loop, Task.Delay(timeout));
    }
}
