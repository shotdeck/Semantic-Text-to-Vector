
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

// Lazy<NpgsqlConnection>: opens on first `.Value` access, NOT during DI
// resolution. SearchController + KeywordCache use this so they keep the
// "already open" fast path without having to call OpenAsync themselves.
// Because Open() happens lazily (inside the action / service method), any
// failure now surfaces through our UseExceptionHandler middleware instead of
// being thrown during controller activation as a blank 500.
builder.Services.AddScoped<Lazy<NpgsqlConnection>>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new Lazy<NpgsqlConnection>(() =>
    {
        var conn = new NpgsqlConnection(ResolveConnectionString(config));
        conn.Open();
        return conn;
    });
});

// Plain NpgsqlConnection: returns a CLOSED connection. Admin controllers
// (SynonymsAdminController, UnwantedWordsController) already open it
// themselves via the `mustClose` pattern with OpenAsync(CancellationToken),
// which is what we want — failures surface cleanly and are cancellable.
// IMPORTANT: do NOT forward this to Lazy<NpgsqlConnection>.Value — that
// would re-introduce the eager-Open-during-DI-resolution bug that produced
// blank 500s when the SSH tunnel or pool was unhealthy.
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
