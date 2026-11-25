using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Administrator")]
public sealed class AdminDashboardController(NpgsqlDataSource db) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        const string sql = """
            SELECT
              (SELECT count(*) FROM users u JOIN user_roles ur ON u.user_id=ur.user_id JOIN roles r ON ur.role_id=r.role_id WHERE r.role_name='Customer'),
              (SELECT count(*) FROM users u JOIN user_roles ur ON u.user_id=ur.user_id JOIN roles r ON ur.role_id=r.role_id WHERE r.role_name='Nutritionist'),
              (SELECT count(*) FROM users WHERE is_active = TRUE);
            """;
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Ok(new { customers = reader.GetInt64(0), nutritionists = reader.GetInt64(1), activeUsers = reader.GetInt64(2) });
    }
}

[ApiController]
[Route("api/nutritionist")]
[Authorize(Roles = "Nutritionist")]
public sealed class NutritionistDashboardController : ControllerBase
{
    [HttpGet("dashboard")]
    public IActionResult Dashboard() => Ok(new {
        message = "Nutritionist portal ready",
        assignedClients = 0,
        appointments = 0,
        pendingReviews = 0,
        dataStatus = "not_implemented"
    });
}
