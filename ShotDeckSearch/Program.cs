
using Npgsql;
using ShotDeck.Keywords;


var builder = WebApplication.CreateBuilder(args);

// Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // Azure injects this automatically if you enabled App Insights in the Portal
    options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
});




// SSH tunnel (optional, you had this)
builder.Services.AddHostedService<SshTunnelService>();

// Database connection (scoped). IMPORTANT: do NOT call Open() here.
// Opening during DI resolution means that any failure (e.g. dead SSH tunnel,
// stale pooled socket) becomes a blank 500 that is thrown before any
// controller/middleware runs, making it impossible to return a useful error
// body or catch the failure with IExceptionHandler. Callers open the
// connection lazily (every controller already does this via the `mustClose`
// pattern: `if (_connection.State != ConnectionState.Open) await _connection.OpenAsync(...)`).
builder.Services.AddScoped<NpgsqlConnection>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connStr = config.GetConnectionString("DefaultConnection")
        ?? config["ConnectionStrings:Default"]
        ?? throw new InvalidOperationException("DefaultConnection is not configured.");

    return new NpgsqlConnection(connStr);
});

// Keyword caching (singleton) - also includes unwanted words caching
builder.Services.AddSingleton<IKeywordCacheService, KeywordCacheService>();

builder.Services.AddHttpClient();

// Keyword warmup at startup (singleton, creates scope manually)
builder.Services.AddHostedService<KeywordWarmupService>();

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowAll", p =>
        p.AllowAnyOrigin()
         .AllowAnyHeader()
         .AllowAnyMethod());
});

builder.Services.AddControllers();

var app = builder.Build();

// Surface unhandled exceptions as JSON instead of a blank 500 so clients
// (and browser devtools) see what actually went wrong.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("UnhandledException");
        logger.LogError(ex, "Unhandled exception for {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var payload = new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            title = "An unexpected error occurred.",
            status = 500,
            detail = ex?.Message,
            exceptionType = ex?.GetType().FullName,
            traceId = context.TraceIdentifier
        };
        await context.Response.WriteAsJsonAsync(payload);
    });
});

app.UseCors("AllowAll");

app.UseStaticFiles();
app.UseSwagger();

app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger"; // <-- final route
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ShotDeck API v1");
    c.DocumentTitle = "ShotDeck API Docs";
    c.InjectStylesheet("/swagger/shotdeck.css?v=600");
    c.InjectJavascript("/swagger/shotdeck.js?v=600");
    c.HeadContent += "<link rel=\"icon\" href=\"/swagger/shotdeck-logo.png\">";
    //c.HeadContent += "<style>.topbar{background:#00ff88!important}</style>";
});



app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
