using ComicManagement.Api.Authentication;
using ComicManagement.Identity.Authentication;
using ComicManagement.Persistence;
using ComicManagement.Shared.Identity;

var builder = WebApplication.CreateBuilder(args);

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' is required.");
var authenticationSettings = IdentityAuthenticationSettings.FromConfiguration(
    builder.Configuration,
    builder.Environment.ContentRootPath);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddPersistence(databaseConnectionString);
builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddIdentityAuthentication(authenticationSettings);
builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddOutboxWorker();

var app = builder.Build();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
