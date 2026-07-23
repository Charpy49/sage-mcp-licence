using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Sage100Mcp.LicenseServer.Licensing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LicenseRepository>();

var app = builder.Build();

var adminKey = app.Configuration["Licensing:AdminApiKey"];
if (string.IsNullOrWhiteSpace(adminKey))
{
    throw new InvalidOperationException(
        "Licensing:AdminApiKey doit être configuré (appsettings.json ou variable d'environnement Licensing__AdminApiKey).");
}

// --- Endpoint public, appelé par les serveurs MCP déployés chez les clients ---
app.MapPost("/api/license/validate", async Task<Ok<ValidateResponse>> (
    ValidateRequest request, LicenseRepository repo, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey))
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "missing_key"));
    }

    var license = await repo.FindByKeyAsync(request.LicenseKey, ct);
    if (license is null)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "unknown_key"));
    }
    if (license.IsRevoked)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "revoked", ClientName: license.ClientName));
    }
    if (license.ExpiresAtUtc < DateTimeOffset.UtcNow)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "expired", ClientName: license.ClientName,
            ExpiresAtUtc: license.ExpiresAtUtc));
    }

    await repo.RecordValidationAsync(license.Id, http.Connection.RemoteIpAddress?.ToString(), ct);

    return TypedResults.Ok(new ValidateResponse(true, ClientName: license.ClientName,
        ExpiresAtUtc: license.ExpiresAtUtc, AllowedTools: license.AllowedTools));
});

// --- Endpoints d'administration, protégés par une clé partagée ---
var admin = app.MapGroup("/api/admin/licenses").AddEndpointFilter(async (context, next) =>
{
    var httpContext = context.HttpContext;
    if (!httpContext.Request.Headers.TryGetValue("X-Admin-Key", out var provided) ||
        !FixedTimeEquals(provided.ToString(), adminKey))
    {
        return Results.Unauthorized();
    }
    return await next(context);
});

admin.MapPost("/", async Task<Created<CreateLicenseResponse>> (
    CreateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    var key = LicenseKeyGenerator.Generate();
    var record = await repo.CreateAsync(request.ClientName, key, request.ExpiresAtUtc, request.AllowedTools, ct);
    var response = new CreateLicenseResponse(record.Id, key, record.ClientName, record.ExpiresAtUtc, record.AllowedTools);
    return TypedResults.Created($"/api/admin/licenses/{record.Id}", response);
});

admin.MapGet("/", async (LicenseRepository repo, CancellationToken ct) =>
    (await repo.ListAsync(ct)).Select(LicenseSummary.From));

admin.MapGet("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindByIdAsync(id, ct);
    return record is null ? TypedResults.NotFound() : TypedResults.Ok(LicenseSummary.From(record));
});

admin.MapPut("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, UpdateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, request.ClientName, request.ExpiresAtUtc, request.IsRevoked,
        request.AllowedTools, request.ClearAllowedTools ?? false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
});

admin.MapPost("/{id}/revoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: true, null, false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
});

admin.MapPost("/{id}/unrevoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: false, null, false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
});

admin.MapDelete("/{id}", async Task<Results<NoContent, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
    await repo.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

app.Run();

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
