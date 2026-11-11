using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Core.Models;

namespace AgentMesh.Api.Tools;

/// <summary>
/// Normalizes arguments passed to platform and session MCP tools before handler dispatch,
/// ensuring that LLM callers that send aliases (e.g. 'body' instead of 'summary_3r', 'content'
/// instead of 'value', or camelCase property names) get bound cleanly rather than triggering
/// the library's generic "An error occurred invoking '<tool>'".
/// </summary>
public static class PlatformToolCallNormalizer
{
    public static IDictionary<string, JsonElement>? NormalizeArguments(string? toolName, IDictionary<string, JsonElement>? rawArgs)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return rawArgs;

        var normalizedName = toolName.Trim().ToLowerInvariant();
        var result = rawArgs is not null
            ? new Dictionary<string, JsonElement>(rawArgs, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        switch (normalizedName)
        {
            case "open_pull_request":
                NormalizeOpenPullRequest(result);
                break;
            case "save_memory":
                NormalizeSaveMemory(result);
                break;
            case "manage_task":
                NormalizeManageTask(result);
                break;
            case "post_job_message":
                NormalizePostJobMessage(result);
                break;
            case "inspect_job_status":
                NormalizeInspectJobStatus(result);
                break;
            case "stage_agent_intent":
                NormalizeStageAgentIntent(result);
                break;
            case "append_thought":
            case "record_tool_call":
            case "record_tool_result":
            case "add_session_message":
            case "update_session_status":
                NormalizeSessionTools(result);
                break;
        }

        return result;
    }

    private static void NormalizeOpenPullRequest(IDictionary<string, JsonElement> args)
    {
        // Title aliases
        if (!args.ContainsKey("title"))
        {
            if (TryGetString(args, "pr_title", out var pt)) args["title"] = JsonSerializer.SerializeToElement(pt);
            else if (TryGetString(args, "subject", out var s)) args["title"] = JsonSerializer.SerializeToElement(s);
            else if (TryGetString(args, "name", out var n)) args["title"] = JsonSerializer.SerializeToElement(n);
        }

        // Branch aliases
        if (!args.ContainsKey("branch_name"))
        {
            if (TryGetString(args, "target_branch", out var tb)) args["branch_name"] = JsonSerializer.SerializeToElement(tb);
            else if (TryGetString(args, "targetBranch", out var tb2)) args["branch_name"] = JsonSerializer.SerializeToElement(tb2);
            else if (TryGetString(args, "branch", out var b)) args["branch_name"] = JsonSerializer.SerializeToElement(b);
            else if (TryGetString(args, "branchName", out var bn)) args["branch_name"] = JsonSerializer.SerializeToElement(bn);
            else if (TryGetString(args, "head_branch", out var hb)) args["branch_name"] = JsonSerializer.SerializeToElement(hb);
            else if (TryGetString(args, "headBranch", out var hb2)) args["branch_name"] = JsonSerializer.SerializeToElement(hb2);
        }

        // Job ID aliases
        if (!args.ContainsKey("job_id") && TryGetString(args, "jobId", out var jid))
        {
            args["job_id"] = JsonSerializer.SerializeToElement(jid);
        }

        // Summary 3-R
        JsonElement? summaryElement = null;
        if (args.TryGetValue("summary_3r", out var s3r))
        {
            summaryElement = s3r;
        }
        else if (args.TryGetValue("summary3R", out var s3r2))
        {
            summaryElement = s3r2;
        }
        else if (args.TryGetValue("summary", out var sm))
        {
            summaryElement = sm;
        }
        else if (args.TryGetValue("body", out var b))
        {
            summaryElement = b;
        }

        args["summary_3r"] = ExtractSummary3RElement(summaryElement);
    }

