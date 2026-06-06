
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

static string ResolveConnectionString(IConfiguration config) =>
    config.GetConnectionString("DefaultConnection")
        ?? config["ConnectionStrings:Default"]
        ?? throw new InvalidOperationException("DefaultConnection is not configured.");

// Database connection (scoped, CLOSED). All controllers open the connection
// themselves via the mustClose pattern (OpenAsync → use → CloseAsync in
// finally), returning it to the pool immediately after each operation.
// Do NOT open the connection here — that holds a pool slot for the entire
// request lifetime and caused pool-exhaustion 500s under load.
builder.Services.AddScoped<NpgsqlConnection>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new NpgsqlConnection(ResolveConnectionString(config));
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
