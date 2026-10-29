using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentMesh.Core.Services;

/// <summary>
/// What a runner says its own driver can do, pushed to the API at registration instead of being
/// guessed by the API at request time.
/// </summary>
/// <remarks>
/// <para>
/// This is option C of the capability-discovery design: invert the direction of truth. The runner
/// is the process that actually invokes the binary, so it is the only component that can say what
/// that binary accepts. Before this, the API resolved capabilities by shelling out to
/// <c>agy</c>/<c>claude</c> or dialing the driver's endpoint from inside
/// <c>GET /api/system/capabilities</c>.
/// </para>
/// <para>
/// That request-path probing was not merely slow. It was <em>wrong by construction in
/// production</em>: the API image is <c>aspnet:10.0</c> with no driver binaries installed
/// (<c>deploy/docker/Dockerfile.api</c>), so <c>AgyExecutableLocator.Resolve()</c> finds nothing
/// on the API host and every agy driver reports "the agy CLI is not installed on the API host"
/// forever, however many runners are healthy on their own machines. The dashboard's model picker
/// was empty for reasons that had nothing to do with the driver.
/// </para>
/// <para>
/// A declaration is a claim, not a fact, so it carries its own provenance
/// (<see cref="ObservedAt"/> and <see cref="Source"/>) and is treated as stale after
/// <see cref="FreshnessWindow"/>. The API falls back to a live probe when a declaration is
/// missing or stale, which keeps older runners (which send nothing) working exactly as they do
/// today. That rollout tail is the main cost of this design and the reason it is additive rather
/// than a replacement.
/// </para>
/// </remarks>
public sealed record DriverCapabilityDeclaration
{
    /// <summary>
    /// How long a declaration is believed before the API re-reads the driver itself.
    /// </summary>
    /// <remarks>
    /// Shorter than the probe's positive TTL on purpose: a declaration is a second-hand claim, so
    /// the API should be willing to check the provider directly sooner than it would re-run its
    /// own successful read. Runners re-push on registration and on every hub reconnect, so an
    /// online runner refreshes well inside this window regardless.
    /// </remarks>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Schema tag so a future shape change is distinguishable from a truncated or foreign blob.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bumped to 2 by <see cref="Endpoint"/>, which changed what a declaration asserts: v1 said
    /// "this adapter reported these models", v2 says "this adapter at this endpoint did".
    /// </para>
    /// <para>
    /// It is a discriminator for diagnosis, <b>not</b> an admission check, and must stay that way.
    /// The API is upgraded before the fleet's runners are, so a reader that rejected anything below
    /// the current tag would empty every picker on deploy day and stay empty until every host
    /// happened to re-register. A v1 blob is read as a v2 blob with an unbound endpoint, which is
    /// exactly what it is. Pinned both ways by
    /// <c>Declared_AVersionOneBlobWithoutAnEndpointKeyIsStillTrusted</c> and
    /// <c>Declared_ANewerBlobThanTheReaderKnowsStillParses</c>.
    /// </para>
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Canonical adapter id this declaration describes (see <see cref="DriverAdapters"/>).</summary>
    [JsonPropertyName("adapter")]
    public string Adapter { get; init; } = string.Empty;

    /// <summary>
    /// The endpoint this declaration was read from, for an HTTP driver, as the runner dialled it.
    /// Null for a host-level read (<c>agy</c>), which has no endpoint to bind to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter check alone cannot catch a row repointed between two OpenAI-compatible servers:
    /// the adapter still matches, so a stored catalogue keeps answering for a driver that now
    /// points somewhere else, and it does so until that runner happens to re-register. Binding a
    /// declaration to the URL it came from is what makes that edit take effect immediately.
    /// </para>
    /// <para>
    /// Compared through <see cref="DriverModelDiscovery.NormalizeEndpoint"/>, never as written. The
    /// row and the runner describe one server by two code paths, and an exact-string comparison
    /// would reject a correct catalogue over a trailing slash or a pasted <c>/models</c> suffix -
    /// regressing the picker to probing for reasons nobody could explain.
    /// </para>
    /// <para>
    /// Absent means <em>unbound</em>, not mismatched. Runners built before this field exists send no
    /// key, and reading that as a mismatch would empty every picker in a mixed-version fleet the
    /// moment the API is upgraded, which is the rollout tail this design exists to survive.
    /// </para>
    /// </remarks>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>
    /// Models the driver reported, with the options each one accepts. Empty means "the driver
    /// could not be read", which is different from a null <c>models</c> list elsewhere meaning
    /// "this driver has no discovery surface".
    /// </summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<DeclaredModel> Models { get; init; } = Array.Empty<DeclaredModel>();

    /// <summary>
    /// False when the driver has no way to be told its context window, so the UI hides the picker
    /// instead of offering a control that changes nothing.
    /// </summary>
    [JsonPropertyName("supportsContextSelection")]
    public bool SupportsContextSelection { get; init; }

