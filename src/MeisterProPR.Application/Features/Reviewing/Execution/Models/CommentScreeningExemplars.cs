// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Labeled exemplar comments per <see cref="CommentScreeningClass" /> — the reference points the embedding
///     screener compares a comment against (one centroid per class). Deliberately English: the embedding model is
///     multilingual, so English exemplars place semantically-equivalent phrasing in other languages near the same
///     centroid. There is no per-language phrase list to maintain — extend by adding exemplars that capture a
///     shape of hedging/vagueness/firmness, never by translating. A small cross-lingual set is used only to
///     validate that this holds (Phase C), not as classifier input.
/// </summary>
public static class CommentScreeningExemplars
{
    /// <summary>Exemplar comment texts grouped by screening class.</summary>
    public static IReadOnlyDictionary<CommentScreeningClass, IReadOnlyList<string>> ByClass { get; } =
        new Dictionary<CommentScreeningClass, IReadOnlyList<string>>
        {
            [CommentScreeningClass.Firm] =
            [
                "This dereferences `config` without a null check, so it throws a NullReferenceException when the config file is missing.",
                "The loop condition uses `<=` and reads one past the end of the array, causing an IndexOutOfRangeException.",
                "The file stream is never disposed, leaking a handle on every call to this method.",
                "This concatenates `userId` directly into the SQL string, allowing SQL injection.",
                "The cast to `int` throws InvalidCastException when the incoming value is a string.",
                "The `await` is missing here, so the exception from the task is never observed and the write is not persisted.",
                "This returns before releasing the lock, so a thrown exception leaves the mutex held and deadlocks later callers.",
                "The comparison uses `==` on two byte arrays, which compares references, not contents, so equal payloads are treated as different.",
            ],
            [CommentScreeningClass.Hedged] =
            [
                "This might be a problem if the input is null, but I am not sure.",
                "It seems like this could fail under concurrency; please verify.",
                "Consider whether this correctly handles the empty-collection case.",
                "I cannot confirm it, but this may leak a resource on the error path.",
                "It appears the token could already be expired here; it would be worth checking.",
                "Unclear whether this branch is reachable — you may want to validate that.",
                "There might be an edge case around the boundary value, though I could not reproduce it.",
                "This could potentially cause an issue depending on how callers use it.",
            ],
            [CommentScreeningClass.Vague] =
            [
                "Consider refactoring this for readability.",
                "You could also add some more tests here.",
                "It would be worth improving the naming in this area.",
                "This could be strengthened.",
                "You might want to clean this up at some point.",
                "Consider adding documentation for this.",
                "This part could be made more maintainable.",
                "It might be nice to simplify this logic eventually.",
            ],
        };
}
