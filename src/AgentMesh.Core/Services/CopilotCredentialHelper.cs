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
    /// Extracts a valid Copilot authentication token from <c>~/.copilot/config.json</c>,
    /// <c>~/.config/github-copilot/hosts.json</c>, or <c>~/.config/github-copilot/apps.json</c>.
    /// Classic <c>ghp_</c> tokens are ignored.
    /// </summary>
    public static string? TryExtractCopilotTokenFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            // 1. Copilot CLI >= 1.x plaintext login format:
            //    { "copilot_tokens": { "https://github.com:login": "gho_..." }, "logged_in_users": [ ... ] }
            if (root.TryGetProperty("copilot_tokens", out var copilotTokens) &&
                copilotTokens.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("logged_in_users", out var users) &&
                    users.ValueKind == JsonValueKind.Array)
                {
                    foreach (var user in users.EnumerateArray())
                    {
                        if (user.ValueKind != JsonValueKind.Object) continue;
                        var host = user.TryGetProperty("host", out var hProp) && hProp.ValueKind == JsonValueKind.String
                            ? hProp.GetString()?.Trim().TrimEnd('/')
                            : "https://github.com";
                        var login = user.TryGetProperty("login", out var lProp) && lProp.ValueKind == JsonValueKind.String
                            ? lProp.GetString()?.Trim()
                            : null;
                        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(login))
                        {
                            var key = $"{host}:{login}";
                            if (copilotTokens.TryGetProperty(key, out var matchedTok) &&
                                matchedTok.ValueKind == JsonValueKind.String &&
                                IsSupportedCopilotToken(matchedTok.GetString()))
                            {
                                return matchedTok.GetString()!.Trim();
                            }
                        }
                    }
                }

                foreach (var prop in copilotTokens.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        IsSupportedCopilotToken(prop.Value.GetString()))
                    {
                        return prop.Value.GetString()!.Trim();
                    }
                }
            }

            // 2. Copilot CLI authTokens map:
            //    { "authTokens": { "https://github.com": { "token": "gho_..." } } }
            if (root.TryGetProperty("authTokens", out var authTokens) &&
                authTokens.ValueKind == JsonValueKind.Object)
            {
                if (authTokens.TryGetProperty("https://github.com", out var ghCom) &&
                    ghCom.ValueKind == JsonValueKind.Object)
                {
                    var ghTok = ReadFirstSupportedTokenProperty(ghCom);
                    if (ghTok != null) return ghTok;
                }

                foreach (var hostEntry in authTokens.EnumerateObject())
                {
                    if (hostEntry.Value.ValueKind == JsonValueKind.Object)
                    {
                        var tok = ReadFirstSupportedTokenProperty(hostEntry.Value);
                        if (tok != null) return tok;
                    }
                }
            }

            // 3. Top-level token fields (legacy config or direct token)
            var topLevel = ReadFirstSupportedTokenProperty(root);
            if (topLevel != null) return topLevel;

            // 4. ~/.config/github-copilot/hosts.json or apps.json:
            //    { "github.com": { "oauth_token": "ghu_..." } }
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name is "copilot_tokens" or "authTokens") continue;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var nested = ReadFirstSupportedTokenProperty(prop.Value);
                    if (nested != null) return nested;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="configJson"/> contains a Copilot CLI login entry that
    /// <c>copilot</c> CLI natively recognizes without failing credential storage lookup.
    /// When <c>logged_in_users</c> or <c>last_logged_in_user</c> is present, Copilot CLI requires a
    /// matching token in <c>copilot_tokens</c> (otherwise it ignores <c>authTokens</c> and fails with
    /// "Saved sign-in could not be read from credential storage").
    /// </summary>
    public static bool HasActiveCopilotConfigLogin(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;

            var validCopilotTokenKeys = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("copilot_tokens", out var ctObj) && ctObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ctObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        IsSupportedCopilotToken(prop.Value.GetString()))
                    {
                        validCopilotTokenKeys.Add(prop.Name);
                    }
                }
            }

            var hasLoggedInUsers = root.TryGetProperty("logged_in_users", out var usersArr) &&
                                   usersArr.ValueKind == JsonValueKind.Array &&
                                   usersArr.GetArrayLength() > 0;
            var hasLastLoggedInUser = root.TryGetProperty("last_logged_in_user", out var lastUserObj) &&
                                      lastUserObj.ValueKind == JsonValueKind.Object;

            if (hasLoggedInUsers || hasLastLoggedInUser)
            {
                if (validCopilotTokenKeys.Count == 0)
                {
                    return false;
                }

                if (hasLastLoggedInUser)
                {
                    var key = BuildUserTokenKey(lastUserObj);
                    if (key != null && validCopilotTokenKeys.Contains(key))
                    {
                        return true;
                    }
                }

                if (hasLoggedInUsers)
                {
                    foreach (var user in usersArr.EnumerateArray())
                    {
                        if (user.ValueKind != JsonValueKind.Object) continue;
                        var key = BuildUserTokenKey(user);
                        if (key != null && validCopilotTokenKeys.Contains(key))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            if (validCopilotTokenKeys.Count > 0)
            {
                return true;
            }

            if (root.TryGetProperty("authTokens", out var atObj) && atObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in atObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object &&
                        ReadFirstSupportedTokenProperty(prop.Value) != null)
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string? BuildUserTokenKey(JsonElement userObj)
    {
        var host = userObj.TryGetProperty("host", out var hProp) && hProp.ValueKind == JsonValueKind.String
            ? hProp.GetString()?.Trim().TrimEnd('/')
            : "https://github.com";
        var login = userObj.TryGetProperty("login", out var lProp) && lProp.ValueKind == JsonValueKind.String
            ? lProp.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }
        return $"{host}:{login}";
    }

    private static string? BuildUserTokenKey(JsonObject userObj)
    {
        var host = userObj.TryGetPropertyValue("host", out var hNode) &&
                   hNode is JsonValue hjv &&
                   hjv.TryGetValue<string>(out var hStr) &&
                   !string.IsNullOrWhiteSpace(hStr)
            ? hStr.Trim().TrimEnd('/')
            : "https://github.com";
        var login = userObj.TryGetPropertyValue("login", out var lNode) &&
                    lNode is JsonValue ljv &&
                    ljv.TryGetValue<string>(out var lStr)
            ? lStr.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }
        return $"{host}:{login}";
    }

    /// <summary>
    /// Removes any poisoned classic PAT (<c>ghp_...</c>) entries from a <c>~/.copilot/config.json</c>
    /// document while preserving valid Copilot tokens, <c>logged_in_users</c>, and <c>trusted_folders</c>.
    /// Also upgrades a bare legacy <c>{"token":"gho_..."}</c> into <c>authTokens</c>.
    /// </summary>
    public static string? ScrubClassicPatFromConfigJson(string? existingJson, out bool modified)
        => ScrubClassicPatFromConfigJson(existingJson, out modified, pruneOrphanedLoggedInUsers: false);

    /// <summary>
    /// Removes poisoned classic PAT (<c>ghp_...</c>) entries from <c>~/.copilot/config.json</c>, and
    /// optionally prunes orphaned <c>logged_in_users</c>/<c>last_logged_in_user</c> entries that have
    /// no matching entry in <c>copilot_tokens</c> (which in headless containers without an OS keyring
    /// cause Copilot CLI to fail with "Saved sign-in could not be read from credential storage").
    /// </summary>
    public static string? ScrubClassicPatFromConfigJson(
        string? existingJson,
        out bool modified,
        bool pruneOrphanedLoggedInUsers)
    {
        modified = false;
        if (string.IsNullOrWhiteSpace(existingJson)) return existingJson;

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(existingJson) as JsonObject;
        }
        catch (JsonException)
        {
            return existingJson;
        }

        if (obj == null) return existingJson;

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

        if (obj.TryGetPropertyValue("authTokens", out var authNode) && authNode is JsonObject authObj)
        {
            var keysToRemove = new List<string>();
            foreach (var (hostKey, hostVal) in authObj)
            {
                if (hostVal is JsonObject hostObj &&
                    hostObj.TryGetPropertyValue("token", out var tokNode) &&
                    tokNode is JsonValue tokJv &&
                    tokJv.TryGetValue<string>(out var tokStr) &&
                    IsClassicGitHubPat(tokStr))
                {
                    keysToRemove.Add(hostKey);
                }
            }
            foreach (var k in keysToRemove)
            {
                authObj.Remove(k);
                modified = true;
            }
            if (authObj.Count == 0)
            {
                obj.Remove("authTokens");
                modified = true;
            }
        }

        if (obj.TryGetPropertyValue("copilot_tokens", out var cpNode) && cpNode is JsonObject cpObj)
        {
            var keysToRemove = new List<string>();
            foreach (var (userKey, tokNode) in cpObj)
            {
                if (tokNode is JsonValue tokJv &&
                    tokJv.TryGetValue<string>(out var tokStr) &&
                    IsClassicGitHubPat(tokStr))
                {
                    keysToRemove.Add(userKey);
                }
            }
            foreach (var k in keysToRemove)
            {
                cpObj.Remove(k);
                modified = true;
            }
            if (cpObj.Count == 0)
            {
                obj.Remove("copilot_tokens");
                modified = true;
            }
        }

        if (pruneOrphanedLoggedInUsers)
        {
            var validTokenKeys = new HashSet<string>(StringComparer.Ordinal);
            if (obj.TryGetPropertyValue("copilot_tokens", out var remainingCp) && remainingCp is JsonObject remainingCpObj)
            {
                foreach (var (userKey, tokNode) in remainingCpObj)
                {
                    if (tokNode is JsonValue tokJv &&
                        tokJv.TryGetValue<string>(out var tokStr) &&
                        IsSupportedCopilotToken(tokStr))
                    {
                        validTokenKeys.Add(userKey);
                    }
                }
            }

            if (obj.TryGetPropertyValue("logged_in_users", out var usersNode) && usersNode is JsonArray usersArr)
            {
                for (var i = usersArr.Count - 1; i >= 0; i--)
                {
                    if (usersArr[i] is not JsonObject uObj ||
                        BuildUserTokenKey(uObj) is not string key ||
                        !validTokenKeys.Contains(key))
                    {
                        usersArr.RemoveAt(i);
                        modified = true;
                    }
                }

                if (usersArr.Count == 0)
                {
                    obj.Remove("logged_in_users");
                    modified = true;
                }
            }

            if (obj.TryGetPropertyValue("last_logged_in_user", out var lastNode))
            {
                if (lastNode is not JsonObject lastObj ||
                    BuildUserTokenKey(lastObj) is not string lastKey ||
                    !validTokenKeys.Contains(lastKey))
                {
                    obj.Remove("last_logged_in_user");
                    modified = true;
                }
            }
        }

        // Upgrade legacy bare {"token": "gho_..."} into authTokens so Copilot CLI 1.x recognizes it
        if (!HasActiveCopilotConfigLogin(obj.ToJsonString()) &&
            obj.TryGetPropertyValue("token", out var legacyTokNode) &&
            legacyTokNode is JsonValue legacyJv &&
            legacyJv.TryGetValue<string>(out var legacyStr) &&
            IsSupportedCopilotToken(legacyStr))
        {
            obj["authTokens"] = new JsonObject
            {
                ["https://github.com"] = new JsonObject { ["token"] = legacyStr.Trim() }
            };
            modified = true;
        }

        if (obj.Count == 0)
        {
            modified = true;
            return null;
        }

        return obj.ToJsonString();
    }

    /// <summary>
    /// Merges a supported Copilot token into <c>~/.copilot/config.json</c> without clobbering
    /// <c>trusted_folders</c>. Orphaned <c>logged_in_users</c> entries without matching
    /// <c>copilot_tokens</c> are always pruned so Copilot CLI never fails credential storage lookup.
    /// When <paramref name="overwriteExisting"/> is false, an existing valid interactive
    /// <c>copilot_tokens</c> login is preserved untouched; seeded <c>authTokens</c> is kept in sync.
    /// </summary>
    public static string? MergeCopilotTokenIntoConfigJson(
        string? existingJson,
        string copilotToken,
        bool overwriteExisting = false)
    {
        var scrubbed = ScrubClassicPatFromConfigJson(existingJson, out _, pruneOrphanedLoggedInUsers: true);
        if (!IsSupportedCopilotToken(copilotToken))
        {
            return scrubbed;
        }

        var cleanToken = copilotToken.Trim();
        JsonObject obj;
        try
        {
            obj = (!string.IsNullOrWhiteSpace(scrubbed) ? JsonNode.Parse(scrubbed) as JsonObject : null)
                  ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }

        if (!overwriteExisting && HasValidInteractiveCopilotTokens(obj))
        {
            return obj.ToJsonString();
        }

        if (overwriteExisting)
        {
            var tokenAlreadyActiveInCopilotTokens = false;
            if (obj.TryGetPropertyValue("copilot_tokens", out var cpNode) && cpNode is JsonObject cpObj)
            {
                foreach (var (_, valNode) in cpObj)
                {
                    if (valNode is JsonValue jv &&
                        jv.TryGetValue<string>(out var s) &&
                        string.Equals(s.Trim(), cleanToken, StringComparison.Ordinal))
                    {
                        tokenAlreadyActiveInCopilotTokens = true;
                        break;
                    }
                }
            }

            if (!tokenAlreadyActiveInCopilotTokens)
            {
                obj.Remove("copilot_tokens");
                obj.Remove("logged_in_users");
                obj.Remove("last_logged_in_user");
                obj.Remove("token");
            }
        }

        var authObj = obj["authTokens"] as JsonObject ?? new JsonObject();
        authObj["https://github.com"] = new JsonObject
        {
            ["token"] = cleanToken
        };
        obj["authTokens"] = authObj;
        return obj.ToJsonString();
    }

    private static bool HasValidInteractiveCopilotTokens(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("copilot_tokens", out var cpNode) || cpNode is not JsonObject cpObj)
        {
            return false;
        }

        foreach (var (_, valNode) in cpObj)
        {
            if (valNode is JsonValue jv &&
                jv.TryGetValue<string>(out var s) &&
                IsSupportedCopilotToken(s))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ensures <c>~/.copilot/settings.json</c> sets <c>storeTokenPlaintext: true</c> so
    /// <c>copilot login --device-code</c> persists tokens in headless containers without an OS keyring.
    /// </summary>
    public static string EnsurePlaintextStorageSettingsJson(string? existingSettingsJson, out bool modified)
    {
        modified = false;
        JsonObject? obj = null;
        if (!string.IsNullOrWhiteSpace(existingSettingsJson))
        {
            try
            {
                obj = JsonNode.Parse(existingSettingsJson) as JsonObject;
            }
            catch (JsonException)
            {
                obj = null;
            }
        }

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
    public static string MergeOAuthTokenIntoDriverCapabilitiesJson(string? existingCapabilitiesJson, string oauthToken)
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
        // (or a non-ghp_ top-level token).
        if (HasActiveCopilotConfigLogin(copilotConfig))
        {
            var tok = TryExtractCopilotTokenFromJson(copilotConfig);
            if (tok != null) return tok;
        }

        var hostsTok = TryExtractCopilotTokenFromJson(copilotHosts);
        if (hostsTok != null) return hostsTok;

        var appsTok = TryExtractCopilotTokenFromJson(copilotApps);
        if (appsTok != null) return appsTok;

        var sanitizedEnv = SanitizeCopilotEnvToken(copilotEnv, personaGhToken);
        if (sanitizedEnv != null) return sanitizedEnv;

        // Fallback to legacy bare top-level token in config.json if supported
        return TryExtractCopilotTokenFromJson(copilotConfig);
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
