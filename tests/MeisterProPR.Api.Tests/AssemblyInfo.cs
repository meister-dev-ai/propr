// Api integration tests manipulate environment variables (AI_ENDPOINT, AI_DEPLOYMENT,
// MEISTER_CLIENT_KEYS) and use WebApplicationFactory<Program>, both of which are
// process-wide singletons. Parallel execution causes race conditions between test classes.

[assembly: CollectionBehavior(DisableTestParallelization = true)]