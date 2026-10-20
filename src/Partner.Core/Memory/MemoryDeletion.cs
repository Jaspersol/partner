using System.Globalization;

namespace Partner.Core.Memory;

/// <summary>
/// Carries a session deletion through to what was remembered from it.
/// <para>
/// A memory distilled from an event records that event's seq in <c>SourceEventSeq</c>. Leaving the memory after
/// the event is deleted would be a leak: it would still be recalled and injected into a prompt weeks later.
/// </para>
/// <para>
/// The link is exact: a record is removed if and only if the event it came from was removed.
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

        // One range query, not one per seq: a deleted span is a conversation, and its length is the person's to
        // choose.
        var records = await memory.FindBySourceRangeAsync(fromSeq, toSeq, ct).ConfigureAwait(false);

        foreach (var record in records)
        {
            // The same four-step privacy delete as any other removal, so the text leaves the FTS index and
            // the vector table rather than only being marked invalid.
            await memory.SoftDeleteAsync(record.RecordId, actor, ct).ConfigureAwait(false);
        }

        return records.Count;
    }
}
