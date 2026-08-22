// The SSIS Object Model / Pipeline API is COM-based and not thread-safe. Running SSIS-touching
// tests in parallel causes intermittent COM failures, so we serialize the integration suite.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
