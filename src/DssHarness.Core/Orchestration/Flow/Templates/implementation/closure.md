# The universe this repository decides in

This file is the repository's own, written once by the tool that shipped it and never
overwritten after. It says, in prose written for the run's agents, the universe each
decision here ranges over: what a change must cover to be done, and what else judges the
same behaviour. A defect that comes back was decided in several places; this file is where
closing a class of defects at its one home begins.

The workflow declares this file as its text `closure`, and an agent is given it where its
step's prompt reads `{text.closure}`, as the file stands when the prompt is given. The
prompts shipped beside it that read it are the plan's, the plan audit's, the item audit's
and the builders' - the implement prompt and the recovery's remedy - which plan, check and
close the classes of the work items: what is written here weighs on what they decide a
class covers, so keep it true of the repository.

## The axes

Every axis a decision ranges over. A class's universe answers each of them by the name in
brackets - its values, named concretely, and where they are declared; or why it does not
apply - and answers each further axis the heading "This repository" names among its
further axes, by the name it has there:

- Each kind or type the decision ranges over - an enum's members, a schema's keys, a
  table's rows: name them (`kinds`).
- Each target or platform the repository builds for: its legs (`targets`).
- Each build configuration and optimisation level - Debug and Release, `-O0` and `-O2`,
  `/Od` and `/O2` (`configurations`).
- Each input and output format (`formats`).
- Each entry point that reaches the decision - command line, API, configuration file, UI
  (`entryPoints`).
- Each tier or layer it passes - parse, check, lower, emit; request, service, store
  (`tiers`).
- Each form of the construct - declare, define, reference; read, write; with and without
  the qualifier (`forms`).
- Both directions - accepted and refused; serialise and deserialise; encode and decode
  (`directions`).

## The references

What else judges the same behaviour, each measured on its own: another implementation, a
standard's examples, a reference tool, a previous version. Where there are none, the
tests and the legs are the only judges, and this says so.

## The optimised forms

How the same source is built or run in another form that changes what reaches the
decision: the release pipeline, another compiler's optimiser, a minifier, a trimmed or
AOT build. A change is not done until the optimised form of it is measured too.

## How a hotspot is read here

The tool counts how often a path was touched; this file says what a count means here -
beside the counts, in prose: which paths are hot by nature, and what a repeated touch
of one is a sign of.

## This repository

(Left empty by the shipped file. This heading is where a repository writes its own
universe: its formats x targets x tiers x optimisation pipelines x forms x both
directions, with its references and its optimised forms named, and each further axis
its decisions range over, by a name of its own.)
