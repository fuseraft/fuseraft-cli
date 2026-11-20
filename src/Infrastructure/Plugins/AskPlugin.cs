using System.ComponentModel;
using fuseraft.Core.Interfaces;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// ask_user: a multiple-choice question for the user, answered through <see cref="IHumanApprovalService.PromptQuestionAsync"/>.
/// Only registered where <see cref="IHumanApprovalService.CanAskQuestions"/> is true, and never given to subagents.
/// </summary>
public sealed class AskPlugin(IHumanApprovalService person)
{
    public const int MaxOptions = 8;

    [Description(
        "Ask the user a multiple-choice question. Only for a decision that is genuinely theirs (preference, intent), " +
        "not facts you can look up or permission, which tools ask for themselves.")]
    public async Task<string> UserAsync(
        [Description("The question, as one clear sentence.")] string question,
        [Description("2 to 8 short options, recommended first.")] string[] options,
        [Description("Let the user type an answer that isn't in the options.")] bool allow_other = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "[ERROR] question is empty.";
        var choices = (options ?? []).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).Distinct().ToList();
        if (choices.Count is < 2 or > MaxOptions)
            return $"[ERROR] Give between 2 and {MaxOptions} distinct options; got {choices.Count}.";

        var answer = (await person.PromptQuestionAsync(question.Trim(), choices, allow_other, ct))?.Trim();
        if (string.IsNullOrEmpty(answer) || (!allow_other && !choices.Contains(answer)))
            return "The user dismissed the question without answering. Don't ask it again; continue with your best judgment " +
                   "and say what you assumed, or stop and explain what you need.";
        return choices.Contains(answer) ? $"The user chose: {answer}" : $"The user answered: {answer}";
    }
}
