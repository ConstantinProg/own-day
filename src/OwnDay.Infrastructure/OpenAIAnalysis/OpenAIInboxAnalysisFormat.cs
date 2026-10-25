namespace OwnDay.Infrastructure.OpenAIAnalysis;

public static class OpenAIInboxAnalysisFormat
{
    public const string SchemaVersion = "inbox-v1";
    public const string PromptVersion = "inbox-analysis-v3";

    public static string SystemPrompt { get; } = GetPrompt(PromptVersion);

    // The allowlist also makes historical prompts available to the explicit evaluation tool.
    public static string GetPrompt(string version)
    {
        if (version is not ("inbox-analysis-v1" or "inbox-analysis-v2" or "inbox-analysis-v3"))
        {
            throw new ArgumentException("Unknown Inbox prompt version.", nameof(version));
        }

        using var stream = typeof(OpenAIInboxAnalysisFormat).Assembly.GetManifestResourceStream(
            $"OwnDay.Infrastructure.OpenAIAnalysis.Prompts.{version}.txt")!;
        using var reader = new StreamReader(stream);
        // Keep versioned prompt bytes stable across LF/CRLF checkouts.
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    public const string Schema = """
        {
          "type": "object", "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object", "additionalProperties": false,
                "required": ["kind", "title", "description", "properties", "projectIds", "ambiguities"],
                "properties": {
                  "kind": { "type": "string", "enum": ["Action", "Project", "WaitingFor", "Reference", "SomedayMaybe"] },
                  "title": { "type": "string" },
                  "description": { "type": ["string", "null"] },
                  "properties": {
                    "type": "object", "additionalProperties": false,
                    "required": ["source", "waitingSince", "waitingSinceEvidence", "unresolvedDateText"],
                    "properties": {
                      "source": { "type": ["string", "null"] },
                      "waitingSince": { "type": ["string", "null"] },
                      "waitingSinceEvidence": { "type": ["string", "null"] },
                      "unresolvedDateText": { "type": ["string", "null"] }
                    }
                  },
                  "projectIds": { "type": "array", "items": { "type": "integer" } },
                  "ambiguities": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """;
}