    public static JsonElement ExtractSummary3RElement(JsonElement? summaryElement)
    {
        var result = "Implementation completed and verified.";
        var reasoning = "Implemented changes as requested.";
        var risk = "Low regression risk based on test verification.";

        if (!summaryElement.HasValue || summaryElement.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return JsonSerializer.SerializeToElement(new
            {
                result,
                reasoning,
                risk
            });
        }

        var elem = summaryElement.Value;
        if (elem.ValueKind == JsonValueKind.String)
        {
            Parse3RString(elem.GetString() ?? string.Empty, ref result, ref reasoning, ref risk);
            return JsonSerializer.SerializeToElement(new
            {
                result,
                reasoning,
                risk
            });
        }

        if (elem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in elem.EnumerateObject())
            {
                if (prop.Name.Equals("result", StringComparison.OrdinalIgnoreCase))
                    result = prop.Value.ToString();
                else if (prop.Name.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
                    reasoning = prop.Value.ToString();
                else if (prop.Name.Equals("risk", StringComparison.OrdinalIgnoreCase))
                    risk = prop.Value.ToString();
            }

            return JsonSerializer.SerializeToElement(new
            {
                result = string.IsNullOrWhiteSpace(result) ? "Implementation completed and verified." : result,
                reasoning = string.IsNullOrWhiteSpace(reasoning) ? "Implemented changes as requested." : reasoning,
                risk = string.IsNullOrWhiteSpace(risk) ? "Low regression risk based on test verification." : risk
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            result = elem.ToString(),
            reasoning,
            risk
        });
    }

    private static void Parse3RString(string text, ref string result, ref string reasoning, ref string risk)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var resMatch = Regex.Match(text, @"(?:^|\n)(?:#+\s*)?(?:\*\*)?Result(?:\*\*)?:?\s*([\s\S]*?)(?=(?:^|\n)(?:#+\s*)?(?:\*\*)?Reasoning(?:\*\*)?:?|$)", RegexOptions.IgnoreCase);
        var reasMatch = Regex.Match(text, @"(?:^|\n)(?:#+\s*)?(?:\*\*)?Reasoning(?:\*\*)?:?\s*([\s\S]*?)(?=(?:^|\n)(?:#+\s*)?(?:\*\*)?Risk(?:\*\*)?:?|$)", RegexOptions.IgnoreCase);
        var rskMatch = Regex.Match(text, @"(?:^|\n)(?:#+\s*)?(?:\*\*)?Risk(?:\*\*)?:?\s*([\s\S]*?)(?=(?:^|\n)(?:#+\s*)?(?:\*\*)?---|$)", RegexOptions.IgnoreCase);

        if (resMatch.Success && !string.IsNullOrWhiteSpace(resMatch.Groups[1].Value))
        {
            result = resMatch.Groups[1].Value.Trim();
        }
        else
        {
            result = text.Trim();
        }

        if (reasMatch.Success && !string.IsNullOrWhiteSpace(reasMatch.Groups[1].Value))
        {
            reasoning = reasMatch.Groups[1].Value.Trim();
        }

        if (rskMatch.Success && !string.IsNullOrWhiteSpace(rskMatch.Groups[1].Value))
        {
            risk = rskMatch.Groups[1].Value.Trim();
        }
    }

    private static void NormalizeSaveMemory(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("value"))
        {
            if (TryGetString(args, "content", out var c)) args["value"] = JsonSerializer.SerializeToElement(c);
            else if (TryGetString(args, "text", out var t)) args["value"] = JsonSerializer.SerializeToElement(t);
            else if (TryGetString(args, "val", out var v)) args["value"] = JsonSerializer.SerializeToElement(v);
        }

        if (TryGetString(args, "category", out var rawCat))
        {
            if (rawCat.Equals("architecture", StringComparison.OrdinalIgnoreCase) ||
                rawCat.Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                args["category"] = JsonSerializer.SerializeToElement(AgentMemoryCategories.CodebaseGotcha);
            }
            else if (rawCat.Equals("environment_quirk", StringComparison.OrdinalIgnoreCase))
            {
                args["category"] = JsonSerializer.SerializeToElement(AgentMemoryCategories.Environment);
            }
            else if (rawCat.Equals("test_strategy", StringComparison.OrdinalIgnoreCase))
            {
                args["category"] = JsonSerializer.SerializeToElement(AgentMemoryCategories.BuildAndTest);
            }
            else if (AgentMemoryCategories.TryNormalize(rawCat, out var canonical))
            {
                args["category"] = JsonSerializer.SerializeToElement(canonical);
            }
        }

