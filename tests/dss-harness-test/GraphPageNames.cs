// The graph's names of its pages, as the core gives them (src/DssHarness.Core/Codebase/GraphPageNames.cs): an alias of
// one assembly reaches no other, so the suite names them again - and the graph's own tests read, unchanged, as the proof
// that the graph's pages are the shared ones (DssHarness.Core.Pages) and changed nothing.
global using GraphSection = DssHarness.Core.Pages.PageSection<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageSection = DssHarness.Core.Pages.PageOfSection<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageAnswer = DssHarness.Core.Pages.PageAnswer<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageFind = DssHarness.Core.Pages.KeptFind<DssHarness.Core.Codebase.GraphPageKept>;
