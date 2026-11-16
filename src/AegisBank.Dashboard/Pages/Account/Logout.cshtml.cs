using AegisBank.Application;
using AegisBank.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace AegisBank.Dashboard.Pages.Account;

public sealed class LogoutModel(IAuthService auth) : ConsolePage
{
    public void OnGet() { }

    // Sign-out is a POST protected by the antiforgery token, so another site cannot trigger it.
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var actor = Actor;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await auth.RecordSignOutAsync(actor.UserId, "console", HttpContext.GetCorrelationId(), ct);
        return RedirectToPage("/Account/Login");
    }
}
