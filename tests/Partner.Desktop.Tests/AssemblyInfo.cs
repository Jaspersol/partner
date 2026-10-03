using Xunit;

// The desktop is global, shared, mutable state: these tests enumerate real windows, and the live ones host a
// window of their own and move the cursor. Running them in parallel makes any test that samples the desktop
// racy against a window appearing or closing underneath it — which is exactly how a hosted TopMost test window
// made "the first foreground window" vanish between two observations, and turned a stable test into a
// once-in-a-while failure. Serial is slower and correct.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
