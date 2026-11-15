using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMesh.Core.Services;

/// <summary>
/// Separates GitHub Copilot CLI authentication (driver-scoped model runtime identity, e.g. an
/// Enterprise Managed User or Copilot-licensed account) from GitHub Personal Access Token
/// authentication (persona-scoped repository identity for <c>git</c>, <c>gh</c>, and PR operations).
/// </summary>
public static class CopilotCredentialHelper
{
    /// <summary>
    /// Persona-scoped GitHub PAT environment variable for repository git/PR operations.
    /// Deliberately named outside Copilot CLI's auth lookup chain (<c>COPILOT_GITHUB_TOKEN</c>,
    /// <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>) so it can safely stay in subprocess environments.
    /// </summary>
    public const string PersonaGitHubTokenEnvVar = "AGENTMESH_GITHUB_TOKEN";

    /// <summary>
    /// Driver-scoped Copilot OAuth / fine-grained token environment variable used to seed
    /// <c>~/.copilot/config.json</c> without colliding with repository <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>.
    /// </summary>
    public const string CopilotOAuthTokenEnvVar = "COPILOT_OAUTH_TOKEN";

    public const string ClassicPatUnsupportedMessage =
        "Classic GitHub Personal Access Tokens (ghp_) are not supported by GitHub Copilot CLI. " +
        "Use 'Interactive OAuth Login' to sign in to Copilot, or provide a fine-grained PAT (github_pat_...) " +
        "with the 'Copilot Requests' permission. To configure a GitHub PAT for git push and pull requests, " +
        "set it under the Persona's 'GitHub Credentials & Identity' settings instead.";

    /// <summary>
    /// Delimiter used when reading candidate Copilot credential files out of a container in one exec call.
    /// </summary>
    public const string ContainerReadDelimiter = "---AGENTMESH_COPILOT_CRED_SPLIT---";

    public const string GitHubComHost = "https://github.com";

