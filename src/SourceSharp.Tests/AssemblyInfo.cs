using Xunit;

// NO PARALLELISM, and this is not a workaround for flakiness — it is a property
// of what is under test. The engine binding is per-SIDE and selected by a
// thread-local, several registries are process-wide, and some suites publish
// networked state that others read back. Two tests at once would be two tests
// sharing one engine, and the failure would look like a bug in whichever lost
// the race.
//
// The runner this replaces ran everything on one thread in one order and never
// had to say so. Saying it is the improvement: the constraint was always there.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
