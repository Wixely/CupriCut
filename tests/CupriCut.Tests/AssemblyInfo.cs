using Xunit;

// One test class at a time.
//
// Not a preference - a requirement, until CupriFace#185 is fixed. The engine keeps ONE diagnostics
// sink for the whole process: any document being opened, settled or rendered on any thread drops
// its findings into it, and CupriDoctor.Check drains whatever is there and calls it the answer.
// Measured, with one thread doing nothing but rendering a document that uses a `letter-spacing`
// the engine ignores, while another checked a document with no such property anywhere in it:
// 157 of 200 checks reported the other document's warning. The renderer never called the doctor
// at all.
//
// So a test that renders and a test that lints cannot run at the same time, or the lint reads the
// render's findings. It first showed up as `Every_shipped_composition_lints_clean` failing on
// bar-race.html for a property bar-race.html does not contain, while passing whenever it was run
// on its own.
//
// CupriCut serialises its OWN checks (see Services/Doctor) but cannot do anything about a
// concurrent RENDER - the studio holds a document open for as long as the preview is on screen,
// so there is no lock that both covers rendering and lets a lint through. This comes out when the
// engine keeps a finding with the document that produced it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
