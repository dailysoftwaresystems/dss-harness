// The graph's names of its pages, as every caller of the graph has always written them: each is a use of the one owner
// of an answer's pages (DssHarness.Core.Pages) over the graph's own entry and its own kept result, so a page, a section
// and a kept result of the graph are those of every answer, and the files the graph keeps read as they always did.
global using GraphSection = DssHarness.Core.Pages.PageSection<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageSection = DssHarness.Core.Pages.PageOfSection<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageAnswer = DssHarness.Core.Pages.PageAnswer<DssHarness.Core.Codebase.GraphEntry>;
global using GraphPageFind = DssHarness.Core.Pages.KeptFind<DssHarness.Core.Codebase.GraphPageKept>;