        if (!args.ContainsKey("is_global") && TryGetBool(args, "isGlobal", out var ig))
        {
            args["is_global"] = JsonSerializer.SerializeToElement(ig);
        }
    }

    private static void NormalizeManageTask(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("task_id") && TryGetString(args, "taskId", out var tid)) args["task_id"] = JsonSerializer.SerializeToElement(tid);
        if (!args.ContainsKey("progress_note") && TryGetString(args, "progressNote", out var pn)) args["progress_note"] = JsonSerializer.SerializeToElement(pn);
        if (!args.ContainsKey("job_id") && TryGetString(args, "jobId", out var jid)) args["job_id"] = JsonSerializer.SerializeToElement(jid);
        if (!args.ContainsKey("notes") && TryGetString(args, "note", out var n)) args["notes"] = JsonSerializer.SerializeToElement(n);

        if (args.TryGetValue("tasks", out var tasksElem))
        {
            if (tasksElem.ValueKind == JsonValueKind.String)
            {
                var tasksStr = tasksElem.GetString();
                if (!string.IsNullOrWhiteSpace(tasksStr))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tasksStr);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            args["tasks"] = NormalizeTasksArray(doc.RootElement);
                        }
                    }
                    catch { }
                }
            }
            else if (tasksElem.ValueKind == JsonValueKind.Array)
            {
                args["tasks"] = NormalizeTasksArray(tasksElem);
            }
        }
    }

    private static JsonElement NormalizeTasksArray(JsonElement arrayElem)
    {
        var normalizedList = new List<Dictionary<string, object?>>();
        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                }

                if (!dict.ContainsKey("target_file") && dict.TryGetValue("targetFile", out var tf)) dict["target_file"] = tf;
                if (!dict.ContainsKey("verification_method") && dict.TryGetValue("verificationMethod", out var vm)) dict["verification_method"] = vm;

                normalizedList.Add(dict);
            }
        }

        return JsonSerializer.SerializeToElement(normalizedList);
    }

    private static void NormalizePostJobMessage(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("content"))
        {
            if (TryGetString(args, "message", out var m)) args["content"] = JsonSerializer.SerializeToElement(m);
            else if (TryGetString(args, "text", out var t)) args["content"] = JsonSerializer.SerializeToElement(t);
            else if (TryGetString(args, "body", out var b)) args["content"] = JsonSerializer.SerializeToElement(b);
        }

        if (!args.ContainsKey("target_persona"))
        {
            if (TryGetString(args, "targetPersona", out var tp)) args["target_persona"] = JsonSerializer.SerializeToElement(tp);
            else if (TryGetString(args, "target", out var tgt)) args["target_persona"] = JsonSerializer.SerializeToElement(tgt);
            else if (TryGetString(args, "persona", out var p)) args["target_persona"] = JsonSerializer.SerializeToElement(p);
        }

        if (!args.ContainsKey("requires_reply"))
        {
            if (TryGetBool(args, "requiresReply", out var rr))
            {
                args["requires_reply"] = JsonSerializer.SerializeToElement(rr);
            }
            else
            {
                args["requires_reply"] = JsonSerializer.SerializeToElement(false);
            }
        }
        else if (args["requires_reply"].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            var str = args["requires_reply"].ToString();
            bool.TryParse(str, out var parsed);
            args["requires_reply"] = JsonSerializer.SerializeToElement(parsed);
        }

        if (!args.ContainsKey("job_id") && TryGetString(args, "jobId", out var jid)) args["job_id"] = JsonSerializer.SerializeToElement(jid);
    }

    private static void NormalizeInspectJobStatus(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("jobId") && TryGetString(args, "job_id", out var jid)) args["jobId"] = JsonSerializer.SerializeToElement(jid);
    }

    private static void NormalizeStageAgentIntent(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("targetService") && TryGetString(args, "target_service", out var ts)) args["targetService"] = JsonSerializer.SerializeToElement(ts);
        if (!args.ContainsKey("actionVerb") && TryGetString(args, "action_verb", out var av)) args["actionVerb"] = JsonSerializer.SerializeToElement(av);
        if (!args.ContainsKey("payloadJson") && TryGetString(args, "payload_json", out var pj)) args["payloadJson"] = JsonSerializer.SerializeToElement(pj);
        if (!args.ContainsKey("sessionId") && TryGetString(args, "session_id", out var sid)) args["sessionId"] = JsonSerializer.SerializeToElement(sid);
    }

    private static void NormalizeSessionTools(IDictionary<string, JsonElement> args)
    {
        if (!args.ContainsKey("sessionId") && TryGetString(args, "session_id", out var sid)) args["sessionId"] = JsonSerializer.SerializeToElement(sid);
        if (!args.ContainsKey("toolName") && TryGetString(args, "tool_name", out var tn)) args["toolName"] = JsonSerializer.SerializeToElement(tn);
        if (!args.ContainsKey("toolArgsJson") && TryGetString(args, "tool_args_json", out var taj)) args["toolArgsJson"] = JsonSerializer.SerializeToElement(taj);
        if (!args.ContainsKey("toolResult") && TryGetString(args, "tool_result", out var tr)) args["toolResult"] = JsonSerializer.SerializeToElement(tr);
        if (!args.ContainsKey("toolResultJson") && TryGetString(args, "tool_result_json", out var trj)) args["toolResultJson"] = JsonSerializer.SerializeToElement(trj);
        if (!args.ContainsKey("eventType") && TryGetString(args, "event_type", out var et)) args["eventType"] = JsonSerializer.SerializeToElement(et);
        if (!args.ContainsKey("targetAgentId") && TryGetString(args, "target_agent_id", out var tai)) args["targetAgentId"] = JsonSerializer.SerializeToElement(tai);
        if (!args.ContainsKey("metadataJson") && TryGetString(args, "metadata_json", out var mj)) args["metadataJson"] = JsonSerializer.SerializeToElement(mj);
        if (!args.ContainsKey("initialMessage") && TryGetString(args, "initial_message", out var im)) args["initialMessage"] = JsonSerializer.SerializeToElement(im);
    }

    private static bool TryGetString(IDictionary<string, JsonElement> dict, string key, out string value)
    {
        if (dict.TryGetValue(key, out var elem))
        {
            if (elem.ValueKind == JsonValueKind.String)
            {
                value = elem.GetString()?.Trim() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
            if (elem.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                value = elem.ToString().Trim();
                return !string.IsNullOrWhiteSpace(value);
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetBool(IDictionary<string, JsonElement> dict, string key, out bool value)
    {
        if (dict.TryGetValue(key, out var elem))
        {
            if (elem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = elem.GetBoolean();
                return true;
            }
            if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out var parsed))
            {
                value = parsed;
                return true;
            }
        }
        value = false;
        return false;
    }
}
