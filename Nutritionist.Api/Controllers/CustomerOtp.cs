using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using Npgsql;

namespace Nutritionist.Api.Controllers;

internal static class CustomerOtp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static byte[] Hash(IConfiguration config, Guid challenge, string code)
    {
        var key = Convert.FromBase64String(config["Otp:Key"] ?? "");
        if (key.Length < 32) throw new InvalidOperationException("OTP key is missing or too short.");
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{challenge:D}:{code}"));
    }
    internal static async Task<(Guid? Challenge, string? Error)> Send(NpgsqlDataSource db, IConfiguration config, Guid user, string email, Guid? previous, CancellationToken ct)
    {
        var apiKey = config["Brevo:ApiKey"];
        var sender = config["Brevo:SenderEmail"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sender))
            return (null, "Email verification is temporarily unavailable.");
        var challenge = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        var hash = Hash(config, challenge, code);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serialize all sends for this user, including first login and resend.
        await using (var gate = new NpgsqlCommand("SELECT user_id FROM users WHERE user_id=@u AND is_active FOR UPDATE", conn, tx))
        {
            gate.Parameters.AddWithValue("u", user);
            if (await gate.ExecuteScalarAsync(ct) is null) return (null, "Account unavailable.");
        }
        if (previous.HasValue)
        {
            await using var check = new NpgsqlCommand("SELECT 1 FROM customer_login_otp WHERE user_id=@u AND challenge_id=@p AND NOT consumed AND expires_at>now() AND attempts<5", conn, tx);
            check.Parameters.AddWithValue("u", user); check.Parameters.AddWithValue("p", previous.Value);
            if (await check.ExecuteScalarAsync(ct) is null) return (null, "Please sign in again.");
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO customer_login_otp(user_id,challenge_id,code_hash,expires_at,sent_at,window_start,sends)
            VALUES(@u,@c,@h,now()+interval '5 minutes',now(),now(),1)
            ON CONFLICT(user_id) DO UPDATE SET challenge_id=@c,code_hash=@h,expires_at=now()+interval '5 minutes',sent_at=now(),
            window_start=CASE WHEN customer_login_otp.window_start<=now()-interval '15 minutes' THEN now() ELSE customer_login_otp.window_start END,
            sends=CASE WHEN customer_login_otp.window_start<=now()-interval '15 minutes' THEN 1 ELSE customer_login_otp.sends+1 END,
            attempts=CASE WHEN customer_login_otp.window_start<=now()-interval '15 minutes' THEN 0 ELSE customer_login_otp.attempts END,consumed=false
            WHERE customer_login_otp.sent_at<=now()-interval '60 seconds'
              AND (customer_login_otp.window_start<=now()-interval '15 minutes' OR (customer_login_otp.sends<3 AND customer_login_otp.attempts<5))
            RETURNING challenge_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("u", user); cmd.Parameters.AddWithValue("c", challenge); cmd.Parameters.AddWithValue("h", hash);
        if (await cmd.ExecuteScalarAsync(ct) is null) return (null, "Please wait before requesting another code (maximum 3 sends per 15 minutes).");
        await tx.CommitAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        req.Headers.Add("api-key", apiKey);
        var logoUrl = System.Net.WebUtility.HtmlEncode(config["Brevo:LogoUrl"] ?? "https://nutrifitora-client-web.onrender.com/email-logo.png");
        var html = $"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;background:#edf5ef;font-family:Arial,sans-serif;color:#20382c">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border-radius:20px;overflow:hidden">
            <tr><td align="center" style="background:#104d39;padding:28px;color:#ffffff"><strong style="font-size:28px">NutriFitora</strong><p style="margin:8px 0 0;color:#d2e8d9">Your journey to better wellness</p></td></tr>
            <tr><td style="padding:28px 22px"><h1 style="font-size:24px;text-align:center;margin:0 0 24px">Verify your identity</h1>
            <p>Hello,</p><p style="line-height:1.6">Use the verification code below to securely sign in to your NutriFitora account.</p>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="background:#f0f8f2;border:1px solid #c5e1cc;border-radius:12px;padding:22px 8px"><strong style="font-size:12px;color:#167451">YOUR VERIFICATION CODE</strong><p style="font-size:32px;letter-spacing:5px;font-weight:bold;color:#104d39;margin:16px 0">{code}</p><span style="font-size:14px;color:#52665a">Valid for 5 minutes</span></td></tr></table>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:24px 0"><tr><td align="center" bgcolor="#104d39" style="border-radius:10px;padding:14px"><a href="https://nutrifitora-client-web.onrender.com/" style="display:block;color:#ffffff;text-decoration:none;font-weight:bold"><img src="{logoUrl}" alt="NutriFitora" width="100" style="display:block;margin:0 auto 8px;background:#ffffff;border-radius:6px;padding:5px;max-width:100%;height:auto">Open NutriFitora</a></td></tr></table>
            <p style="font-size:13px;color:#52665a;text-align:center">Return to your original login screen to enter the code.</p>
            <div style="background:#fff8e9;border-radius:10px;padding:16px;font-size:14px;line-height:1.6"><strong>Keep your account secure</strong><br>Never share this code with anyone. If you did not request it, ignore this email.</div>
            <p>Stay healthy, stay inspired.<br><strong>Team NutriFitora</strong></p></td></tr>
            <tr><td align="center" style="background:#f4f8f5;padding:20px;color:#52665a;font-size:12px;line-height:1.7">Small steps. Healthier you.<br>© {DateTime.UtcNow.Year} NutriFitora. All rights reserved.<br>This is an automated email. Please do not reply.</td></tr></table></td></tr></table></body></html>
            """;
        req.Content = JsonContent.Create(new { sender = new { name = "NutriFitora", email = sender }, to = new[] { new { email } }, subject = "NutriFitora | Your secure login verification code", htmlContent = html, textContent = $"Your verification code is {code}. It expires in 5 minutes. Do not share this code. If you did not request it, ignore this email." });
        try { using var response = await Http.SendAsync(req, ct); if (response.IsSuccessStatusCode) return (challenge, null); }
        catch (HttpRequestException) { } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return (null, "Unable to send verification email. Please try signing in again after 60 seconds.");
    }
}
