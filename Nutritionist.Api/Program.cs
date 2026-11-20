
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// 1. Controllers and Swagger
// =====================================================

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// =====================================================
// 2. Neon PostgreSQL configuration
// =====================================================

var connectionString =
    builder.Configuration.GetConnectionString("Neon")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:Neon configuration.");

NpgsqlConnectionStringBuilder neonConnectionBuilder;

try
{
    neonConnectionBuilder =
        new NpgsqlConnectionStringBuilder(connectionString);

    if (string.IsNullOrWhiteSpace(neonConnectionBuilder.Host) ||
        string.IsNullOrWhiteSpace(neonConnectionBuilder.Database) ||
        string.IsNullOrWhiteSpace(neonConnectionBuilder.Username) ||
        string.IsNullOrWhiteSpace(neonConnectionBuilder.Password))
    {
        throw new InvalidOperationException(
            "Neon connection string must include Host, " +
            "Database, Username and Password.");
    }

    Console.WriteLine(
        "Neon connection string format validated.");
}
catch (ArgumentException)
{
    throw new InvalidOperationException(
        "Invalid ConnectionStrings:Neon format. " +
        "Check the Render environment variable. " +
        "Do not include surrounding quotation marks.");
}

// Register Npgsql without Entity Framework
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(
        neonConnectionBuilder.ConnectionString));

// =====================================================
// 3. Password hashing
// =====================================================

builder.Services.AddScoped<
    IPasswordHasher<object>,
    PasswordHasher<object>>();

// =====================================================
// 4. CORS configuration
// =====================================================

var configuredOrigins =
    builder.Configuration["Cors:AllowedOrigins"];

var allowedOrigins = new List<string>
{
    "http://localhost:8081",
    "http://localhost:19006",
    "http://localhost:3000",
    "http://127.0.0.1:8081"
};

if (!string.IsNullOrWhiteSpace(configuredOrigins))
{
    allowedOrigins.AddRange(
        configuredOrigins.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries));
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("NutriFitoraCors", policy =>
    {
        policy.WithOrigins(
                allowedOrigins
                    .Select(origin => origin.TrimEnd('/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// =====================================================
// 5. JWT configuration
// =====================================================

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "Missing Jwt:Key configuration.");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must contain at least 32 UTF-8 bytes.");
}

var jwtIssuer =
    builder.Configuration["Jwt:Issuer"]
    ?? "Nutritionist.Api";

var jwtAudience =
    builder.Configuration["Jwt:Audience"]
    ?? "Nutritionist.Pwa";

var signingKey = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(
        JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,

                ValidateAudience = true,
                ValidAudience = jwtAudience,

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,

                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
    });

builder.Services.AddAuthorization();

// =====================================================
// 6. Build application
// =====================================================

var app = builder.Build();

// =====================================================
// 7. Exception handling
// =====================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exception = context.Features.Get<
                Microsoft.AspNetCore.Diagnostics
                    .IExceptionHandlerFeature>()?.Error;

            var logger = context.RequestServices
                .GetRequiredService<ILogger<Program>>();

            if (exception != null)
            {
                logger.LogError(
                    exception,
                    "Unhandled exception on {Path}",
                    context.Request.Path);
            }

            context.Response.StatusCode = 500;

            await context.Response.WriteAsJsonAsync(
                new
                {
                    message = "Internal server error."
                });
        });
    });
}

// =====================================================
// 8. Swagger
// =====================================================

// Swagger is enabled for development and deployment.
// Consider restricting it before public production use.

app.UseSwagger();
app.UseSwaggerUI();

// =====================================================
// 9. Middleware
// =====================================================

// Render terminates HTTPS at its reverse proxy.
// Do not redirect internal HTTP traffic here.

app.UseRouting();

app.UseCors("NutriFitoraCors");

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

// =====================================================
// 10. API homepage
// =====================================================

app.MapGet("/", () => Results.Ok(new
{
    message = "NutriFitora API is running successfully",
    status = "Online",
    version = "1.0"
}));

// =====================================================
// 11. Health endpoint
// =====================================================

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "NutriFitora API",
    environment = app.Environment.EnvironmentName
}));

// =====================================================
// 12. Start application
// =====================================================

app.Run();
