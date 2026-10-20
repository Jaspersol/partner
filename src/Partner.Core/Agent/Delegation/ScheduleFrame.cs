namespace Partner.Core.Agent.Delegation;

/// <summary>
/// A timed task's message, as the model reads it. It was written by the person earlier and fires later, so the
/// person is probably not at the keyboard: asking them a question gets no answer, and a turn that ends on one has
/// done nothing at all.
/// </summary>
public static class ScheduleFrame
{
    public const string Note =
        "[这是之前设好的定时任务，到点自动触发；用户此刻多半不在场。直接把事做完并简短汇报结果，不要向用户提问或等待确认；"
        + "做不了就说明卡在哪一步。]";

    public static string Wrap(string message) => $"{Note}\n{message}";
}
