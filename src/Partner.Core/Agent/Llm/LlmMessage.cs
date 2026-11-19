namespace Partner.Core.Agent.Llm;

/// <summary>
/// One tool call requested by the model. <see cref="ArgumentsJson"/> is the raw JSON string the model produced;
/// this layer neither parses nor validates it — the tool itself and the governance layer do.
/// </summary>
/// <param name="Id">The call id given by the model; used when filling in a <c>role = "tool"</c> message.</param>
/// <param name="Name">Tool name.</param>
/// <param name="ArgumentsJson">Tool arguments (raw JSON text).</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One conversation message. Immutable value object.
/// </summary>
/// <param name="Role">Role: <c>"system"</c> / <c>"user"</c> / <c>"assistant"</c> / <c>"tool"</c>.</param>
/// <param name="Content">Text content; may be <see langword="null"/> for a tool-call-only message.</param>
/// <param name="ToolCalls">Tool calls carried by an assistant message; <see langword="null"/> when none.</param>
/// <param name="ToolCallId">Set only when <c>role == "tool"</c>: the matching <see cref="LlmToolCall.Id"/>.</param>
/// <param name="Name">Optional name (for example the tool name when <c>role == "tool"</c>).</param>
/// <param name="Images">Data URIs of attached images, in attach order; <see langword="null"/> when none.</param>
public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<string>? Images = null);

internal static class LlmConversation
{
    // A tool may return images between parallel results. Both wires require every result immediately after
    // the assistant's calls; defer those image messages until the whole matching result group has been sent.
    internal static IEnumerable<LlmMessage> OrderToolResults(IReadOnlyList<LlmMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            yield return message;
            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 1 } calls)
            {
                continue;
            }

            var firstImage = i + 1;
            while (firstImage < messages.Count && messages[firstImage].Role == "tool")
            {
                firstImage++;
            }

            if (firstImage >= messages.Count || messages[firstImage].Role != "user"
                || messages[firstImage].Images is not { Count: > 0 }
                || firstImage + 1 >= messages.Count || messages[firstImage + 1].Role != "tool")
            {
                continue;
            }

            var pending = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            if (pending.Count != calls.Count)
            {
                continue;
            }

            var end = i + 1;
            var hasImages = false;
            while (end < messages.Count && pending.Count > 0)
            {
                var next = messages[end];
                if (next.Role == "tool" && next.ToolCallId is not null && pending.Remove(next.ToolCallId))
                {
                    end++;
                }
                else if (next.Role == "user" && next.Images is { Count: > 0 })
                {
                    hasImages = true;
                    end++;
                }
                else
                {
                    break;
                }
            }

            if (pending.Count != 0 || !hasImages)
            {
                continue;
            }

            for (var j = i + 1; j < end; j++)
            {
                if (messages[j].Role == "tool") yield return messages[j];
            }

            for (var j = i + 1; j < end; j++)
            {
                if (messages[j].Role == "user") yield return messages[j];
            }

            i = end - 1;
        }
    }
}
