using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Auth;

namespace Samian.WebApi.Controllers;

// The thinnest layer in the solution, and that is the point. A controller here does exactly three
// things: bind the route/body to a request record, hand it to an application service, pick the HTTP
// status code. No try/catch (the exception middleware owns that), no validation (the request
// validators own that), no mapping (the response records own that).
//
// Controllers depend on IAuthService — an Application INTERFACE. They never see IUserRepository,
// User.Create, or the PBKDF2 hasher; "can a controller compile against Domain types?" is the
// question the onion answers, and here the answer is it does not even import them.
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
    }

    //AllowAnonymous on purpose, with a decision behind it: registration hands back a token so a new
    //user can immediately call protected endpoints, which is friendlier than a second login round
    //trip. The trade-off is that a scripted attacker learns which user names were free a moment ago —
    //acceptable here because RegisterAsync already answers 409 "already taken" for that exact probe.
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var response = await _auth.RegisterAsync(request, cancellationToken);

        // 201 Created with a Location header only makes sense for a fetchable resource, and there is
        // no GET /api/auth/{id} — a token is a session, not a document. 200 with the body is honest.
        return Ok(response);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        // Bad credentials surface as InvalidCredentialsException -> 401 in the exception middleware.
        // The controller staying silent about it is what keeps the "unknown user" and "wrong
        // password" responses byte-identical.
        var response = await _auth.LoginAsync(request, cancellationToken);

        return Ok(response);
    }
}
