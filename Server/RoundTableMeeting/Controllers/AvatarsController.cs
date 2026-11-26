using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Avatars;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.Controllers;

public sealed record SelectAvatarRequest(string AvatarId);
public sealed record AvatarInvitationRequest(string DisplayName, string AvatarId);
public sealed record AvatarInvitationResult(string InvitationKey);
public sealed record SelectedAvatarResult(string AvatarUrl);
public sealed class AvatarUploadRequest { public required IFormFile File { get; init; } }
public sealed class LibraryAvatarUploadRequest { public string Group { get; init; } = "自定义"; public required string Name { get; init; } public required IFormFile File { get; init; } }

[ApiController, Route("api/avatars"), Authorize]
public sealed class AvatarsController(PublicAvatarLibrary library, IAvatarRepository avatars, IMeetingWorkspaceService meetings, ICurrentUserContext current) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AvatarEntry>>> List(CancellationToken ct)
    {
        using var lease = await library.AcquireAsync(ct);
        return Ok(library.Catalog().SelectMany(g => g.Items).ToArray());
    }

    [HttpGet("groups"), Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<IReadOnlyList<AvatarGroup>>> Groups(CancellationToken ct)
    {
        using var lease = await library.AcquireAsync(ct);
        return Ok(library.Catalog());
    }

    [HttpPost("groups"), Authorize(Policy = "AdminOnly"), RequireBrowserOrigin(true)]
    public async Task<IActionResult> Edit(LibraryEdit edit, CancellationToken ct)
    {
        using var lease = await library.AcquireAsync(ct);
        try { library.Edit(edit); return NoContent(); }
        catch (LibraryError error) { return StatusCode(error.Status, new { message = error.Message }); }
    }

    [HttpGet("/Avatars/{group}/{file}"), AllowAnonymous]
    public async Task<IActionResult> PublicImage(string group, string file, CancellationToken ct)
    {
        using var lease = await library.AcquireAsync(ct);
        var image = library.Open(group, file);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Value.Stream, image.Value.Type);
    }

    [HttpGet("{id}/image")]
    public async Task<IActionResult> Image(string id, CancellationToken ct)
    {
        if (current.UserId is not Guid viewer) return Unauthorized();
        var image = await avatars.ReadImageAsync(id, viewer, ct);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Bytes, "image/png");
    }

    [HttpPost, Authorize(Policy = "AdminOnly"), RequireBrowserOrigin(false), Consumes("multipart/form-data"), RequestSizeLimit(AvatarUpload.MaxBytes + 65536)]
    public async Task<ActionResult<AvatarEntry>> Add([FromForm] LibraryAvatarUploadRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length is < 1 or > 64) return Invalid("invalid_avatar_name");
        var png = await AvatarUpload.ReadAsync(request.File, ct);
        if (png is null) return Invalid("invalid_avatar_file");
        using var lease = await library.AcquireAsync(ct);
        try
        {
            var entry = library.Add(request.Group, name, png);
            return Created(entry.ImageUrl, entry);
        }
        catch (LibraryError error) { return StatusCode(error.Status, new { message = error.Message }); }
    }

    [HttpPut("/api/account/avatar"), RequireBrowserOrigin(true)]
    public async Task<ActionResult<SelectedAvatarResult>> Select(SelectAvatarRequest request, CancellationToken ct)
    {
        if (current.UserId is not Guid user) return Unauthorized();
        using var lease = await library.AcquireAsync(ct);
        var entry = library.Find(request.AvatarId);
        if (entry is null) return NotFound();
        await avatars.SetAccountAsync(user, entry.ImageUrl, ct);
        return Ok(new SelectedAvatarResult(entry.ImageUrl));
    }

    [HttpPost("/api/account/avatar"), RequireBrowserOrigin(false), Consumes("multipart/form-data"), RequestSizeLimit(AvatarUpload.MaxBytes + 65536)]
    public async Task<ActionResult<SelectedAvatarResult>> Upload([FromForm] AvatarUploadRequest request, CancellationToken ct)
    {
        if (current.UserId is not Guid user) return Unauthorized();
        var png = await AvatarUpload.ReadAsync(request.File, ct);
        if (png is null) return Invalid("invalid_avatar_file");
        var entry = await avatars.SaveUploadAsync("个人头像", png, user, ct);
        return Ok(new SelectedAvatarResult(entry.ImageUrl));
    }

    [HttpPost("/api/meetings/{meetingId:guid}/agent-invitations"), RequireBrowserOrigin(true)]
    public async Task<ActionResult<AvatarInvitationResult>> Invite(Guid meetingId, AvatarInvitationRequest request, CancellationToken ct)
    {
        var permission = await meetings.InvitationAsync(meetingId, ct);
        if (permission.Status != MeetingOperationStatus.Success) return Forbid();
        var name = request.DisplayName.Trim().Normalize();
        if (name.Length is < 1 or > 64) return Invalid("invalid_display_name");
        using var lease = await library.AcquireAsync(ct);
        var entry = library.Find(request.AvatarId);
        if (entry is null) return NotFound();
        var key = Guid.NewGuid();
        var digest = Convert.ToHexString(SHA256.HashData(key.ToByteArray()));
        await avatars.CreateInvitationAsync(meetingId, name.ToUpperInvariant(), digest, entry.ImageUrl, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new AvatarInvitationResult(key.ToString("N")));
    }

    private ActionResult Invalid(string code) => (ActionResult)ApiProblemDetailsWriter.CreateResult(HttpContext, 400, code);
}
