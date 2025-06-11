using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using Hangfire;
using SABC_Phase2.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<Phase2Context>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Phase2ContextConnection")));

// Configure Hangfire to use SQL Server storage
builder.Services.AddHangfire(config =>
    config.UseSqlServerStorage(builder.Configuration.GetConnectionString("Phase2ContextConnection")));
builder.Services.AddHangfireServer();

// Register your custom publishing service
builder.Services.AddScoped<ITenderPublishingService, TenderPublishingService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

// Hangfire Dashboard (optional, for monitoring jobs)
app.UseHangfireDashboard(); // You can add options or protect it with authorization if needed

// Schedule the background job to run every minute
RecurringJob.AddOrUpdate<ITenderPublishingService>(
    "publish-scheduled-tenders",
    service => service.PublishScheduledTendersAsync(),
    Cron.Minutely); // You can change this to Cron.Hourly or a custom expression

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
