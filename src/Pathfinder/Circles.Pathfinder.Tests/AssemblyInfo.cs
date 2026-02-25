using NUnit.Framework;

// Default: fixtures run sequentially (safe for staging-dependent tests).
// Pure unit test fixtures opt in via [Parallelizable] on their class.
// This avoids session pool exhaustion on staging2's /test-env.
[assembly: LevelOfParallelism(8)]
