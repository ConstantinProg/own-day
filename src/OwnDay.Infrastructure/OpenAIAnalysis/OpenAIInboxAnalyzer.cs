using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class OpenAIInboxAnalyzer(ChatClient client, IOptions<OpenAIInboxAnalysisOptions> options) : IInboxAnalyzer
{
    public InboxAnalyzerDescriptor Descriptor { get; } = new(options.Value.Model,
        OpenAIInboxAnalysisFormat.SchemaVersion, OpenAIInboxAnalysisFormat.PromptVersion,
        options.Value.EffectiveReasoningEffort, options.Value.MaxOutputTokens, options.Value.TimeoutSeconds);

    public async Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(request, InboxAnalysisJson.Options);
        if (input.Length > 48_000)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.ContextLimit);
        }

        // The SDK's typed reasoning property is experimental. The public
        // model reader supports this documented wire field without experimental APIs.
        var completionOptions = Descriptor.ReasoningEffort is { } effort
            ? ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString(
                JsonSerializer.Serialize(new { reasoning_effort = effort })))!
            : new ChatCompletionOptions();
        completionOptions.MaxOutputTokenCount = Descriptor.MaxOutputTokens;
        completionOptions.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("inbox_analysis",
            BinaryData.FromString(OpenAIInboxAnalysisFormat.Schema), jsonSchemaIsStrict: true);

        ChatCompletion completion;
        try
        {
            completion = await client.CompleteChatAsync(
                [new SystemChatMessage(OpenAIInboxAnalysisFormat.SystemPrompt), new UserChatMessage(input)],
                completionOptions, cancellationToken);
        }
        catch (ClientResultException exception)
        {
            throw new InboxAnalyzerException(Classify(exception));
        }
        catch (HttpRequestException)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.ProviderFailure);
        }
        catch (JsonException)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.InvalidResponse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.Timeout);
        }

        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.Refused);
        }

        if (completion.FinishReason != ChatFinishReason.Stop || completion.Content.Count != 1 ||
            completion.Content[0].Kind != ChatMessageContentPartKind.Text)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.IncompleteResponse);
        }

        return new(completion.Content[0].Text, completion.Model, completion.Id,
            completion.Usage?.InputTokenCount, completion.Usage?.OutputTokenCount);
    }

    private static InboxAnalysisCode Classify(ClientResultException exception)
    {
        if (exception.Status == 429)
        {
            // Inspect only a known error code. Never retain or log the provider's body/message.
            try
            {
                var content = exception.GetRawResponse()?.Content;
                if (content is not null)
                {
                    using var json = JsonDocument.Parse(content);
                    if (json.RootElement.ValueKind == JsonValueKind.Object &&
                        json.RootElement.TryGetProperty("error", out var error) &&
                        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        code.GetString() is "insufficient_quota" or "billing_hard_limit_reached")
                    {
                        return InboxAnalysisCode.BudgetLimit;
                    }
                }
            }
            catch (JsonException) { }

            return InboxAnalysisCode.RateLimit;
        }

        return exception.Status switch
        {
            401 or 403 => InboxAnalysisCode.Configuration,
            0 or 408 or 409 => InboxAnalysisCode.ProviderFailure,
            >= 500 => InboxAnalysisCode.ProviderFailure,
            _ => InboxAnalysisCode.PermanentFailure
        };
    }
}
