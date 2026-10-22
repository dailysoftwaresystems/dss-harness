using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The one reader of which text each template key of a step holds: the definition's check reads them, and the engine
/// fills them where a step starts - every key a type marks, a pause's question left to its asking.
/// </summary>
public sealed class FlowStepTemplatesTests
{
    private static WorkflowStep Step(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", $"""
            name: flow
            steps:
            {text}
            """).Steps.Single();

    private static string? Upper(string template, out string? problem)
    {
        problem = null;
        return template.ToUpperInvariant();
    }

    /// <summary>Every key a step type marks a template is read by its texts, whatever type marks it: none is left unread.</summary>
    [Fact]
    public void EveryKeyATypeMarks_IsReadByItsTexts()
    {
        foreach (var type in WorkflowStepTypes.All)
        {
            foreach (var key in type.TemplateKeys)
            {
                Assert.Empty(FlowStepTemplates.Texts(new WorkflowStep { Name = "x", Type = type.Type }, key));
            }
        }
    }

    /// <summary>
    /// Each template key is filled where the step starts - a scalar whole, a list member by member, a map value by value,
    /// in the order the definition gives them - and a pause's question is left as written, for the pause fills it as it
    /// asks; the keys that are no templates are left as written too.
    /// </summary>
    [Fact]
    public void EachTemplateKey_IsFilled_AndAPausesQuestionIsLeftToItsAsking()
    {
        var commit = FlowStepTemplates.Fill(Step("""
              - name: save
                type: commit
                message: 'the {input.what}'
                variables: { scope: '{input.scope}', kind: fix }
            """), Upper, out _, out _)!;
        var pullRequest = FlowStepTemplates.Fill(Step("""
              - name: open
                type: pull-request
                title: 'add {input.what}'
                body: body.md
                base: '{input.base}'
            """), Upper, out _, out _)!;
        var anchors = FlowStepTemplates.Fill(Step("""
              - name: balance
                type: anchor-balance
                base: '{input.base}'
            """), Upper, out _, out _)!;
        var ci = FlowStepTemplates.Fill(Step("""
              - name: ci
                type: ci-read
                branch: '{input.branch}'
            """), Upper, out _, out _)!;
        var wait = FlowStepTemplates.Fill(Step("""
              - name: wait
                type: ci-wait
                commit: '{input.sha}'
            """), Upper, out _, out _)!;
        var pause = FlowStepTemplates.Fill(Step("""
              - name: ask
                type: pause
                question: 'go on with {input.what}?'
            """), Upper, out _, out _)!;
        var run = FlowStepTemplates.Fill(Step("""
              - name: shape
                type: run
                runner: tidy
                inputs: { first: '{input.a}', second: '{input.b}' }
            """), Upper, out _, out _)!;

        Assert.Equal("THE {INPUT.WHAT}", commit.Message);
        Assert.Equal(["scope", "kind"], commit.Variables.Keys);
        Assert.Equal(["{INPUT.SCOPE}", "FIX"], commit.Variables.Values);
        Assert.Equal(("ADD {INPUT.WHAT}", "{INPUT.BASE}", "body.md"), (pullRequest.Title, pullRequest.Base, pullRequest.Body));
        Assert.Equal("{INPUT.BASE}", anchors.Base);
        Assert.Equal("{INPUT.BRANCH}", ci.Branch);
        Assert.Equal("{INPUT.SHA}", wait.Commit);
        Assert.Equal("go on with {input.what}?", pause.Question);
        Assert.Equal(("tidy", "{INPUT.A}", "{INPUT.B}"), (run.Runner, run.Inputs["first"], run.Inputs["second"]));
    }

    /// <summary>
    /// A key whose text cannot be filled is named with why, and nothing of the step is handed on; and a test's filter,
    /// exclusion or label that fills to nothing is not given.
    /// </summary>
    [Fact]
    public void AKeyThatCannotBeFilled_IsNamed_AndOneFilledToNothingIsNotGiven()
    {
        var step = Step("""
              - name: verify
                type: test
                filter: '{input.filter}'
                exclude: ['{input.skip}', slow]
                labels: ['{input.label}']
            """);

        Assert.Null(FlowStepTemplates.Fill(step, Unreadable, out var key, out var problem));
        Assert.Equal(("exclude", "'{input.skip}' reads nothing"), (key, problem));

        var empty = FlowStepTemplates.Fill(step, Empty, out key, out problem)!;

        Assert.Equal((null, null), (key, problem));
        Assert.Null(empty.Filter);
        Assert.Equal(["slow"], empty.Exclude);
        Assert.Empty(empty.Labels);

        static string? Unreadable(string template, out string? problem)
        {
            problem = template == "{input.skip}" ? $"'{template}' reads nothing" : null;
            return problem is null ? template : null;
        }

        static string? Empty(string template, out string? problem)
        {
            problem = null;
            return template.StartsWith('{') ? string.Empty : template;
        }
    }
}
