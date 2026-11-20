using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly NpgsqlDataSource _db;
    private readonly IPasswordHasher<object> _hasher;
    private readonly IConfiguration _config;

    public AuthController(NpgsqlDataSource db, IPasswordHasher<object> hasher, IConfiguration config)
    {
        _db = db; _hasher = hasher; _config = config;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = """
            INSERT INTO users(full_name,email,password_hash,phone_number)
            VALUES(@name,@email,@hash,@phone) RETURNING user_id;
            """;
        var hash = _hasher.HashPassword(new object(), request.Password);
        Guid id;
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("name", request.FullName.Trim());
            cmd.Parameters.AddWithValue("email", email);
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.AddWithValue("phone", (object?)request.PhoneNumber ?? DBNull.Value);
            id = (Guid)(await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("User insert failed."));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return Conflict(new { message = "An account with this email already exists." });
        }

        await using (var role = new NpgsqlCommand("""
            INSERT INTO user_roles(user_id,role_id)
            SELECT @id,role_id FROM roles WHERE role_name='Customer';
            """, conn, tx))
        {
            role.Parameters.AddWithValue("id", id);
            await role.ExecuteNonQueryAsync(ct);
        }
        await using (var profile = new NpgsqlCommand(
            "INSERT INTO customer_profiles(user_id) VALUES(@id);", conn, tx))
        {
            profile.Parameters.AddWithValue("id", id);
            await profile.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return StatusCode(201, new { message = "Registration successful. Please log in." });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        const string sql = """
            SELECT u.user_id,u.full_name,u.email,u.password_hash,u.is_active,
                   COALESCE(string_agg(r.role_name,','),'')
            FROM users u
            LEFT JOIN user_roles ur ON ur.user_id=u.user_id
            LEFT JOIN roles r ON r.role_id=ur.role_id
            WHERE lower(u.email)=@email
            GROUP BY u.user_id;
            """;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("email", request.Email.Trim().ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Unauthorized(new { message = "Invalid email or password." });

        var id = reader.GetGuid(0);
        var name = reader.GetString(1);
        var email = reader.GetString(2);
        var hash = reader.GetString(3);
        var active = reader.GetBoolean(4);
        var roles = reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var verified = _hasher.VerifyHashedPassword(new object(), hash, request.Password);
        if (!active || verified == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid email or password." });

        var token = CreateToken(id, email, name, roles);
        return Ok(new {
            accessToken = token, tokenType = "Bearer", expiresIn = 1800,
            user = new { userId = id, fullName = name, email, roles }
        });
    }

    private string CreateToken(Guid id, string email, string name, string[] roles)
    {
        var issuer = _config["Jwt:Issuer"] ?? "Nutritionist.Api";
        var audience = _config["Jwt:Audience"] ?? "Nutritionist.Pwa";
        var mins = int.TryParse(_config["Jwt:ExpiryMinutes"], out var value) ? value : 30;
        var claims = new List<Claim> {
            new(JwtRegisteredClaimNames.Sub,id.ToString()),
            new(JwtRegisteredClaimNames.Email,email),
            new(ClaimTypes.Name,name),
            new(JwtRegisteredClaimNames.Jti,Guid.NewGuid().ToString())
        };
        claims.AddRange(roles.Select(x => new Claim(ClaimTypes.Role,x)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key,SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(issuer,audience,claims,expires:DateTime.UtcNow.AddMinutes(mins),signingCredentials:creds));
    }
}

<<<<<<< HEAD

public sealed record RegisterRequest(
    [Required]
    [StringLength(120, MinimumLength = 2)]
    string FullName,

    [Required]
    [EmailAddress]
    [StringLength(254)]
    string Email,

    [Required]
    [MinLength(8)]
    [StringLength(128)]
    string Password,

    [Phone]
    [StringLength(25)]
    string? PhoneNumber
);


public sealed record LoginRequest(
    [param: Required]
    [param: EmailAddress]
    string Email,

    [param: Required]
=======
public sealed record RegisterRequest(
    [Required, StringLength(120, MinimumLength = 2)]
    string FullName,

    [Required, EmailAddress, StringLength(254)]
    string Email,

    [Required, MinLength(8), StringLength(128)]
    string Password,

    [Phone, StringLength(25)]
    string? PhoneNumber
);

public sealed record LoginRequest(
    [Required, EmailAddress]
    string Email,

    [Required]
>>>>>>> origin/main
    string Password
);
