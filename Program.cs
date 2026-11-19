using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RobotFactoryBot;

var builder = WebApplication.CreateBuilder(args);

// Настройка JSON-сериализации для соответствия OpenAPI спецификации
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

// Настройка CORS для локальной отладки в браузерной песочнице
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();   // отдаёт wwwroot/index.html на /
app.UseStaticFiles();    // отдаёт wwwroot/*
// Обязательный заголовок для Chrome/Edge Private Network Access (HTTPS песочница -> http://localhost)
app.Use(async (context, next) =>
{
    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

// Глобальный перехватчик ошибок: арена требует валидный JSON даже при сбое
app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        // Если упало событие, безопаснее вернуть пустой список команд, чтобы не прерывать прогон
        if (context.Request.Path.Value?.EndsWith("/event") == true)
        {
            await context.Response.WriteAsJsonAsync(new EventResponse());
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new OkResponse(true));
        }
    });
});

// — Эндпоинты API v1 —

app.MapGet("/v1/health", () => Results.Ok(new HealthResponse("ok")));

app.MapGet("/v1/info", () => Results.Ok(new InfoResponse(
    Name: "BayesianFactoryBot",
    Version: "1.0.0",
    Project: "factory",
    SpecVersion: new[] { "v1" }
)));

app.MapPost("/v1/runs/{runId}/start", ([FromRoute] string runId, [FromBody] StartRequest? request) =>
{
    if (request != null)
    {
        if (string.IsNullOrEmpty(request.RunId))
            request.RunId = runId;

        FactoryEngine.StartRun(request);
    }

    return Results.Ok(new OkResponse(true));
});

app.MapPost("/v1/runs/{runId}/event", ([FromRoute] string runId, [FromBody] EventRequest? request) =>
{
    if (request == null)
    {
        return Results.Ok(new EventResponse());
    }

    try
    {
        var response = FactoryEngine.HandleEvent(runId, request);
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Run {runId} | Seq {request.Seq}] Error: {ex.Message}");
        return Results.Ok(new EventResponse());
    }
});

app.MapGet("/v1/runs/{runId}/debug", ([FromRoute] string runId) =>
{
    var debug = FactoryEngine.GetDebug(runId);
    return debug != null ? Results.Ok(debug) : Results.NotFound();
});

app.MapPost("/v1/runs/{runId}/finish", ([FromRoute] string runId, [FromBody] FinishRequest? request) =>
{
    if (request != null)
    {
        Console.WriteLine($"[Run {runId}] Finished | Profit: {request.Profit:F2} | Sold: {request.Sold} | Refused: {request.Refused}");
    }

    FactoryEngine.FinishRun(runId);
    return Results.Ok(new OkResponse(true));
});

// Запуск на порту 4001 (по умолчанию для песочницы)
var port = Environment.GetEnvironmentVariable("PORT") ?? "4001";
app.Run($"http://0.0.0.0:{port}");