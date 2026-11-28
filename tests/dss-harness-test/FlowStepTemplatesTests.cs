using System.Text.Json.Nodes;
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
        var reader = FlowStepTemplates.Fill(Step("""
              - name: triage
                type: reason
                agent: triage
                commit: '{orient.startCommit}'
                prompt: prompts/triage.md
                outputs: schemas/triage.schema.json
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

        // A reason step's commit - the one its tree starts at - is filled as a CI wait's is; its files are not templates.
        Assert.Equal(("{ORIENT.STARTCOMMIT}", "triage", "prompts/triage.md"), (reader.Commit, reader.Agent, reader.Prompt));
        Assert.Equal("go on with {input.what}?", pause.Question);
        Assert.Equal(("tidy", "{INPUT.A}", "{INPUT.B}"), (run.Runner, run.Inputs["first"], run.Inputs["second"]));
    }

    /// <summary>
    /// A key whose text cannot be filled is named with why, and nothing of the step is handed on; a test's exclusion or
    /// label that fills to nothing is not given; its affected and noTests are filled as they read; and its filter, a
    /// list the engine reads where the step starts, is handed on as written, never filled.
    /// </summary>
    [Fact]
    public void AKeyThatCannotBeFilled_IsNamed_AndOneFilledToNothingIsNotGiven()
    {
        var step = Step("""
              - name: verify
                type: test
                filter: 'union(input.units, run.affectedTests)'
                exclude: ['{input.skip}', slow]
                labels: ['{input.label}']
                affected: '{input.key}'
                noTests: 'none: {input.reason}'
            """);

        Assert.Null(FlowStepTemplates.Fill(step, Unreadable, out var key, out var problem));
        Assert.Equal(("exclude", "'{input.skip}' reads nothing"), (key, problem));

        var empty = FlowStepTemplates.Fill(step, Empty, out key, out problem)!;

        Assert.Equal((null, null), (key, problem));
        Assert.Equal("union(input.units, run.affectedTests)", empty.Filter!.Expression);
        Assert.Equal(["slow"], empty.Exclude);
        Assert.Empty(empty.Labels);
        Assert.Equal((string.Empty, "none: {input.reason}"), (empty.Affected, empty.NoTests));

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

    /// <summary>
    /// The classes of the work item a reason step's prompt reads (<see cref="FlowStepTemplates.ClassesOf"/>), read by the
    /// reader the template's fill reads by: a prompt that reads <c>{input.item.classes}</c> - or <c>{item.classes}</c>, as
    /// a step of the flow's own names the <c>forEach</c> variable - is handed what that read yields, taken apart from what
    /// the reader holds, and one that reads one class (<c>{input.item.classes[1]}</c>) that class alone; one that reads no
    /// classes, or whose read yields none - nothing in scope, an item that declares none - is handed nothing.
    /// </summary>
    [Fact]
    public void APromptThatReadsTheItemsClasses_IsHandedThemAsItsReaderReadsThem_AndOneThatReadsNoneIsHandedNothing()
    {
        var item = JsonNode.Parse("""{"title": "one", "classes": [{"id": "C1"}, {"id": "C2"}]}""")!;
        var input = new JsonObject { ["item"] = item.DeepClone() };

        var throughInput = FlowStepTemplates.ClassesOf("The classes you are closing:\n\n{input.item.classes}", Reader(("input", input)));
        var throughItem = FlowStepTemplates.ClassesOf("The classes you are closing:\n\n{item.classes}", Reader(("item", item)));
        var one = FlowStepTemplates.ClassesOf("The class you are closing:\n\n{input.item.classes[1]}", Reader(("input", input)));

        Assert.Equal("""[{"id":"C1"},{"id":"C2"}]""", throughInput!.ToJsonString());
        Assert.Equal(throughInput!.ToJsonString(), throughItem!.ToJsonString());
        Assert.Equal("""[{"id":"C2"}]""", one!.ToJsonString());

        // Taken apart from what the reader holds: what the step does of its classes does not touch the item the run holds.
        Assert.Equal(2, item["classes"]!.AsArray().Count);
        throughItem.Add(JsonNode.Parse("""{"id":"C3"}"""));
        Assert.Equal(2, item["classes"]!.AsArray().Count);

        Assert.Null(FlowStepTemplates.ClassesOf("Build this work item: {input.item.title}.", Reader(("input", input))));
        Assert.Null(FlowStepTemplates.ClassesOf("The classes you are closing:\n\n{input.item.classes}", Reader()));
        Assert.Null(FlowStepTemplates.ClassesOf("The classes you are closing:\n\n{input.item.classes}", Reader(("input", JsonNode.Parse("""{"item": {"title": "one", "noClass": "nothing to decide"}}""")))));

        static Func<FlowExpression.Path, FlowExpression.PathRead> Reader(params (string Root, JsonNode? Value)[] roots)
            => path => roots.FirstOrDefault(root => string.Equals(root.Root, path.Root, StringComparison.Ordinal)) is { Root: not null } found
                ? new(FlowValue.Of(found.Value), 0)
                : new(FlowValue.Missing, 0);
    }
}
