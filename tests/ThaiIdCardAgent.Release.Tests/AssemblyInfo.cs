// These tests drive real PowerShell scripts and manipulate global machine state — the
// CurrentUser certificate store and Authenticode signing. That state is not safe to share
// across parallel tests, so run them serially for deterministic, race-free results.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
