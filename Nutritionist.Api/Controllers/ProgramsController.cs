using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Nutritionist.Api.Controllers;

[ApiController]
[Route("api/programs")]
public sealed class ProgramsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public ProgramsController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProgramDto>>> GetAll(CancellationToken cancellationToken)
    {
        var programs = new List<ProgramDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, program_code, program_name, duration_weeks, price_inr,
                   description, goal, includes, best_for, is_purchasable
            FROM public.nutrition_programs
            WHERE is_active = TRUE
            ORDER BY display_order, id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            programs.Add(ReadProgram(reader));
        }
        return Ok(programs);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ProgramDto>> GetById(long id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, program_code, program_name, duration_weeks, price_inr,
                   description, goal, includes, best_for, is_purchasable
            FROM public.nutrition_programs
            WHERE id = @id AND is_active = TRUE
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Ok(ReadProgram(reader))
            : NotFound();
    }

    private static ProgramDto ReadProgram(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetDecimal(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetBoolean(9)
    );
}

public sealed record ProgramDto(
    long Id,
    string ProgramCode,
    string ProgramName,
    int DurationWeeks,
    decimal? PriceInr,
    string? Description,
    string? Goal,
    string? Includes,
    string? BestFor,
    bool IsPurchasable
);
