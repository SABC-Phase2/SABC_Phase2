using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using Hangfire;
using SABC_Phase2.Services;
using QuestPDF.Infrastructure; // Add this at the top

// Set the QuestPDF license type before building the app
QuestPDF.Settings.License = LicenseType.Community;


var builder = WebApplication.CreateBuilder(args);

// ---------------------------
// Add services to the container
// ---------------------------

// MVC Controllers and Razor Views
builder.Services.AddControllersWithViews();

// EF Core with SQL Server using connection string from appsettings.json
builder.Services.AddDbContext<Phase2Context>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Phase2ContextConnection")));

// Hangfire: Configure background job storage using SQL Server
builder.Services.AddHangfire(config =>
    config.UseSqlServerStorage(builder.Configuration.GetConnectionString("Phase2ContextConnection")));

// Register Hangfire's background job server
builder.Services.AddHangfireServer();

// Register your custom close publishing service for DI
builder.Services.AddScoped<ITenderClosingService, TenderClosingService>();


// Register your custom tender publishing service for DI
builder.Services.AddScoped<ITenderPublishingService, TenderPublishingService>();

builder.Services.AddTransient<TenderReportPdfService>();
var app = builder.Build();

// ---------------------------
// Configure the HTTP request pipeline
// ---------------------------

if (!app.Environment.IsDevelopment())
{
    // Production: Use global error handler and enforce HTTPS
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();   // Redirect HTTP to HTTPS
app.UseStaticFiles();        // Serve static files from wwwroot

app.UseRouting();
app.UseAuthorization();

// ---------------------------
// Hangfire Dashboard
// ---------------------------
// Provides a UI for monitoring background jobs.
// Consider adding authentication to this route in production.
app.UseHangfireDashboard();

// ---------------------------
// Background Job Scheduling
// ---------------------------
// This registers a recurring job that runs every minute to publish scheduled tenders.
RecurringJob.AddOrUpdate<ITenderPublishingService>(
    "publish-scheduled-tenders",                        // Job ID
    service => service.PublishScheduledTendersAsync(),  // Job method
    Cron.Minutely);                                     // Schedule: every minute

// This registers a recurring job that runs every 5 minute to publish open tenders to closed tenders.
RecurringJob.AddOrUpdate<ITenderClosingService>(
    "close-expired-tenders",
    service => service.CloseExpiredTendersAsync(),
    "*/5 * * * *" // every 5 minutes
);

// ---------------------------
// Configure default route for MVC
// ---------------------------
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=TenderAdmin}/{action=Index}/{id?}");

app.Run();
