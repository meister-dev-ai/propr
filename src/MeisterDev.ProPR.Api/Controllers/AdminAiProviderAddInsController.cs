// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     The add-ins this host found: the families it is running, the assemblies it passed over, and the ones
///     waiting for a platform administrator to activate them.
/// </summary>
/// <remarks>
///     <para>
///         An add-in in the external directory is described here and not loaded until an administrator activates
///         it. Everything shown for such an add-in was read out of the file with none of it executed, which is
///         what makes the decision one about a binary rather than about a family that is already running.
///     </para>
///     <para>
///         Platform administrators only: the directories, the file paths and the hashes describe the
///         installation, and administering one tenant is not grounds for reading them, let alone for deciding
///         what the host runs.
///     </para>
/// </remarks>
[ApiController]
[Route("admin/ai-provider-add-ins")]
public sealed class AdminAiProviderAddInsController(
    ProviderAddInCatalog catalog,
    ProviderAddInActivationService activations) : ControllerBase
{
    /// <summary>Returns every assembly the host saw, and every activation this installation holds.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <response code="200">The loaded families, the passed-over assemblies, and the ones awaiting activation.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderAddInInventoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetAddIns(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(
            new ProviderAddInInventoryDto(
                [
                    .. catalog.Loaded.Select(family => new LoadedProviderAddInDto(
                        family.Key,
                        family.Label,
                        family.Version,
                        family.ContractVersion,
                        family.ReachedHostPatterns,
                        family.RequiredCapabilityKey,
                        family.FilePath,
                        family.ContentHash,
                        ProviderAddInNames.Format(family.Origin))),
                ],
                [
                    .. catalog.Rejected.Select(skipped => new RejectedProviderAddInDto(
                        ProviderAddInNames.Format(skipped.Category),
                        skipped.Reason,
                        skipped.FilePath,
                        skipped.ContentHash,
                        skipped.Key,
                        ProviderAddInNames.Format(skipped.Origin))),
                ],
                [
                    .. catalog.Awaiting.Select(found => new AwaitingProviderAddInDto(
                        found.FilePath,
                        found.ContentHash,
                        found.Key,
                        found.Label,
                        found.Version,
                        found.ContractVersion,
                        found.ReachedHosts,
                        found.RequiredCapability,
                        found.AssemblyName,
                        found.AssemblyVersion,
                        found.Refusal,
                        found.CanBeActivated)),
                ]));
    }

    /// <summary>The decisions this installation's administrators have made, newest first.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <response code="200">The activations, each with whether the host is running it now.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    [HttpGet("activations")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderAddInActivationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActivations(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var serving = catalog.Loaded
            .Select(family => family.ContentHash)
            .Where(hash => hash is not null)
            .ToHashSet(StringComparer.Ordinal);

        return this.Ok(
            (await activations.ListAsync(ct)).Select(row => new ProviderAddInActivationDto(
                row.ContentHash,
                row.FamilyKey,
                row.Label,
                row.Version,
                row.FilePath,
                row.ActivatedByDisplayName,
                row.ActivatedAt,
                serving.Contains(row.ContentHash)))
            .ToList());
    }

    /// <summary>Lets this host run the add-in whose bytes hash to <paramref name="contentHash" />.</summary>
    /// <param name="contentHash">The bytes being approved, as the page listed them.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    ///     The add-in is loaded by this call, so the family serves before the administrator leaves the page. It
    ///     is refused where the file has changed since the host read it, where what its driver declares
    ///     disagrees with what its assembly states, and where the identity is one this host already serves.
    /// </remarks>
    /// <response code="200">The add-in was loaded and is serving.</response>
    /// <response code="400">The add-in was not activated; the body says why.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    [HttpPost("{contentHash}/activate")]
    [ProducesResponseType(typeof(LoadedProviderAddInDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Activate(string contentHash, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var outcome = await activations.ActivateAsync(contentHash, AuthHelpers.GetUserId(this.HttpContext), ct);

        if (outcome.Activated is not { } loaded)
        {
            this.ModelState.AddModelError("contentHash", outcome.Refusal ?? "The add-in was not activated.");

            return this.ValidationProblem();
        }

        return this.Ok(
            new LoadedProviderAddInDto(
                loaded.Key,
                loaded.Label,
                loaded.Version,
                loaded.ContractVersion,
                loaded.ReachedHostPatterns,
                loaded.RequiredCapabilityKey,
                loaded.FilePath,
                loaded.ContentHash,
                ProviderAddInNames.Format(loaded.Origin)));
    }

    /// <summary>Withdraws the activation of one add-in.</summary>
    /// <param name="contentHash">The bytes whose activation is being withdrawn.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    ///     The add-in stays loaded until this host restarts. An assembly cannot be unloaded from under the
    ///     connections using it, and a review holding a client that family built is in flight. What this does is
    ///     stop the next start taking it.
    /// </remarks>
    /// <response code="204">The activation was withdrawn.</response>
    /// <response code="404">No activation holds those bytes.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    [HttpDelete("{contentHash}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Revoke(string contentHash, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        return await activations.RevokeAsync(contentHash, ct) ? this.NoContent() : this.NotFound();
    }
}
