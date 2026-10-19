namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Walks a workflow's declaration as a graph and finds its dependency cycles: every declared step a node, groups
/// included, the recovery's own steps among them, and an edge from each step to each step it waits for - for each
/// dependency, for a group's hold on each of its children, for the dependencies a child inherits from every group
/// around it, and for an <c>on</c>: a handler waits for its source's ending as a dependent waits for what it depends
/// on.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and the codebase's first graph: nothing here reads a file, keeps a state or knows a run. The walk is an
/// iterative three-colour depth-first search in declaration order, so a cycle is reported as the path that closes
/// it, first-found first, and no stack is spent on a flow however deep.
/// </para>
/// <para>
/// Every edge runs from the waiter to what it waits for: a step to its dependency, a group to each child it does not
/// end before, a child to what its groups wait for, a handler to its source. Every name is found through
/// <see cref="FlowScope"/>, the one owner of where a name is: a dependency and a source are a sibling of the step
/// that names them, a child's inherited dependency a sibling of the group that declared it.
/// </para>
/// <para>
/// Declaration level is enough: an instance graph is a copy of an acyclic body, and a <c>repeat</c> is sequencing,
/// not an edge. <c>then</c> is no edge either - it is the one back edge the engine allows, drawn when a handler
/// ends and bounded by <c>maxVisits</c> - and what it may name is a rule of its own, checked beside this walk and
/// not by it. A name that finds no sibling, and a step that names itself, draw no edge here: the validator owns
/// those refusals ("depends on itself", "is raised by its own ending"), each said once, and this walk runs beside it
/// over files the reader accepted, whatever the validator will say of them - so nothing here throws and nothing here
/// refuses.
/// </para>
/// </remarks>
public static class FlowGraph
{
    /// <summary>
    /// Finds the workflow's first dependency cycle, as the path that closes it - each node a step's path, a member's
    /// under its group's and a step of the recovery's under <see cref="FlowScope.Recovery"/>, joined in walk order -
    /// or <see langword="null"/> where the flow has none.
    /// </summary>
    /// <param name="definition">The workflow, as it was read.</param>
    public static IReadOnlyList<string>? FindCycle(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Steps that share a name share a path, and so one node: a duplicate is the validator's to refuse, and the
        // walk still answers over what was read, with the edges of every declaration of it.
        var order = new List<string>();
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        Collect(definition.Steps, scope: null, inherited: [], definition, order, edges);

        if (definition.Recover is { } recover)
        {
            Collect(recover.Steps, FlowScope.Recovery, inherited: [], definition, order, edges);
        }

        return Walk(order, edges);
    }

    /// <summary>
    /// Places every step of <paramref name="siblings"/> and of every group among them as a node, in declaration order,
    /// a group before its members, with the edges from it to what it waits for.
    /// </summary>
    private static void Collect(
        IReadOnlyList<WorkflowStep> siblings,
        string? scope,
        IReadOnlyList<string> inherited,
        WorkflowDefinition definition,
        List<string> order,
        Dictionary<string, List<string>> edges)
    {
        foreach (var step in siblings)
        {
            var id = scope is null ? step.Name : $"{scope}.{step.Name}";

            if (!edges.TryGetValue(id, out var waits))
            {
                waits = [];
                edges[id] = waits;
                order.Add(id);
            }

            // A dependency is found where it was declared, among the siblings of the step that names it, and a child
            // inherits it as it was found there: never looked for again among the child's own siblings.
            var held = new List<string>(inherited);

            foreach (var name in step.DependsOn)
            {
                if (FlowScope.SiblingOf(name, definition, scope) is { } dependency && dependency.Path != id)
                {
                    held.Add(dependency.Path);
                }
            }

            foreach (var waited in held)
            {
                Draw(waits, waited);
            }

            // A handler waits for its source's ending, a sibling of its own, as a dependent waits for its dependency.
            if (step.On is { } raised
                && FlowScope.SiblingOf(raised.Step, definition, scope) is { } source
                && source.Path != id)
            {
                Draw(waits, source.Path);
            }

            // A group holds each of its children: it does not end before they do.
            foreach (var child in step.Steps)
            {
                Draw(waits, $"{id}.{child.Name}");
            }

            if (step.Steps.Count > 0)
            {
                Collect(step.Steps, id, held, definition, order, edges);
            }
        }
    }

    /// <summary>Adds an edge to <paramref name="target"/> where the node has none to it yet.</summary>
    private static void Draw(List<string> waits, string target)
    {
        if (!waits.Contains(target, StringComparer.Ordinal))
        {
            waits.Add(target);
        }
    }

    /// <summary>The walk: three colours, declaration order, the first closing path reported whole.</summary>
    private static IReadOnlyList<string>? Walk(IReadOnlyList<string> nodes, IReadOnlyDictionary<string, List<string>> edges)
    {
        const byte white = 0, grey = 1, black = 2;

        var colour = new Dictionary<string, byte>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            colour[node] = white;
        }

        foreach (var start in nodes)
        {
            if (colour[start] != white)
            {
                continue;
            }

            // The stack holds the path: a node and how far along its edges the walk is.
            var path = new Stack<(string Id, int Next)>();
            colour[start] = grey;
            path.Push((start, 0));

            while (path.Count > 0)
            {
                var top = path.Peek();

                if (top.Next >= edges[top.Id].Count)
                {
                    colour[top.Id] = black;
                    path.Pop();
                    continue;
                }

                var along = edges[top.Id][top.Next];
                path.Pop();
                path.Push((top.Id, top.Next + 1));

                switch (colour[along])
                {
                    case white:
                        colour[along] = grey;
                        path.Push((along, 0));
                        break;
                    case grey:
                        // The path from the first sighting of the node closed: report it, closed. The stack
                        // says its newest first, so the path is read from its oldest.
                        var walked = path.Select(frame => frame.Id).Reverse().ToList();
                        var closed = walked.SkipWhile(id => !string.Equals(id, along, StringComparison.Ordinal)).ToList();

                        closed.Add(along);
                        return closed;
                    default:
                        break;
                }
            }
        }

        return null;
    }
}