    /// <summary>
    /// Parses Copilot <c>config.json</c>/<c>settings.json</c> documents, which the real Copilot CLI
    /// writes as JSONC (leading <c>//</c> header comments). Returns null when unparseable.
    /// </summary>
    public static JsonObject? ParseJsonC(string? jsonC)
    {
        if (string.IsNullOrWhiteSpace(jsonC)) return null;
        var stripped = string.Join("\n", jsonC.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        try
        {
            return JsonNode.Parse(stripped) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the GitHub login for the active sign-in from a Copilot CLI config document.
    /// Understands both the modern camelCase schema (<c>lastLoggedInUser</c>/<c>loggedInUsers</c>,
    /// token keys <c>https://github.com:login</c>) and the legacy snake_case plaintext schema
    /// (<c>last_logged_in_user</c>/<c>logged_in_users</c> with <c>copilot_tokens</c>).
    /// </summary>
    public static string? TryGetKnownLoginFromConfigJson(string? configJson)
    {
        var obj = ParseJsonC(configJson);
        if (obj == null) return null;

        foreach (var key in new[] { "lastLoggedInUser", "last_logged_in_user" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonObject uo &&
                uo.TryGetPropertyValue("login", out var ln) && ln is JsonValue lv &&
                lv.TryGetValue<string>(out var ls) && !string.IsNullOrWhiteSpace(ls))
            {
                return ls.Trim();
            }
        }

        foreach (var key in new[] { "loggedInUsers", "logged_in_users" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject io && io.TryGetPropertyValue("login", out var ln) &&
                        ln is JsonValue lv && lv.TryGetValue<string>(out var ls) &&
                        !string.IsNullOrWhiteSpace(ls))
                    {
                        return ls.Trim();
                    }
                }
            }
        }

        // Derive from qualified token keys like "https://github.com:octocat" (or ":github" account-type variant)
        foreach (var mapKey in new[] { "authTokens", "copilot_tokens", "copilotTokens" })
        {
            if (obj.TryGetPropertyValue(mapKey, out var mapNode) && mapNode is JsonObject map)
            {
                foreach (var prop in map)
                {
                    var suffix = StripHostPrefix(prop.Key);
                    if (suffix == null) continue;
                    var loginPart = suffix.Split(':', 2)[0].Trim();
                    if (loginPart.Length > 0) return loginPart;
                }
            }
        }

        return null;
    }

    /// <summary>Returns the part of a token-map key after the host prefix, or null when not host-prefixed.</summary>
    private static string? StripHostPrefix(string key)
    {
        foreach (var prefix in new[] { GitHubComHost, "https://ghe.io", "http://localhost" })
        {
            if (key.StartsWith(prefix + ":", StringComparison.Ordinal))
                return key[(prefix.Length + 1)..];
        }
        return null;
    }

    /// <summary>Builds the native token-map key ("https://github.com:login") for a sign-in.</summary>
    public static string BuildHostLoginKey(string host, string login) =>
        $"{(string.IsNullOrWhiteSpace(host) ? GitHubComHost : host.Trim().TrimEnd('/'))}:{login.Trim()}";

    /// <summary>
    /// Returns true when <paramref name="token"/> is a GitHub classic PAT (<c>ghp_...</c>),
    /// which GitHub Copilot CLI unconditionally rejects.
    /// </summary>
    public static bool IsClassicGitHubPat(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return token.Trim().StartsWith("ghp_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="token"/> is non-empty and eligible for GitHub Copilot CLI
    /// authentication (not a classic <c>ghp_</c> PAT and not an internal AgentMesh bearer token).
    /// </summary>
    public static bool IsSupportedCopilotToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var trimmed = token.Trim();
        if (IsClassicGitHubPat(trimmed)) return false;
        if (trimmed.StartsWith("mesh_tok_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Sanitizes an inherited <c>COPILOT_GITHUB_TOKEN</c> value, rejecting classic <c>ghp_</c> PATs
    /// and rejecting values that merely mirror the persona's repository GitHub PAT.
    /// </summary>
    public static string? SanitizeCopilotEnvToken(string? copilotEnvToken, string? personaGitHubToken = null)
    {
        if (!IsSupportedCopilotToken(copilotEnvToken)) return null;
        var trimmed = copilotEnvToken!.Trim();

        if (!string.IsNullOrWhiteSpace(personaGitHubToken) &&
            string.Equals(trimmed, personaGitHubToken.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Extracts the best Copilot authentication token from a <c>~/.copilot/config.json</c> document
    /// (JSONC tolerated). Prefers the token that the CLI itself would resolve: the active sign-in
    /// record (<c>lastLoggedInUser</c>/<c>loggedInUsers</c> or legacy snake_case names) matched to a
    /// <c>host:login</c>-qualified token entry in <c>authTokens</c>/<c>copilot_tokens</c>/<c>copilotTokens</c>.
    /// Classic <c>ghp_</c> tokens are never returned.
    /// </summary>
    public static string? TryExtractCopilotTokenFromJson(string? json)
    {
        var root = ParseJsonC(json);
        if (root == null) return null;

        // 1. Tokens matched to known sign-in records, via host:login-qualified map keys.
        foreach (var record in EnumerateSignInRecords(root))
        {
            var key = BuildHostLoginKey(record.Host, record.Login);
            var tok = FindTokenForQualifiedKey(root, key);
            if (tok != null) return tok;
        }

        // 2. Any host:login-qualified token entry (account-type suffix variants included).
        foreach (var mapKey in TokenMapKeys)
        {
            if (root.TryGetPropertyValue(mapKey, out var mapNode) && mapNode is JsonObject map)
            {
                foreach (var prop in map)
                {
                    if (StripHostPrefix(prop.Key) == null) continue; // skip plain-host keys for now
                    var tok = ReadTokenFromMapValue(prop.Value);
                    if (tok != null) return tok;
                }
            }
        }

        // 3. Plain-host authTokens entries (older seed shape; the CLI cannot resolve an active
        //    sign-in from these alone, but the token value is still the best extraction we have).
        if (root.TryGetPropertyValue("authTokens", out var authNode) && authNode is JsonObject authMap)
        {
            foreach (var prop in authMap)
            {
                var tok = ReadTokenFromMapValue(prop.Value);
                if (tok != null) return tok;
            }
        }

        // 4. Top-level token fields (legacy config or direct token).
        var topLevel = ReadFirstSupportedTokenProperty(root);
        if (topLevel != null) return topLevel;

        // 5. ~/.config/github-copilot/hosts.json or apps.json: { "github.com": { "oauth_token": ... } }
        foreach (var prop in root)
        {
            if (prop.Value is null || TokenMapKeys.Contains(prop.Key) || SignInRecordKeys.Contains(prop.Key)) continue;
            if (prop.Value is JsonObject nestedObj)
            {
                var nested = ReadFirstSupportedTokenProperty(nestedObj);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static readonly string[] TokenMapKeys = { "copilot_tokens", "copilotTokens", "authTokens" };
    private static readonly string[] SignInRecordKeys = { "lastLoggedInUser", "last_logged_in_user", "loggedInUsers", "logged_in_users" };

    private static IEnumerable<(string Host, string Login)> EnumerateSignInRecords(JsonObject root)
    {
        foreach (var key in new[] { "lastLoggedInUser", "last_logged_in_user" })
        {
            if (root.TryGetPropertyValue(key, out var node) && node is JsonObject uo)
            {
                var pair = ReadHostLoginPair(uo);
                if (pair != null) yield return pair.Value;
            }
        }

        foreach (var key in new[] { "loggedInUsers", "logged_in_users" })
        {
            if (root.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject io)
                    {
                        var pair = ReadHostLoginPair(io);
                        if (pair != null) yield return pair.Value;
                    }
                }
            }
        }
    }

    private static (string Host, string Login)? ReadHostLoginPair(JsonObject userObj)
    {
        var host = GitHubComHost;
        if (userObj.TryGetPropertyValue("host", out var hNode) && hNode is JsonValue hjv &&
            hjv.TryGetValue<string>(out var hs) && !string.IsNullOrWhiteSpace(hs))
        {
            host = hs.Trim().TrimEnd('/');
        }
        if (userObj.TryGetPropertyValue("login", out var lNode) && lNode is JsonValue ljv &&
            ljv.TryGetValue<string>(out var ls) && !string.IsNullOrWhiteSpace(ls))
        {
            return (host, ls.Trim());
        }
        return null;
    }

    private static string? FindTokenForQualifiedKey(JsonObject root, string qualifiedKey)
    {
        foreach (var mapKey in TokenMapKeys)
        {
            if (!root.TryGetPropertyValue(mapKey, out var mapNode) || mapNode is not JsonObject map) continue;
            // Exact key, or with the account-type suffix the CLI writes ("host:login:github").
            if (map.TryGetPropertyValue(qualifiedKey, out var direct))
            {
                var tok = ReadTokenFromMapValue(direct);
                if (tok != null) return tok;
            }
            foreach (var prop in map)
            {
                if (!prop.Key.StartsWith(qualifiedKey + ":", StringComparison.Ordinal)) continue;
                var tok = ReadTokenFromMapValue(prop.Value);
                if (tok != null) return tok;
            }
        }
        return null;
    }

    private static string? ReadTokenFromMapValue(JsonNode? value)
    {
        if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
            return IsSupportedCopilotToken(s) ? s.Trim() : null;
        if (value is JsonObject obj)
            return ReadFirstSupportedTokenProperty(obj);
        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="configJson"/> contains a Copilot CLI login that the real
    /// <c>copilot</c> CLI can resolve on startup (validated empirically against Copilot CLI 1.0.112):
    /// <list type="bullet">
    /// <item>When a sign-in record exists (<c>lastLoggedInUser</c>/<c>loggedInUsers</c> in modern
    /// camelCase, or <c>last_logged_in_user</c>/<c>logged_in_users</c> in the legacy schema), the CLI
    /// resolves the active sign-in by looking up a <c>host:login</c>-qualified token entry in
    /// <c>authTokens</c>/<c>copilot_tokens</c>/<c>copilotTokens</c>. A record without a matching entry
    /// makes the CLI report "No authentication information found" or "Saved sign-in could not be
    /// read from credential storage" even when a bare <c>authTokens["https://github.com"]</c> token
    /// is present in the same file — such files are therefore NOT considered active.</item>
    /// <item>Files with no sign-in records and only plain-host token keys are NOT active either
    /// (the CLI cannot determine an identity; it fails with "Saved sign-in could not be read").</item>
    /// </list>
    /// This parser intentionally mirrors CLI behavior so AgentMesh verification never reports green
    /// for a file the CLI itself cannot authenticate with.
    /// </summary>
    public static bool HasActiveCopilotConfigLogin(string? configJson)
    {
        var root = ParseJsonC(configJson);
        if (root == null) return false;

        var records = EnumerateSignInRecords(root).ToList();
        if (records.Count > 0)
        {
            // Prefer the CLI's primary identity pointer (lastLoggedIn*), then any listed user.
            foreach (var record in records.OrderByDescending(r =>
                         IsLastLoggedInRecord(root, r)))
            {
                if (FindTokenForQualifiedKey(root, BuildHostLoginKey(record.Host, record.Login)) != null)
                    return true;
            }
            return false;
        }

        // No sign-in records: only a legacy qualified copilot_tokens entry can authenticate.
        foreach (var mapKey in new[] { "copilot_tokens", "copilotTokens" })
        {
            if (root.TryGetPropertyValue(mapKey, out var mapNode) && mapNode is JsonObject map)
            {
                foreach (var prop in map)
                {
                    if (StripHostPrefix(prop.Key) == null) continue;
                    if (ReadTokenFromMapValue(prop.Value) != null) return true;
                }
            }
        }

        return false;
    }

    private static bool IsLastLoggedInRecord(JsonObject root, (string Host, string Login) record)
    {
        foreach (var key in new[] { "lastLoggedInUser", "last_logged_in_user" })
        {
            if (root.TryGetPropertyValue(key, out var node) && node is JsonObject uo &&
                ReadHostLoginPair(uo) is (string h, string l) &&
                h == record.Host && l == record.Login)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes poisoned classic PAT (<c>ghp_...</c>) entries from a <c>~/.copilot/config.json</c>
    /// document (JSONC tolerated) while preserving valid Copilot tokens and unrelated settings.
    /// </summary>
    public static string? ScrubClassicPatFromConfigJson(string? existingJson, out bool modified)
        => ScrubClassicPatFromConfigJson(existingJson, out modified, pruneOrphanedLoggedInUsers: false);

    /// <summary>
    /// Removes poisoned classic PAT (<c>ghp_...</c>) entries from a <c>~/.copilot/config.json</c>
    /// document, and optionally prunes orphaned sign-in records — <c>loggedInUsers</c>/
    /// <c>lastLoggedInUser</c> (camelCase, current CLI) and <c>logged_in_users</c>/
    /// <c>last_logged_in_user</c> (legacy snake_case) — that have no <c>host:login</c>-qualified
    /// token entry anywhere in the file. Orphaned records are exactly what makes the real CLI fail
    /// a whole otherwise-valid file ("No authentication information found" / "Saved sign-in could
    /// not be read from credential storage").
    /// </summary>
    public static string? ScrubClassicPatFromConfigJson(
        string? existingJson,
        out bool modified,
        bool pruneOrphanedLoggedInUsers)
    {
        modified = false;
        if (string.IsNullOrWhiteSpace(existingJson)) return existingJson;

        var obj = ParseJsonC(existingJson);
        if (obj == null) return existingJson; // unparseable: caller decides whether to replace wholesale

        foreach (var key in new[] { "token", "oauth_token", "access_token", "user_token" })
        {
            if (obj.TryGetPropertyValue(key, out var valNode) &&
                valNode is JsonValue jv &&
                jv.TryGetValue<string>(out var strVal) &&
                IsClassicGitHubPat(strVal))
            {
                obj.Remove(key);
                modified = true;
            }
        }

        foreach (var mapKey in TokenMapKeys)
        {
            if (obj.TryGetPropertyValue(mapKey, out var mapNode) && mapNode is JsonObject map)
            {
                var keysToRemove = new List<string>();
                foreach (var (entryKey, valueNode) in map)
                {
                    if (IsClassicGitHubPat(RawEntryTokenString(valueNode)))
                        keysToRemove.Add(entryKey);
                }
                foreach (var k in keysToRemove)
                {
                    map.Remove(k);
                    modified = true;
                }
                if (map.Count == 0)
                {
                    obj.Remove(mapKey);
                    modified = true;
                }
            }
        }

        if (pruneOrphanedLoggedInUsers && PruneOrphanedSignInRecords(obj, out var pruned))
        {
            modified = pruned;
        }

        if (obj.Count == 0)
        {
            modified = true;
            return null;
        }

        return obj.ToJsonString();
    }

    private static string? RawEntryTokenString(JsonNode? value)
    {
        if (value is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        if (value is JsonObject obj)
        {
            foreach (var key in new[] { "oauth_token", "access_token", "user_token", "token" })
            {
                if (obj.TryGetPropertyValue(key, out var n) && n is JsonValue jvv &&
                    jvv.TryGetValue<string>(out var sv))
                {
                    return sv;
                }
            }
        }
        return null;
    }

    private static bool PruneOrphanedSignInRecords(JsonObject obj, out bool changed)
    {
        changed = false;
        bool HasEntryFor((string Host, string Login) record) =>
            FindTokenForQualifiedKey(obj, BuildHostLoginKey(record.Host, record.Login)) != null;

        foreach (var arrKey in new[] { "loggedInUsers", "logged_in_users" })
        {
            if (obj.TryGetPropertyValue(arrKey, out var usersNode) && usersNode is JsonArray arr)
            {
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is not JsonObject uObj ||
                        ReadHostLoginPair(uObj) is not (string h, string l) ||
                        !HasEntryFor((h, l)))
                    {
                        arr.RemoveAt(i);
                        changed = true;
                    }
                }
                if (arr.Count == 0)
                {
                    obj.Remove(arrKey);
                    changed = true;
                }
            }
        }

        foreach (var lastKey in new[] { "lastLoggedInUser", "last_logged_in_user" })
        {
            if (obj.TryGetPropertyValue(lastKey, out var lastNode))
            {
                if (lastNode is not JsonObject lastObj ||
                    ReadHostLoginPair(lastObj) is not (string lh, string ll) ||
                    !HasEntryFor((lh, ll)))
                {
                    obj.Remove(lastKey);
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Merges a supported Copilot token into <c>~/.copilot/config.json</c> without clobbering
    /// unrelated settings (<c>firstLaunchAt</c>, <c>trustedFolders</c>, ...).
    /// <para>
    /// CRITICAL FORMAT NOTE (empirically validated against Copilot CLI 1.0.112): the CLI keys token
    /// entries by <c>host:login</c> (e.g. <c>authTokens["https://github.com:octocat"]</c>) and
    /// resolves the active sign-in through <c>lastLoggedInUser</c>/<c>loggedInUsers</c>. Writing a
    /// bare <c>authTokens["https://github.com"]</c> entry — the historical AgentMesh seed shape —
    /// makes the CLI treat the entire file as unreadable. Therefore: when no login is known
    /// (neither <paramref name="login"/> nor derivable from the existing file), this method never
    /// writes a plain-host token entry; callers must fall back to <c>COPILOT_GITHUB_TOKEN</c>
    /// environment injection instead (which the CLI checks first and which wins over file state).
    /// </para>
    /// When <paramref name="overwriteExisting"/> is false, an existing active native login is left
    /// untouched. When true, stale maps/records are replaced (e.g. on account switch).
    /// </summary>
    public static string? MergeCopilotTokenIntoConfigJson(
        string? existingJson,
        string copilotToken,
        bool overwriteExisting = false)
        => MergeCopilotTokenIntoConfigJson(existingJson, copilotToken, login: null, overwriteExisting);

    public static string? MergeCopilotTokenIntoConfigJson(
        string? existingJson,
        string copilotToken,
        string? login,
        bool overwriteExisting = false)
    {
        var scrubbed = ScrubClassicPatFromConfigJson(existingJson, out _, pruneOrphanedLoggedInUsers: true);
        var obj = ParseJsonC(scrubbed) ?? new JsonObject();

        if (!IsSupportedCopilotToken(copilotToken))
        {
            PruneOrphanedSignInRecords(obj, out _);
            return obj.Count == 0 ? null : obj.ToJsonString();
        }

        var cleanToken = copilotToken.Trim();
        // Derive the login from existing file records only when NOT overwriting: an overwrite means
        // "install this token" (possibly a different account), and reusing a stale derived login
        // would key the new token under the old identity. Overwrite callers must pass the login
        // they resolved for THIS token (or leave the file untouched; env injection covers auth).
        var effectiveLogin = !string.IsNullOrWhiteSpace(login)
            ? login!.Trim()
            : (overwriteExisting ? null : TryGetKnownLoginFromConfigJson(obj.ToJsonString()));

        if (!overwriteExisting && HasActiveCopilotConfigLogin(obj.ToJsonString()))
        {
            return obj.ToJsonString();
        }

        if (effectiveLogin == null)
        {
            // Cannot build the native host:login shape without a login. Do NOT write the poison
            // plain-host entry; leave a clean scrubbed file and let env-token injection carry auth.
            return obj.Count == 0 ? null : obj.ToJsonString();
        }

        if (overwriteExisting)
        {
            foreach (var k in new[] { "copilot_tokens", "copilotTokens", "authTokens",
                                      "loggedInUsers", "lastLoggedInUser",
                                      "logged_in_users", "last_logged_in_user", "token" })
            {
                obj.Remove(k);
            }
        }
        else if (FindTokenForQualifiedKey(obj, BuildHostLoginKey(GitHubComHost, effectiveLogin)) != null)
        {
            // A native entry for this login already exists; only backfill missing records below.
        }

        var qualifiedKey = BuildHostLoginKey(GitHubComHost, effectiveLogin);

        var authMap = GetOrCreateMap(obj, "authTokens");
        // Drop legacy plain-host entries: co-keyed files have not been validated against the CLI,
        // and the historical bare-host key is the exact shape that breaks active sign-in resolution.
        foreach (var plainKey in authMap.Where(p => p.Value is not null && StripHostPrefix(p.Key) == null)
                     .Select(p => p.Key).ToList())
        {
            authMap.Remove(plainKey);
        }
        authMap[qualifiedKey] = new JsonObject { ["token"] = cleanToken };

        // Legacy compatibility maps the current CLI still honors alongside authTokens; also makes
        // the file visible to older AgentMesh components that grep for "copilot_tokens".
        var legacyMap = GetOrCreateMap(obj, "copilot_tokens");
        legacyMap[qualifiedKey] = cleanToken;

        var record = new JsonObject { ["host"] = GitHubComHost, ["login"] = effectiveLogin };
        obj["lastLoggedInUser"] = (JsonObject)record.DeepClone();

        var users = obj["loggedInUsers"] as JsonArray ?? new JsonArray();
        var alreadyListed = users.Any(u => u is JsonObject io &&
            ReadHostLoginPair(io) is (string h, string l) && h == GitHubComHost && l == effectiveLogin);
        if (!alreadyListed)
        {
            users.Add((JsonObject)record.DeepClone());
        }
        obj["loggedInUsers"] = users;

        PruneOrphanedSignInRecords(obj, out _);
        return obj.ToJsonString();
    }

    private static JsonObject GetOrCreateMap(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonObject existing)
        {
            return existing;
        }
        var map = new JsonObject();
        obj[key] = map;
        return map;
    }

    /// <summary>
    /// Ensures <c>~/.copilot/settings.json</c> sets <c>storeTokenPlaintext: true</c> so
    /// <c>copilot login --device-code</c> persists tokens in headless containers without an OS keyring.
    /// </summary>
    public static string EnsurePlaintextStorageSettingsJson(string? existingSettingsJson, out bool modified)
    {
        modified = false;
        var obj = ParseJsonC(existingSettingsJson);

        if (obj == null)
        {
            obj = new JsonObject();
            modified = true;
        }

        if (!obj.TryGetPropertyValue("storeTokenPlaintext", out var existingVal) ||
            existingVal is not JsonValue jv ||
            !jv.TryGetValue<bool>(out var b) ||
            !b)
        {
            obj["storeTokenPlaintext"] = true;
            modified = true;
        }

        return obj.ToJsonString();
    }

    /// <summary>
    /// Safely merges <c>oauthToken</c> into an <c>Agent.CapabilitiesJson</c> blob without
    /// clobbering existing capability fields or array-shaped capability lists.
    /// </summary>
    public static string MergeOAuthTokenIntoDriverCapabilitiesJson(string? existingCapabilitiesJson, string oauthToken, string? login = null)
    {
        var obj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(existingCapabilitiesJson))
        {
            try
            {
                var node = JsonNode.Parse(existingCapabilitiesJson);
                if (node is JsonObject existingObj)
                {
                    obj = existingObj;
                }
                else if (node is JsonArray existingArr && existingArr.Count > 0)
                {
                    obj["capabilities"] = existingArr;
                }
            }
            catch (JsonException) { }
        }

        obj["oauthToken"] = oauthToken.Trim();
        if (!string.IsNullOrWhiteSpace(login))
        {
            obj["oauthLogin"] = login.Trim();
        }
        return obj.ToJsonString();
    }

    /// <summary>
    /// Formats <c>~/.config/gh/hosts.yml</c> in the modern multi-account GitHub CLI schema so
    /// <c>gh auth token</c> and <c>gh auth git-credential</c> work offline without triggering
    /// legacy single-account migration checks, while keeping GitHub PAT auth isolated from Copilot CLI.
    /// </summary>
    public static string FormatGhHostsYaml(string gitHubToken)
    {
        var clean = gitHubToken.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("github.com:");
        sb.AppendLine("    users:");
        sb.AppendLine("        git:");
        sb.Append("            oauth_token: ").AppendLine(clean);
        sb.Append("    oauth_token: ").AppendLine(clean);
        sb.AppendLine("    user: git");
        sb.AppendLine("    git_protocol: https");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the single container shell read command used to inspect Copilot-only credential
    /// sources (<c>/root/.copilot/config.json</c>, <c>/root/.config/github-copilot/*.json</c>,
    /// and <c>COPILOT_OAUTH_TOKEN</c>/<c>COPILOT_GITHUB_TOKEN</c>), deliberately excluding
    /// <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, and <c>/root/.config/gh/hosts.yml</c>.
    /// </summary>
    public static string BuildContainerCopilotReadCommand() =>
        $"cat /root/.copilot/config.json 2>/dev/null; printf '\\n{ContainerReadDelimiter}\\n'; " +
        $"cat /root/.config/github-copilot/hosts.json 2>/dev/null; printf '\\n{ContainerReadDelimiter}\\n'; " +
        $"cat /root/.config/github-copilot/apps.json 2>/dev/null; printf '\\n{ContainerReadDelimiter}\\n'; " +
        $"printf '%s' \"${{COPILOT_OAUTH_TOKEN:-${{COPILOT_GITHUB_TOKEN:-}}}}\"; printf '\\n{ContainerReadDelimiter}\\n'; " +
        $"printf '%s' \"${{AGENTMESH_GITHUB_TOKEN:-${{GH_TOKEN:-${{GITHUB_TOKEN:-}}}}}}\"";

    /// <summary>
    /// Parses the stdout returned by <see cref="BuildContainerCopilotReadCommand"/> and returns
    /// the active Copilot token (or null when unauthenticated or only a GitHub PAT exists).
    /// </summary>
    public static string? ParseContainerCopilotReadOutput(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        var segments = stdout.Split(ContainerReadDelimiter, StringSplitOptions.None);
        var copilotConfig = segments.Length > 0 ? segments[0].Trim() : null;
        var copilotHosts = segments.Length > 1 ? segments[1].Trim() : null;
        var copilotApps = segments.Length > 2 ? segments[2].Trim() : null;
        var copilotEnv = segments.Length > 3 ? segments[3].Trim() : null;
        var personaGhToken = segments.Length > 4 ? segments[4].Trim() : null;

        // Only treat ~/.copilot/config.json as active if it has copilot_tokens or authTokens
        // (never fall back to legacy ~/.config/github-copilot/hosts.json or apps.json, which
        // @github/copilot CLI 1.x does not read and would create false-positive auth checks).
        if (HasActiveCopilotConfigLogin(copilotConfig))
        {
            var tok = TryExtractCopilotTokenFromJson(copilotConfig);
            if (tok != null) return tok;
        }

        var sanitizedEnv = SanitizeCopilotEnvToken(copilotEnv, personaGhToken);
        if (sanitizedEnv != null) return sanitizedEnv;

        // A config.json that HasActiveCopilotConfigLogin() rejects is a file the real CLI also
        // cannot authenticate with (orphaned sign-in records poison the whole file). Never report
        // such a file's token as "authenticated" — that is exactly how Verify went green while
        // chat kept failing with "No authentication information found".
        return null;
    }

    private static string? ReadFirstSupportedTokenProperty(JsonObject obj)
    {
        foreach (var key in new[] { "oauth_token", "access_token", "user_token", "token" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue jv &&
                jv.TryGetValue<string>(out var str) &&
                IsSupportedCopilotToken(str))
            {
                return str.Trim();
            }
        }
        return null;
    }

    private static string? ReadFirstSupportedTokenProperty(JsonElement obj)
    {
        foreach (var key in new[] { "oauth_token", "access_token", "user_token", "token" })
        {
            if (obj.TryGetProperty(key, out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                IsSupportedCopilotToken(prop.GetString()))
            {
                return prop.GetString()!.Trim();
            }
        }
        return null;
    }
}
