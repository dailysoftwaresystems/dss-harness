# The universe this repository decides in

This file is the repository's own, written once by the tool that shipped it and never
overwritten after. It says, in prose its agents read, the universe each decision here
ranges over: what a change must cover to be done, and what else judges the same
behaviour. A defect that comes back was decided in several places; this file is where
closing a class of defects at its one home begins.

## The axes

Every axis a decision ranges over, each named concretely or said not to apply, and where
it is declared:

- Each kind or type the decision ranges over - an enum's members, a schema's keys, a
  table's rows: name them.
- Each target or platform the repository builds for: its legs.
- Each build configuration and optimisation level - Debug and Release, `-O0` and `-O2`,
  `/Od` and `/O2`.
- Each input and output format.
- Each entry point that reaches the decision - command line, API, configuration file, UI.
- Each tier or layer it passes - parse, check, lower, emit; request, service, store.
- Each form of the construct - declare, define, reference; read, write; with and without
  the qualifier.
- Both directions - accepted and refused; serialise and deserialise; encode and decode.

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
directions, with its references and its optimised forms named.)
