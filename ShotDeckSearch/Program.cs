
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

// Database connection (scoped – NOT eagerly opened so controllers can
// open / close per-method via the existing mustClose pattern, returning
// connections to the pool immediately instead of holding them for the
// entire request lifetime).
builder.Services.AddScoped<NpgsqlConnection>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connStr = config.GetConnectionString("DefaultConnection")
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
