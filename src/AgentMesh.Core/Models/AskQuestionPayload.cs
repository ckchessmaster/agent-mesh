using System.Text.Json;

namespace AgentMesh.Core.Models;

/// <summary>
/// One question exactly as the agent asked it, after the wire shape has been normalised.
/// </summary>
/// <param name="Question">
/// The question text. Never null or blank: an entry without text is dropped by
/// <see cref="AskQuestionPayload.Parse"/> rather than carried as an empty question the operator
/// would have to guess at.
/// </param>
/// <param name="Options">
/// The choices offered, in the order the agent offered them. Empty when the question is open-ended.
/// </param>
/// <param name="IsMultiSelect">True when the operator may pick more than one option.</param>
public sealed record AskQuestion(
    string Question,
    IReadOnlyList<string> Options,
    bool IsMultiSelect = false);

/// <summary>
/// Reads the <c>ask_question</c> payload into questions, whatever shape the driver recorded it in.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that knows the shapes a question can arrive in, because the readers of that
/// payload disagree about nothing else and must not start here. The platform surface is
/// <c>{"questions":[{"question":"...","options":["..."],"is_multi_select":false}]}</c>, and that is what
/// a function-tool driver records. Three other shapes are in production transcripts today:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>{"questions":"[{...}]"}</c> — the array parameter serialised as a <em>string</em>. Antigravity
///     logs its own <c>ask_question</c> tool call this way in
///     <c>~/.gemini/antigravity-cli/brain/&lt;id&gt;/.system_generated/logs/transcript.jsonl</c>, and the
///     runner forwards the arguments verbatim, so the string form is what lands in
///     <c>session_messages.tool_args</c>.
///     </description>
///   </item>
///   <item><description>a bare array, which is what a fenced <c>```ask_question</c> block most often holds;</description></item>
///   <item>
///     <description>
///     a single <c>{"question":"...","options":[...]}</c> object, from a driver that asks one question per
///     call.
///     </description>
///   </item>
/// </list>
/// <para>
/// The failure this prevents is not cosmetic. In November 2026 job c28eca57 parked on two real questions
/// asked through the Antigravity tool, the session chat rendered both, the operator answered both — and the
/// Stage 1 activity, which only accepted the array shape, reported that no questions had been asked and
/// advanced the job to the plan gate. A reader that insists on one shape does not fail loudly; it fails as
/// a confident "nothing to ask".
/// </para>
/// </remarks>
public static class AskQuestionPayload
{
    /// <summary>The tool name every driver uses for this channel, and the name the transcript rows carry.</summary>
    public const string ToolName = "ask_question";

    /// <summary>
    /// How many questions one discovery turn is allowed to put in front of the operator. Beyond this the
    /// operator is filling a form rather than steering a job, so the remainder is dropped rather than paginated.
    /// </summary>
    public const int MaxQuestions = 5;

    /// <summary>
    /// The questions in <paramref name="payload"/>, in order, capped at <see cref="MaxQuestions"/>.
    /// </summary>
    /// <param name="payload">
    /// A tool-argument document, a fenced block's contents, or an encoded-array string. Anything that is not
    /// readable JSON yields no questions, which the caller must treat as "the agent asked nothing" and say out
    /// loud — never as an invitation to guess over prose.
    /// </param>
    public static IReadOnlyList<AskQuestion> Parse(string? payload)
    {
        var result = new List<AskQuestion>();
        var text = payload?.Trim();
        if (string.IsNullOrEmpty(text)) return result;

        using var outer = TryParse(text);
        if (outer is null) return result;

        // {"questions":"[...]"}: the array carried as a string. Unwrap one layer and read that, because
        // everything downstream (the session card, the cockpit, the workflow's questions) is the array.
        if (outer.RootElement.ValueKind == JsonValueKind.Object &&
            TryGet(outer.RootElement, "questions", out var encoded) &&
            encoded.ValueKind == JsonValueKind.String)
        {
            using var inner = TryParse(encoded.GetString());
            if (inner is not null) Collect(inner.RootElement, result);
            return result;
        }

        Collect(outer.RootElement, result);
        return result;
    }

    private static void Collect(JsonElement root, List<AskQuestion> into)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                AddEach(root, into);
                return;

            case JsonValueKind.Object:
                if (TryGet(root, "questions", out var questions))
                {
                    if (questions.ValueKind == JsonValueKind.Array) AddEach(questions, into);
                    return;
                }

                // One question per call: {"question":"...","options":[...]}. Not a list at all, so it is
                // read as the single-item list it describes.
                if (TryGet(root, "question", out _) && ReadQuestion(root) is { } single)
                {
                    into.Add(single);
                }
                return;

            case JsonValueKind.String:
                // The payload itself was an encoded document ("[{...}]") rather than an object holding one.
                using (var inner = TryParse(root.GetString()))
                {
                    if (inner is not null) Collect(inner.RootElement, into);
                }
                return;
        }
    }

    private static void AddEach(JsonElement array, List<AskQuestion> into)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (ReadQuestion(item) is { } question)
            {
                into.Add(question);
                if (into.Count >= MaxQuestions) return;
            }
        }
    }

    private static AskQuestion? ReadQuestion(JsonElement item)
    {
        // A bare string in the list is a question with no options — the shape a driver writes when the
        // operator's answer is free text.
        if (item.ValueKind == JsonValueKind.String)
        {
            var text = item.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : new AskQuestion(text, []);
        }

        if (item.ValueKind != JsonValueKind.Object) return null;

        var question = ReadText(item, "question") ?? ReadText(item, "id") ?? ReadText(item, "questionId");
        if (string.IsNullOrWhiteSpace(question)) return null;

        var options = new List<string>();
        if (TryGet(item, "options", out var optionsElem) && optionsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in optionsElem.EnumerateArray())
            {
                var label = option.ValueKind == JsonValueKind.String
                    ? option.GetString()
                    : ReadText(option, "label") ?? ReadText(option, "text") ?? ReadText(option, "value");
                if (!string.IsNullOrWhiteSpace(label)) options.Add(label.Trim());
            }
        }

        // Both spellings are on the wire: the tool schema declares snake_case, and a driver that binds the
        // same payload through its own model layer tends to echo camelCase back.
        var multiSelect = ReadFlag(item, "is_multi_select") || ReadFlag(item, "isMultiSelect") || ReadFlag(item, "multi_select");

        return new AskQuestion(question.Trim(), options, multiSelect);
    }

    private static bool ReadFlag(JsonElement element, string name) =>
        TryGet(element, name, out var value) &&
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };

    private static string? ReadText(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads <paramref name="name"/> ignoring case, because drivers log both <c>questions</c> and <c>Questions</c>.</summary>
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value)) return true;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static JsonDocument? TryParse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        try
        {
            return JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
