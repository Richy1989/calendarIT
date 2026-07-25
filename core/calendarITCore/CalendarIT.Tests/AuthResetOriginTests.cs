using calendarITCore.Controllers;
using CalendarIT.Application.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace CalendarIT.Tests;

/// <summary>
/// Where a password-reset link is allowed to point. The link is a bearer credential delivered to
/// the account owner's mailbox, so the host in it must come from the operator's configuration and
/// nothing else — least of all the Host header, which is written by whoever made the request.
/// </summary>
public sealed class AuthResetOriginTests
{
    /// <summary>Captures the origin the controller hands to the service; everything else is inert.</summary>
    private sealed class CapturingAuthService : IAuthService
    {
        public string? LinkBase { get; private set; }
        public bool Called { get; private set; }

        public Task RequestPasswordResetAsync(
            ForgotPasswordRequest request, string? linkBase, CancellationToken cancellationToken = default)
        {
            Called = true;
            LinkBase = linkBase;
            return Task.CompletedTask;
        }

        public AuthConfig GetConfig() => new(RegistrationEnabled: true);
        public Task<AuthResult> RegisterAsync(RegisterRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AuthResult> LoginAsync(LoginRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<AuthResult> RefreshAsync(RefreshTokenRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task LogoutAsync(LogoutRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<PasswordResult> ChangePasswordAsync(Guid u, ChangePasswordRequest r, CancellationToken c = default) => throw new NotSupportedException();
        public Task<PasswordResult> ResetPasswordAsync(ResetPasswordRequest r, CancellationToken c = default) => throw new NotSupportedException();
    }

    /// <summary>A controller answering a request that arrived claiming <paramref name="host"/>.</summary>
    private static (AuthController Controller, CapturingAuthService Service) Controller(
        string host, string? publicBaseUrl)
    {
        var settings = publicBaseUrl is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = publicBaseUrl };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var service = new CapturingAuthService();
        var controller = new AuthController(service, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString(host);
        return (controller, service);
    }

    private static ForgotPasswordRequest Forgot => new() { Email = "owner@example.com" };

    [Fact]
    public async Task ForgotPassword_DoesNotBuildTheLinkFromTheHostHeader()
    {
        var (controller, service) = Controller("attacker.example", publicBaseUrl: null);

        await controller.ForgotPassword(Forgot, CancellationToken.None);

        // Nothing the caller sent may end up in the emailed link.
        Assert.True(service.Called);
        Assert.Null(service.LinkBase);
    }

    [Fact]
    public async Task ForgotPassword_UsesTheConfiguredOriginEvenWhenTheHostHeaderDiffers()
    {
        var (controller, service) = Controller("attacker.example", "https://cal.example.com/");

        await controller.ForgotPassword(Forgot, CancellationToken.None);

        Assert.Equal("https://cal.example.com", service.LinkBase);
    }

    [Fact]
    public async Task ForgotPassword_StillAnswersTheSameWayWithNoOriginConfigured()
    {
        var (controller, _) = Controller("attacker.example", publicBaseUrl: null);

        var result = await controller.ForgotPassword(Forgot, CancellationToken.None);

        // A misconfigured server must not become an account-enumeration oracle.
        Assert.IsType<AcceptedResult>(result);
    }
}
