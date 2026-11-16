using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AegisBank.Dashboard.Pages.Account;

[EnableRateLimiting("login")]
public sealed class LoginModel(IAuthService auth, ILogger<LoginModel> logger) : PageModel
{
    public sealed class InputModel
    {
        [Required, StringLength(64)] public string Username { get; set; } = "";
        [Required, StringLength(256)] public string Password { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Failed { get; private set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var user = await auth.ValidateCredentialsAsync(Input.Username, Input.Password, HttpContext.GetCorrelationId(), ct);
        // Customers have valid credentials but no access to the console; they get the same answer as a wrong password.
        if (user is null || user.Role is not ("Admin" or "Auditor"))
        {
            if (user is not null) logger.LogWarning("Console sign-in refused for non-staff user {UserId}", user.Id);
            Failed = true;
            Input.Password = "";
            return Page();
        }

        var identity = new ClaimsIdentity(AegisClaims.For(user), CookieAuthenticationDefaults.AuthenticationScheme,
            AegisClaims.Username, AegisClaims.Role);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });

        // Only local redirects are followed.
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
    }
}
