namespace Partner.Core.Storage;

/// <summary>
/// A single-writer, append-only line sink with an explicit durability barrier.
/// <para>
/// Semantics:
/// <list type="bullet">
///   <item><see cref="Append"/> may buffer — it does not promise durability.</item>
///   <item><see cref="FlushToDisk"/> is the only durability barrier. A failure there must surface as an
///   exception: callers treat "flush threw" as "the record is not durable".</item>
///   <item><see cref="ReadLines"/> only returns complete lines; a torn tail (crash mid-append) is
///   dropped by <see cref="RepairTail"/>.</item>
/// </list>
/// </para>
/// </summary>
public interface IAppendOnlySink : IDisposable
{
    /// <summary>Bytes durably on disk plus bytes still buffered.</summary>
    long Length { get; }

    /// <summary>Bytes acknowledged durable (after the last successful <see cref="FlushToDisk"/>).</summary>
    long DurableLength { get; }

    /// <summary>Appends one record. Adds the trailing newline. Never durable until <see cref="FlushToDisk"/>.</summary>
    void Append(string record);

    /// <summary>Durability barrier. Throws if the data could not be committed to disk.</summary>
    void FlushToDisk();

    /// <summary>All complete lines currently on disk.</summary>
    IReadOnlyList<string> ReadLines();

    /// <summary>Truncates the file to <paramref name="length"/> bytes (drops a torn tail).</summary>
    void TruncateTo(long length);

    /// <summary>
    /// Replaces the whole file with <paramref name="records"/> (each without its newline), atomically: a crash
    /// leaves either the old file or the new one, never a half-written mix.
    /// <para>
    /// This is the one operation that is not append-only. It exists so that removing history can be physical —
    /// otherwise "deleted" content would stay on disk with a tombstone in front of it.
    /// </para>
    /// </summary>
    void Rewrite(IReadOnlyList<string> records);

    /// <summary>Drops an unterminated final line (a crash mid-append) and returns how many bytes were removed.</summary>
    long RepairTail();
}
