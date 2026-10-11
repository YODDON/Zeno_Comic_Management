using ComicManagement.Persistence;

var builder = WebApplication.CreateBuilder(args);

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' is required.");

builder.Services.AddPersistence(databaseConnectionString);

var app = builder.Build();

app.Run();
