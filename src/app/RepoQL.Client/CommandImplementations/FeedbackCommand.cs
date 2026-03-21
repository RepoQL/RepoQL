using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Feedback;
using RepoQL.ConsoleApp.Tools;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Let agents provide feedback on their experience with RepoQL.
/// Complexity: Gathers session ID and diagnostics, sends to cloud (local fallback).
/// </summary>
[CommandClass]
internal sealed class FeedbackCommand(SessionInfo session, SelfTestRunner runner, FeedbackStore store)
{
    [Command("feedback", Description = "Submit feedback about your experience with RepoQL")]
    public async Task<CommandResult> Execute(
        [CommandParam("Your feedback text")] string feedback,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(feedback))
            return CommandResult.Error("Feedback text is required.");

        string diagnostics;
        try
        {
            diagnostics = await runner.RunAsync(DiagnosticCollectionMode.Fast, cancel);
        }
        catch (Exception ex)
        {
            diagnostics = $"Diagnostics unavailable: {ex.Message}";
        }

        try
        {
            await store.SubmitAsync(session.SessionId, feedback.Trim(), diagnostics, cancel);
            return CommandResult.Success("Feedback recorded — thank you.");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to submit feedback: {ex.Message}");
        }
    }
}