    /// <summary>
    /// Where <see cref="Models"/> came from: <c>agy-models</c>, <c>openai-models-endpoint</c>,
    /// <c>claude-cli-contract</c>. Never a bare "we had a list".
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>
    /// Populated when the runner could not read its own driver, so the dashboard can say why
    /// rather than showing an empty picker with no explanation.
    /// </summary>
    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; init; }

    /// <summary>When the runner read the driver. Drives the staleness check.</summary>
    [JsonPropertyName("observedAt")]
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// True when this declaration is recent enough to be preferred over a live probe.
    /// </summary>
    /// <remarks>
    /// A declaration from a runner that died days ago must not pin the picker to a catalogue that
    /// may no longer be true; the API should be able to go look.
    /// <para>
    /// The window is applied symmetrically, so a timestamp up to one window in the future is still
    /// trusted but one further ahead is not. A runner whose clock is badly wrong cannot have its
    /// age computed at all, and a far-future <c>observedAt</c> is also how a hand-edited blob would
    /// make a declaration immortal - so both fall back to probing rather than being believed.
    /// </para>
    /// </remarks>
    public bool IsFresh(DateTimeOffset now)
    {
        if (ObservedAt == default) return false;

        var age = now - ObservedAt;
        return age < FreshnessWindow && age > -FreshnessWindow;
    }

    /// <summary>
    /// A model the runner's driver reported, in wire form.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate type from <see cref="DriverModelCapability"/>: this one is
    /// serialized across a process and version boundary and must stay stable, while the internal
    /// record is free to change with the picker. Field names here are part of the CLI-to-API
    /// contract and are pinned by <c>CapabilitiesWireFormatTests</c>.
    /// </remarks>
    public sealed record DeclaredModel
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        /// <summary>Effort values the driver accepts for this model.</summary>
        [JsonPropertyName("efforts")]
        public IReadOnlyList<string> Efforts { get; init; } = Array.Empty<string>();

        // No contextSizes field, deliberately. Which windows to *offer* is the API's product
        // policy (bounded to what the runtime will actually honour), not a provider fact, so a
        // runner is not asked to declare it and the API does not read it even if a future CLI
        // starts sending it. Accepting one would re-open the stale "500k" defect through a new
        // channel: a self-report advertising a window the runtime silently clamps.
    }

    /// <summary>
    /// Reads a declaration out of a driver's capabilities blob.
    /// </summary>
    /// <returns>
    /// The declaration, or null when the blob has none. Null is not an error: runners older than
    /// this change send nothing, and the API must fall back to probing rather than reporting an
    /// empty catalogue.
    /// </returns>
    /// <remarks>
    /// Tolerant by design. This blob has three writers (the runner, the dashboard's driver form,
    /// and the token-sync patch in <c>AgentKind</c>) and it is stored as text, so a malformed
    /// <c>declared</c> node must degrade to "no declaration" rather than take down the capability
    /// endpoint or discard the rest of the blob.
    /// </remarks>
    public static DriverCapabilityDeclaration? TryRead(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(capabilitiesJson);
            return TryRead(doc.RootElement);
        }
        catch (Exception)
        {
            // JsonException from a corrupt blob. "This runner did not give us a usable
            // declaration" is a fallback, not a failure.
            return null;
        }
    }

    /// <summary>
    /// Overload for callers that already parsed the blob, so the capability endpoint does not
    /// parse every driver's JSON twice per request.
    /// </summary>
    public static DriverCapabilityDeclaration? TryRead(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetProperty(root, "declared", out var declared) ||
                declared.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return declared.Deserialize<DriverCapabilityDeclaration>(ReadOptions);
        }
        catch (Exception)
        {
            // InvalidOperationException from a node whose shape does not match (a string where an
            // array belongs) is the same "no usable declaration" answer as a missing key.
            return null;
        }
    }

    /// <summary>
    /// Serializes a declaration for the registration payload.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>
    /// Overload for callers holding a <see cref="JsonNode"/> tree, so the capability endpoint does
    /// not parse every driver's blob a second time per request.
    /// </summary>
    public static DriverCapabilityDeclaration? TryRead(JsonNode? root)
    {
        try
        {
            if (root is not JsonObject obj) return null;

            JsonNode? declared = null;
            foreach (var prop in obj)
            {
                if (string.Equals(prop.Key, "declared", StringComparison.OrdinalIgnoreCase))
                {
                    declared = prop.Value;
                    break;
                }
            }

            // Absent key: no allocation. Only a present declaration is re-serialized into the
            // typed record.
            if (declared is not JsonObject) return null;

            return JsonSerializer.Deserialize<DriverCapabilityDeclaration>(declared.ToJsonString(), ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Case-insensitive lookup, matching <see cref="DriverModelDiscovery"/>'s tolerance for
    /// third-party serializers. The blob is written by our own CLI, but it is also hand-edited.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
