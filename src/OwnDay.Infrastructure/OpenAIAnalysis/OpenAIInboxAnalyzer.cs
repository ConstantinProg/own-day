using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed class OpenAIInboxAnalyzer(ChatClient client, IOptions<OpenAIInboxAnalysisOptions> options) : IInboxAnalyzer
{
    public InboxAnalyzerDescriptor Descriptor { get; } = new(options.Value.Model,
        OpenAIInboxAnalysisFormat.SchemaVersion, OpenAIInboxAnalysisFormat.PromptVersion);

    public async Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(request, InboxAnalysisJson.Options);
        if (input.Length > 48_000)
        {
            throw new InboxAnalyzerException("context_limit");
        }

        var completionOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = options.Value.MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("inbox_analysis",
                BinaryData.FromString(OpenAIInboxAnalysisFormat.Schema), jsonSchemaIsStrict: true)
        };

        ChatCompletion completion;
        try
        {
            completion = await client.CompleteChatAsync(
                [new SystemChatMessage(OpenAIInboxAnalysisFormat.SystemPrompt), new UserChatMessage(input)],
                completionOptions, cancellationToken);
        }
        catch (ClientResultException)
        {
            throw new InboxAnalyzerException("provider_failure");
        }
        catch (HttpRequestException)
        {
            throw new InboxAnalyzerException("provider_failure");
        }
        catch (JsonException)
        {
            throw new InboxAnalyzerException("provider_failure");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InboxAnalyzerException("timeout");
        }

        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            throw new InboxAnalyzerException("refused");
        }

        if (completion.FinishReason != ChatFinishReason.Stop || completion.Content.Count != 1 ||
            completion.Content[0].Kind != ChatMessageContentPartKind.Text)
        {
            throw new InboxAnalyzerException("incomplete_response");
        }

        return new(completion.Content[0].Text, completion.Model, completion.Id,
            completion.Usage?.InputTokenCount, completion.Usage?.OutputTokenCount);
    }
}
