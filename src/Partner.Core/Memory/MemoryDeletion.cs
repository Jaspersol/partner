using System.Globalization;

namespace Partner.Core.Memory;

/// <summary>
/// Carries a session deletion through to what was remembered from it.
/// <para>
/// A memory distilled from an event records that event's seq in <c>SourceEventSeq</c>. Deleting the event from
/// the session log and leaving the memory alone would be a real leak rather than an inconsistency: the person
/// asked for it to be gone, and it would still be recalled — and injected into a prompt weeks later, to a
/// conversation where they never said it.
/// </para>
/// <para>
/// The link is exact rather than fuzzy, which is the only kind worth having here: a record is removed if and
/// only if the event it came from was removed.
/// </para>
/// </summary>
public static class MemoryDeletion
{
    /// <summary>
    /// Removes every memory derived from the events in <paramref name="fromSeq"/>..<paramref name="toSeq"/>.
    /// </summary>
    /// <param name="memory">The store to purge from.</param>
    /// <param name="fromSeq">First removed session seq, inclusive.</param>
    /// <param name="toSeq">Last removed session seq, inclusive.</param>
    /// <param name="actor">Who asked. Recorded, so the audit says a person removed it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many records were removed.</returns>
    public static async Task<int> CascadeAsync(
        IMemoryStore memory,
        long fromSeq,
        long toSeq,
        Actor actor = Actor.Human,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);

        if (toSeq < fromSeq)
        {
            return 0;
        }

        // Walk the range rather than a range query: SourceEventSeq has no index (most records have none), and
        // the deleted span is a handful of events in practice. A missing index here is cheaper than a
        // permanent one over a column that is usually null.
        var victims = new List<string>();
        for (var seq = fromSeq; seq <= toSeq; seq++)
        {
            var record = await memory.FindBySourceAsync(seq, ct).ConfigureAwait(false);
            if (record is not null)
            {
                victims.Add(record.RecordId);
            }
        }

        foreach (var recordId in victims)
        {
            // The same four-step privacy delete as any other removal, so the text leaves the FTS index and
            // the vector table rather than only being marked invalid.
            await memory.SoftDeleteAsync(recordId, actor, ct).ConfigureAwait(false);
        }

        return victims.Count;
    }
}
