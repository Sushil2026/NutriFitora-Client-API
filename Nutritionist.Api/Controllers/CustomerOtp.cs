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
        req.Content = JsonContent.Create(new { sender = new { name = "NutriFitora", email = sender }, to = new[] { new { email } }, subject = "Your NutriFitora login code", textContent = $"Your verification code is {code}. It expires in 5 minutes. Do not share this code. If you did not request it, ignore this email." });
        try { using var response = await Http.SendAsync(req, ct); if (response.IsSuccessStatusCode) return (challenge, null); }
        catch (HttpRequestException) { } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return (null, "Unable to send verification email. Please try signing in again after 60 seconds.");
    }
}
