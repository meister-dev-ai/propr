# Quickstart: Resolve PR Comments

1. **Database Migration:** A new EF Core migration will be required to add the `CommentResolutionBehavior` column to the `Clients` table and to create the `ReviewPrScans` table. Run `dotnet ef migrations add AddResolvePrComments` in the Infrastructure project.
2. **API Changes:** Update the Client controllers and DTOs to handle the new configuration. Update the Admin UI to allow configuring the `CommentResolutionBehavior` for a client.
3. **Run Application:** Run the `MeisterProPR.Api` and verify via Swagger UI that the new `CommentResolutionBehavior` property is exposed in the `/clients` endpoints.
4. **Trigger a Review:** Submitting a PR to a configured ADO repository will process the commit and write a `ReviewPrScan` record. Subsequent identical requests will be skipped unless new commits are detected.
5. **Simulate a Fix:** Make a comment as the automated reviewer on a PR, then push a new commit. The system should pick up the new commit, re-evaluate the comment context against the code changes via the AI, and resolve the thread in ADO.