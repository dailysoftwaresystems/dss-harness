namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What one answer grants past a step's own bound.</summary>
public enum FlowGrant
{
    /// <summary>One more iteration of the repeat the question is about.</summary>
    Iteration,

    /// <summary>One more visit of the handler the question names, in the scope its source ended in.</summary>
    Visit,

    /// <summary>One more visit of the recovery, for the step the question is about.</summary>
    Recovery,
}

/// <summary>
/// What a run's bounds were granted past their own, and where the records keep it: each answer that grants one more -
/// a repeat's <c>more</c> one more iteration of that repeat, a handler's <c>more</c> one more visit of that handler in
/// the scope its source ended in, the recovery's <c>more</c> or <c>direct</c> one more visit of it - every one of them
/// in <c>questions.json</c>, and each iteration a plan update added to a loop at its bound, in the update's entry. The
/// one owner the engine applies an answer by and counts the answers back by at a resume, and the facts count what is
/// left by, so the most they say is left is what the engine allows.
/// </summary>
public static class FlowGrants
{
    /// <summary>The kinds of question whose answers can grant past a bound.</summary>
    public static readonly IReadOnlyList<string> Granting = ["loopBound", "visitBound", "recoveryBound"];

    /// <summary>What <paramref name="answer"/> to a question of <paramref name="kind"/> grants past a bound; null where it grants nothing.</summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="answer">The answer, as the question's options spell it.</param>
    public static FlowGrant? Of(string kind, string answer)
        => (kind, answer) switch
        {
            ("loopBound", "more") => FlowGrant.Iteration,
            ("visitBound", "more") => FlowGrant.Visit,
            ("recoveryBound", "more" or "direct") => FlowGrant.Recovery,
            _ => null,
        };

    /// <summary>
    /// Whether <paramref name="update"/> added an iteration to the repeat instance <paramref name="loop"/>: its
    /// <c>addedIteration</c> is one of that loop's own, <c>&lt;loop&gt;#&lt;n&gt;</c>.
    /// </summary>
    /// <param name="update">The plan update's entry.</param>
    /// <param name="loop">The repeat's own instance id.</param>
    public static bool AddedTo(FlowUpdateEntry update, string loop)
    {
        ArgumentNullException.ThrowIfNull(update);

        return update.AddedIteration is { } added
            && added.LastIndexOf('#') is > 0 and var at
            && string.Equals(added[..at], loop, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every grant the answers <paramref name="questions"/> holds gave, in the order given: the question, the instance it
    /// is about, what the answer grants, and the handler a visit is of.
    /// </summary>
    /// <param name="questions">The run's questions and answers; null where it holds none.</param>
    public static IEnumerable<(string Question, string About, FlowGrant Grant, string? Handler)> In(FlowQuestionsFile? questions)
    {
        foreach (var asked in questions?.Questions ?? [])
        {
            if (asked.RaisedBy?.Step is not { } about)
            {
                continue;
            }

            foreach (var given in asked.Answers)
            {
                if (Of(asked.Kind, given.Answer) is { } grant)
                {
                    yield return (asked.Id, about, grant, asked.Handler);
                }
            }
        }
    }

    /// <summary>
    /// How many iterations past its own bound the repeat instance <paramref name="loop"/> was granted: each <c>more</c>
    /// its bound was answered, and each iteration a plan update added to it.
    /// </summary>
    /// <param name="loop">The repeat's own instance id.</param>
    /// <param name="questions">The run's questions and answers; null where it holds none.</param>
    /// <param name="updates">The run's plan updates; null where it holds none.</param>
    public static int IterationsOf(string loop, FlowQuestionsFile? questions, FlowUpdatesFile? updates)
        => In(questions).Count(granted => granted.Grant == FlowGrant.Iteration && string.Equals(granted.About, loop, StringComparison.Ordinal))
            + (updates?.Updates ?? []).Count(update => AddedTo(update, loop));
}
