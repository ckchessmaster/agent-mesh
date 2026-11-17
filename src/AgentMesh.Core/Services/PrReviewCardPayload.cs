using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMesh.Core.Services;

/// <summary>
/// The identity of a <see cref="Enums.TimelineCardType.PrReview"/> timeline card, and the rule for
/// merging two writers' payloads onto one card.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as its own type.</strong> Two independent code paths write the pull-request
/// review card: the workflow's review gate (which owns status transitions and the review round) and the
/// CLI/MCP <c>pr open</c> path (which owns the pull-request linkage the card displays — the summary the
/// persona wrote, the reviewer it asked, the turn that opened it). Neither one knows about the other,
/// and each deduplicates against its own notion of "the card for this PR", so every job that reached the
/// pull-request stage posted two cards that looked identical in the cockpit. Collapsing them needs one
/// shared definition of "the same pull request" and one shared payload merge, and those have to sit
/// below both assemblies rather than be copied into each.
/// </para>
/// <para>
/// <strong>Why the merge is a union instead of a replacement.</strong> Whoever writes second is not
/// stating that the other writer's fields were wrong. The gate's status update carries no
/// <c>content</c>, and replacing the payload there would silently delete the PR summary from a card that
/// had just been showing it. So existing keys survive unless the incoming payload supplies that same
/// key, which is exactly the "each writer owns its own fields" contract the two paths already assume.
/// </para>
/// <para>
/// <strong>Why a card with neither number nor URL never matches anything.</strong> The gate parks
/// before a pull request exists in some paths, and a payload with no pull-request reference describes
/// "the review gate for this job" in the abstract. Matching those aggressively would let an unrelated
/// later park adopt a card it was not meant to own. Absent an identifier, the honest answer is
/// "nothing to match", and a card gets created — which is what happened before, and is correct there.
/// </para>
/// </remarks>
public static class PrReviewCardPayload
{
    /// <summary>
    /// Reads the pull-request number and URL a card payload refers to. Either may be null; a payload
    /// that does not parse yields <c>(null, null)</c> rather than throwing, because these payloads are
    /// written by two producers and read in a hot polling loop.
    /// </summary>
    public static (int? Number, string? Url) ReadReference(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            int? number = null;
            if (doc.RootElement.TryGetProperty("prNumber", out var numberProp) &&
                numberProp.ValueKind == JsonValueKind.Number &&
                numberProp.TryGetInt32(out var parsed) && parsed > 0)
            {
                number = parsed;
            }

            string? url = null;
            if (doc.RootElement.TryGetProperty("prUrl", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(urlProp.GetString()))
            {
                url = NormalizeUrl(urlProp.GetString());
            }

            return (number, url);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Whether two payloads describe the same pull request. Numbers win when both sides have one;
    /// otherwise the URLs must agree. A reference with neither side to compare is not a match: two
    /// pulls of the same job are more likely than the coincidence that would make guessing right.
    /// </summary>
    public static bool IsSamePullRequest((int? Number, string? Url) left, (int? Number, string? Url) right)
    {
        if (left.Number is { } leftNumber && right.Number is { } rightNumber)
        {
            return leftNumber == rightNumber;
        }

        return left.Url is { } leftUrl && right.Url is { } rightUrl &&
               string.Equals(leftUrl, rightUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="incoming"/> over <paramref name="existing"/>, key by key.
    /// </summary>
    /// <remarks>
    /// When one side is not a JSON object the other side is kept, in whichever direction that falls out:
    /// a payload that cannot be parsed must not be written over one that can. The two cases are equally
    /// lopsided in practice — legacy rows carry hand-written junk that the next writer should repair,
    /// while a malformed <em>incoming</em> payload would corrupt a card the cockpit is currently
    /// rendering — and in both, the readable payload is the one that survives. Nothing here throws,
    /// because this runs on the way to a review gate.
    /// </remarks>
    public static string Merge(string? existing, string? incoming)
    {
        var baseObject = TryParseObject(existing);
        var overlayObject = TryParseObject(incoming);

        if (overlayObject is null)
        {
            return baseObject is null ? incoming ?? "{}" : existing!;
        }

        if (baseObject is null)
        {
            return incoming!;
        }

        foreach (var property in overlayObject)
        {
            baseObject[property.Key] = property.Value?.DeepClone();
        }

        return baseObject.ToJsonString();
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Comparison form of a pull-request URL: GitHub serves the same pull request with or without a
    /// trailing slash, and the two writers do not both trim.
    /// </summary>
    private static string? NormalizeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
}
