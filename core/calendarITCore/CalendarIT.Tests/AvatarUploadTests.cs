using System.Security.Claims;
using calendarITCore.Controllers;
using CalendarIT.Application.Profile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CalendarIT.Tests;

/// <summary>
/// An avatar is stored with the content type the client claims and served back as a data URL of
/// that type, so the bytes must actually be that kind of image — nothing else may be stored under
/// an image type.
/// </summary>
public sealed class AvatarUploadTests
{
    private sealed class RecordingProfileService : IProfileService
    {
        public byte[]? Stored { get; private set; }

        public Task<ProfileDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileDto?>(new ProfileDto("a@test", null, null, null, null));

        public Task SetAvatarAsync(Guid userId, byte[] data, string contentType, CancellationToken cancellationToken = default)
        {
            Stored = data;
            return Task.CompletedTask;
        }

        public Task ClearAvatarAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SetDefaultViewAsync(Guid userId, string view, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SetClockFormatAsync(Guid userId, bool use24Hour, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SetWeekStartAsync(Guid userId, string? weekStart, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static async Task<(int Status, RecordingProfileService Service)> UploadAsync(string contentType, byte[] body)
    {
        var service = new RecordingProfileService();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test")),
        };
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(body);
        var controller = new ProfileController(service) { ControllerContext = new ControllerContext { HttpContext = http } };

        var result = await controller.UploadAvatar(CancellationToken.None);
        var status = result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => 0,
        };
        return (status, service);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];
    private static readonly byte[] Gif = "GIF89a\u0001\u0000"u8.ToArray();
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8];

    public static TheoryData<string, byte[]> RealImages => new()
    {
        { "image/png", Png },
        { "image/jpeg", Jpeg },
        { "image/gif", Gif },
        { "image/webp", Webp },
    };

    [Theory]
    [MemberData(nameof(RealImages))]
    public async Task A_real_image_is_stored(string contentType, byte[] body)
    {
        var (status, service) = await UploadAsync(contentType, body);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotNull(service.Stored);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task Html_claiming_to_be_an_image_is_refused(string contentType)
    {
        var (status, service) = await UploadAsync(contentType, "<script>alert(1)</script>"u8.ToArray());
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, status);
        Assert.Null(service.Stored);
    }

    [Fact]
    public async Task A_png_sent_as_jpeg_is_refused()
    {
        var (status, _) = await UploadAsync("image/jpeg", Png);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, status);
    }

    [Fact]
    public async Task A_type_that_is_not_an_image_is_refused()
    {
        var (status, _) = await UploadAsync("image/svg+xml", "<svg/>"u8.ToArray());
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, status);
    }
}
