namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Walks a workflow's declaration as a graph and finds its dependency cycles: every declared step a node, groups
/// included, and an edge for each dependency - resolved as a read of it would be, among the siblings of the scope
/// that declared it, then the scopes around it - for a group's hold on each of its children, for the dependencies
/// a child inherits from every group around it, and for an <c>on</c>: a handler starts after its source as a
/// dependent does.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and the codebase's first graph: nothing here reads a file, keeps a state or knows a run. The walk is an
/// iterative three-colour depth-first search in declaration order, so a cycle is reported as the path that closes
/// it, first-found first, and no stack is spent on a flow however deep.
/// </para>
/// <para>
/// Declaration level is enough: an instance graph is a copy of an acyclic body, and a <c>repeat</c> is sequencing,
/// not an edge. <c>then</c> is no edge either - it is the one back edge the engine allows, drawn when a handler
/// ends and bounded by <c>maxVisits</c> - and what it may name is a rule of its own, checked beside this walk and
/// not by it. A dependency that names nothing the flow declares is no edge here: the validator owns that
/// refusal, and this walk runs beside it over files the reader accepted, whatever the validator will say of them -
/// so nothing here throws and nothing here refuses; where siblings share a name, the last declaration is the one
/// the edges draw to, as a read of it would find.
/// </para>
/// </remarks>
public static class FlowGraph
{
    /// <summary>
    /// Finds the workflow's first dependency cycle, as the path that closes it - each node a step's name, a
    /// member's under its group's, joined in walk order - or <see langword="null"/> where the flow has none.
    /// </summary>
    /// <param name="definition">The workflow, as it was read.</param>
    public static IReadOnlyList<string>? FindCycle(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var nodes = new List<Node>();

        Collect(definition.Steps, id: null, inherited: [], nodes);

        // The id of each node, last declaration winning where siblings share a name: a duplicate is the
        // validator's to refuse, and the walk still answers over what was read.
        var byId = new Dictionary<string, WorkflowStep>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            byId[node.Id] = node.Step;
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var outEdges = new List<string>();

            // Each dependency names a sibling of the scope that declared it, and is looked for nowhere else.
            foreach (var (name, at) in node.Dependencies)
            {
                if (SiblingThenOutward(name, at, definition, byId) is { } target && !outEdges.Contains(target, StringComparer.Ordinal))
                {
                    outEdges.Add(target);
                }
            }

            edges[node.Id] = outEdges;
        }

        foreach (var node in nodes)
        {
            if (node.Step.On is not { } raised)
            {
                continue;
            }

            // A handler starts after its source as a dependent does: the edge runs from the source to it, the
            // source named in the handler's own scope.
            if (SiblingThenOutward(raised.Step, ScopeOf(node.Id), definition, byId) is { } source
                && edges.TryGetValue(source, out var fromSource)
                && !fromSource.Contains(node.Id, StringComparer.Ordinal))
            {
                fromSource.Add(node.Id);
            }
        }

        foreach (var node in nodes)
        {
            if (node.Step.Steps.Count == 0)
            {
                continue;
            }

            // A group holds each of its children: what it waits for, they wait for, and it does not end before
            // they do - an edge down, beside the dependencies up and out.
            foreach (var child in node.Step.Steps)
            {
                var childId = $"{node.Id}.{child.Name}";

                if (byId.ContainsKey(childId) && !edges[node.Id].Contains(childId, StringComparer.Ordinal))
                {
                    edges[node.Id].Add(childId);
                }
            }
        }

        return Walk(nodes, edges);
    }

    /// <summary>One declared step in its place: its id among the flow, what it declares, and the dependencies it inherited with the scope each was declared in.</summary>
    /// <param name="Id">The step's path among the flow: its name, a member's under its group's.</param>
    /// <param name="Step">The step as it was read.</param>
    /// <param name="Dependencies">Every dependency the step holds - its own and what it inherited - each with the path of the scope that declared it.</param>
    private sealed record Node(string Id, WorkflowStep Step, IReadOnlyList<(string Name, string At)> Dependencies);

    /// <summary>Gathers every declared step in declaration order, groups first among their own.</summary>
    private static void Collect(IReadOnlyList<WorkflowStep> siblings, string? id, IReadOnlyList<(string Name, string At)> inherited, List<Node> nodes)
    {
        foreach (var step in siblings)
        {
            var stepId = id is null ? step.Name : $"{id}.{step.Name}";

            // A dependency is declared in the scope its holder sits in: its own among its siblings, a group's
            // among the group's siblings. A child inherits every one of them, each keeping the scope it was
            // declared in, and none is looked for anywhere else.
            var held = new List<(string Name, string At)>(inherited);

            foreach (var name in step.DependsOn)
            {
                held.Add((name, ScopeOf(stepId)));
            }

            nodes.Add(new Node(stepId, step, held));

            if (step.Steps.Count > 0)
            {
                Collect(step.Steps, stepId, held, nodes);
            }
        }
    }

    /// <summary>The scope a step sits in: the path of its group, or the flow's top where it has none.</summary>
    private static string ScopeOf(string stepId)
        => stepId.Contains('.') ? stepId[..stepId.LastIndexOf('.')] : string.Empty;

    /// <summary>
    /// A name's id, resolved as a read of it would be: among the scope's siblings first, then the scopes around
    /// it, outward to the top. A file the rules accept hits the sibling and never walks out; a file they refuse -
    /// whose names this walk still answers over - may name an outer step, and the edge is drawn where it says,
    /// for the validator refuses that file whatever this walk reports of it.
    /// </summary>
    private static string? SiblingThenOutward(string name, string at, WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowStep> byId)
    {
        var scope = at.Length == 0 ? null : at;

        while (true)
        {
            if (scope is null)
            {
                var top = definition.Steps.LastOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));

                return top is null ? null : top.Name;
            }

            if (byId.TryGetValue(scope, out var group))
            {
                var member = group.Steps.LastOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));

                if (member is not null)
                {
                    return $"{scope}.{member.Name}";
                }
            }

            scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : null;
        }
    }

    /// <summary>The walk: three colours, declaration order, the first closing path reported whole.</summary>
    private static IReadOnlyList<string>? Walk(List<Node> nodes, IReadOnlyDictionary<string, List<string>> edges)
    {
        const byte white = 0, grey = 1, black = 2;

        var colour = new Dictionary<string, byte>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            colour[node.Id] = white;
        }

        foreach (var start in nodes)
        {
            if (colour[start.Id] != white)
            {
                continue;
            }

            // The stack holds the path: a node and how far along its edges the walk is.
            var path = new Stack<(string Id, int Next)>();
            colour[start.Id] = grey;
            path.Push((start.Id, 0));

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
                        var closed = new List<string>();
                        var seen = false;

                        foreach (var stepId in walked)
                        {
                            if (seen)
                            {
                                closed.Add(stepId);
                            }
                            else if (string.Equals(stepId, along, StringComparison.Ordinal))
                            {
                                seen = true;
                                closed.Add(stepId);
                            }
                        }

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
