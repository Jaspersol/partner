using Xunit;

// The desktop is global mutable state: these tests enumerate real windows, and the live ones host a window and
// move the cursor. Parallel runs make any desktop sample racy against a window opening or closing underneath it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
